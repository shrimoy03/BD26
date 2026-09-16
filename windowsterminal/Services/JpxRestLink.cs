using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MerchantTerminal.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace MerchantTerminal.Services;

/// <summary>
/// PAX-agreed flow (BloomingdaleDemo sequence diagram): the register drives
/// the terminal through the PxRetailer REST service, using form variables as
/// mailboxes.
///
///   startup  -> POST /setVariable BOOL.FOREGROUND=true; POST /subscribe
///   sale     -> POST /sendBatchCmd [SetVariable START_TRANS_REQ_DATA=json,
///               DisplayForm StartTransaction]
///   result   <- notify IS_TRANS_STARTED=2 -> GET /getVariable TRANS_RESULT
///
/// The REST endpoint is JPxSerialServer on this PC (USB link to the terminal)
/// or PXRRS on the terminal itself over Ethernet/Wi-Fi â€” same sequence, per
/// PAX's "Assumptions &amp; Clarifications".
///
/// Configured from <see cref="LinkConfig"/> â€” the setup screen (F9) writes the
/// terminal IP to %APPDATA%\MerchantTerminal\settings.json, and the documented
/// POS_* environment variables still override it. See <see cref="PosSettings"/>.
/// </summary>
public sealed class JpxRestLink : ITerminalLink
{
    // Names from PAX's sequence diagram. PxDesigner variables are type-prefixed
    // (BOOL./STR./INT./LIST.), so the diagram's bare "FOREGROUND" is really
    // BOOL.FOREGROUND â€” verified against a live A3700 (PxRetailer 2.01.16):
    // getVariable BOOL.FOREGROUND returns "true" and setVariable succeeds,
    // while the unprefixed name is rejected as unknown.
    //
    // The remaining three belong to PAX's custom Bloomingdale's package and do
    // not exist on a stock PxRetail install; see the stock-form fallback in
    // SendAsync. TODO(PAX): confirm their prefixes when that package ships.
    private const string VarRequest = "START_TRANS_REQ_DATA";
    private const string VarResult = "TRANS_RESULT";

    // Handshake states carried in the state mailbox â€” the pollable equivalent
    // of the diagram's IS_TRANS_STARTED notify. PXRRS rejects setVariable with
    // an empty value, so idle is "0" rather than blank.
    private const string StateIdle = "0";
    private const string StateOrderReady = "1";
    private const string StateResultReady = "2";

    /// <summary>Tracks which form PxRetailer is currently showing.</summary>
    private const string VarNextScreen = "SYS.STR.NEXTSCREEN";
    private const string VarForeground = "BOOL.FOREGROUND";
    private const string EventTransState = "IS_TRANS_STARTED";
    private const string FormStart = "StartTransaction";
    /// <summary>Stock cart screen — shown once the basket has at least one line.</summary>
    private const string FormCartIdle = "BackgroundScreen";
    /// <summary>Stock secure idle screen — the default while the basket is empty.</summary>
    private const string FormSecureIdle = "SecureBackgroundScreen";

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly string _baseUrl;
    private readonly string _notifyUrl;
    private readonly string _startForm;
    private readonly string _notifyCertPath;
    private readonly string _notifyCertPassword;
    private readonly string _triggerForm;
    private readonly string _triggerVar;
    private readonly string _triggerMethod;
    private readonly bool _manageSubscription;
    private readonly string _requestVar;
    private readonly string _stateVar;
    private readonly string _resultVar;
    private string? _lastScreen;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private WebApplication? _notifyServer;
    private volatile bool _connected;
    private volatile bool _subscribed;
    private string? _lastUptime;
    private int _cyclesSinceSubscribe;
    private CancellationTokenSource? _resultPoll;
    private bool _phase2Unavailable;

    /// <summary>
    /// True once an inbound notify has proven terminalâ†’register delivery works.
    /// Until then (and again after a re-park or terminal restart) the trigger
    /// poll runs at the fast cadence, so notify never has to be trusted before
    /// it has delivered something.
    /// </summary>
    private volatile bool _notifyHealthy;

    /// <summary>Whether the live subscription points at the real callback (vs parked).</summary>
    private volatile bool _subscribedReal;

    /// <summary>
    /// Watchdog verdict: the real callback poisoned PXRRS with delivery stalls,
    /// so stay parked for the rest of the session (restart the app to retry).
    /// </summary>
    private volatile bool _parkedByWatchdog;

    private readonly object _tenderLock = new();
    private readonly Queue<long> _stalledCalls = new();
    private long _lastTenderAtTick;
    private string? _lastTenderMethod;

    private static bool ForceRealSubscription =>
        Environment.GetEnvironmentVariable("POS_NOTIFY_SUBSCRIBE_REAL") == "1";

    /// <summary>Poll cycles (5s each) between re-claiming the notify callback.</summary>
    private const int ResubscribeCycles = 12;

    public JpxRestLink(LinkConfig config)
    {
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _notifyUrl = config.NotifyUrl;
        _startForm = string.IsNullOrWhiteSpace(config.StartForm) ? FormStart : config.StartForm;
        _notifyCertPath = config.NotifyCertPath;
        _notifyCertPassword = config.NotifyCertPassword;
        _triggerForm = config.TriggerForm?.Trim() ?? "";
        _triggerVar = config.TriggerVariable?.Trim() ?? "";
        _triggerMethod = config.TriggerMethod;
        _manageSubscription = config.ManageSubscription;
        _requestVar = config.RequestVariable;
        _stateVar = config.StateVariable;
        _resultVar = config.ResultVariable;
        // 10s was not enough: the A3700 regularly takes longer than that on
        // setVariable and the listBox calls, and a timed-out handover leaves the
        // sale half-published.
        _http = new HttpClient(BuildHandler(config)) { Timeout = TimeSpan.FromSeconds(30) };
        // Fresh connection per request. PXRRS's embedded NanoHTTPD reaps idle
        // keep-alive sockets on its own ~10s timer; reusing a pooled connection
        // it is concurrently abandoning hangs the request for exactly that
        // timeout (measured: sequential curl with fresh connections never
        // stalls while the pooled client intermittently takes 10.1s). The
        // extra mTLS handshake costs ~80ms on the LAN â€” invisible next to a
        // guaranteed absence of 10s outliers.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <summary>Bundled fallback when no cert path is configured.</summary>
    public static string DefaultCertPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "certs", "pxrrs-integration-client.p12");

    /// <summary>Server identity for our own https notify listener.</summary>
    public static string DefaultNotifyCertPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "certs", "pxrrs-notify-server.p12");

    private System.Security.Cryptography.X509Certificates.X509Certificate2? LoadNotifyCertificate()
    {
        var path = string.IsNullOrWhiteSpace(_notifyCertPath) ? DefaultNotifyCertPath : _notifyCertPath;
        if (!System.IO.File.Exists(path))
        {
            Console.WriteLine($"[JpxRestLink] notify server certificate not found at {path}");
            return null;
        }

        try
        {
            return System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(path, _notifyCertPassword);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[JpxRestLink] could not load notify certificate {path}: {e.Message}");
            return null;
        }
    }

    public static string ResolveCertPath(LinkConfig config) =>
        string.IsNullOrWhiteSpace(config.CertPath) ? DefaultCertPath : config.CertPath.Trim();

    /// <summary>
    /// PXRRS on the terminal serves HTTPS with the PAX self-signed chain and
    /// REQUIRES a client certificate (mutual TLS) â€” plain requests are dropped
    /// mid-handshake. JPxSerialServer on localhost stays plain HTTP.
    /// </summary>
    private static HttpMessageHandler BuildHandler(LinkConfig config)
    {
        var handler = new HttpClientHandler();
        if (!config.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)) return handler;

        // PAX chain is rooted at pxrrs-ca.pax.us (self-signed) â€” trust it for
        // the demo instead of installing the root into the OS store.
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var p12 = ResolveCertPath(config);
        if (System.IO.File.Exists(p12))
        {
            try
            {
                handler.ClientCertificates.Add(
                    System.Security.Cryptography.X509Certificates.X509CertificateLoader
                        .LoadPkcs12FromFile(p12, config.CertPassword));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[JpxRestLink] WARNING: could not load {p12}: {e.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[JpxRestLink] WARNING: client cert not found at {p12} â€” PXRRS will reject us");
        }

        return handler;
    }

    public bool IsConnected => _connected;

    /// <summary>
    /// One-shot reachability check for the setup screen. Distinguishes the
    /// failures that actually happen in the field: wrong IP (timeout), nothing
    /// listening (refused), and missing/rejected mTLS cert (handshake).
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(
        LinkConfig config, CancellationToken ct = default)
    {
        var https = config.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase);
        var certPath = ResolveCertPath(config);
        if (https && !System.IO.File.Exists(certPath))
        {
            return (false, $"No client certificate at {certPath} â€” PXRRS requires mutual TLS. See certs/README.md.");
        }

        using var http = new HttpClient(BuildHandler(config)) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            var response = await http.PostAsync($"{config.BaseUrl.TrimEnd('/')}/getPackageList", null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Terminal answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return (true, "Connected â€” terminal answered getPackageList.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Timed out after 6s. Check the IP and that the terminal is on this Wi-Fi network.");
        }
        catch (UriFormatException)
        {
            return (false, $"'{config.BaseUrl}' is not a valid address.");
        }
        catch (HttpRequestException e)
        {
            var inner = e.InnerException;
            if (inner is System.Security.Authentication.AuthenticationException)
            {
                return (false, $"TLS handshake failed â€” PXRRS rejected our client certificate ({certPath}).");
            }

            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused =>
                        (false, "Connection refused â€” nothing is listening on that port. Is PXRRS running on the terminal?"),
                    System.Net.Sockets.SocketError.HostUnreachable or
                    System.Net.Sockets.SocketError.NetworkUnreachable =>
                        (false, "Host unreachable â€” the terminal is not on this subnet."),
                    _ => (false, socket.Message),
                };
            }

            return (false, inner is null ? e.Message : $"{e.Message} â€” {inner.Message}");
        }
    }

    public async Task StartAsync()
    {
        await StartNotifyServerAsync();
        _ = Task.Run(() => MaintainLinkAsync(_cts.Token));

        if (_triggerForm.Length > 0 || _triggerVar.Length > 0)
        {
            _ = Task.Run(() => WatchTriggerFormAsync(_cts.Token));
            if (_triggerVar.Length > 0)
            {
                Console.WriteLine($"[JpxRestLink] watching {_triggerVar} for a biometric tender");
            }

            if (_triggerForm.Length > 0)
            {
                Console.WriteLine(
                    $"[JpxRestLink] watching for form '{_triggerForm}' as a {_triggerMethod} tender");
            }
        }

        Console.WriteLine($"[JpxRestLink] driving {_baseUrl}, notify at {_notifyUrl}");
    }

    /// <summary>
    /// Fallback for the notify path: poll the trigger variable (and optionally
    /// the displayed form) that the tender buttons write. Notify delivery DOES
    /// work (the earlier "never dispatches" verdict was subscription contention
    /// â€” PXRRS holds one subscriber slot, last one wins), but it has real
    /// failure modes: a stolen slot, a firewalled callback, a terminal restart.
    /// So the poll runs fast (400ms) until an inbound notify proves delivery,
    /// then relaxes to a 2s safety net; <see cref="RaiseTender"/> dedupes the
    /// overlap. Form watching fires on the transition into the form, not while
    /// it stays there, so holding on the screen does not restart the tender.
    /// </summary>
    private async Task WatchTriggerFormAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_notifyHealthy ? 2000 : 400), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_connected) continue;

            // While a tender is in flight the result poll owns the mailbox â€”
            // pausing the trigger poll halves the request pressure on PXRRS
            // (which serializes) and avoids double-reading the shared variable.
            if (_resultPoll is not null) continue;

            // Preferred route: the button carries a PxDesigner SetVariable
            // action writing "face"/"palm" here. Unlike FireEvent this needs no
            // notification from PXRRS, so it works on this terminal today.
            if (_triggerVar.Length > 0 && await ReadBiometricTriggerAsync()) continue;

            if (_triggerForm.Length == 0) continue;

            string? screen;
            try
            {
                screen = await GetVariableAsync(VarNextScreen);
            }
            catch (Exception)
            {
                continue;
            }

            if (screen is null || screen == _lastScreen) continue;

            var previous = _lastScreen;
            _lastScreen = screen;

            // Ignore the very first reading: we do not know whether the
            // terminal was already sitting on the trigger form before we
            // started watching.
            if (previous is null) continue;

            if (!screen.Equals(_triggerForm, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine($"[JpxRestLink] '{screen}' displayed â€” treating as {_triggerMethod} tender");
            RaiseTender(_triggerMethod, "form watch");
        }
    }

    private async Task StartNotifyServerAsync()
    {
        var uri = new Uri(_notifyUrl);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // Kestrel drops a connection whose TLS handshake fails without a word,
        // so a terminal that cannot negotiate with us looks exactly like one
        // that never called. Surface its connection diagnostics â€” this is the
        // only place the difference is visible.
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Debug);

        // PXRRS will not post results to a plain-http replyURL â€” PAX's own
        // RetailDemoApplication subscribes with an https one. Serve TLS with
        // the PAX server identity when the advertised URL says https.
        var useTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(uri.Port, listen =>
            {
                // HTTP/1.1 only. Under TLS, Kestrel otherwise advertises h2 over
                // ALPN; a Java client that selects it and then fails the
                // exchange is indistinguishable from nothing arriving.
                listen.Protocols = HttpProtocols.Http1;

                if (!useTls) return;
                var cert = LoadNotifyCertificate();
                if (cert is null)
                {
                    Console.WriteLine("[JpxRestLink] WARNING: https notify requested but no server certificate â€” falling back to http");
                    return;
                }

                listen.UseHttps(https =>
                {
                    https.ServerCertificate = cert;

                    // Pin TLS 1.2. Kestrel otherwise prefers 1.3, and the
                    // terminal's Java client fails that handshake â€” PAX's own
                    // Node sample negotiates 1.2 and does receive callbacks
                    // with this exact certificate.
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                });
            });
        });

        _notifyServer = builder.Build();

        // Terminal middleware rather than MapPost(path): accept any method and
        // any path. PXRRS's exact callback shape is not documented, and a
        // mismatch on the verb or path would otherwise be rejected with no
        // trace at all â€” which is indistinguishable from nothing arriving.
        // Everything inbound is logged so the real shape is visible.
        _notifyServer.Run(async context =>
        {
            var request = context.Request;
            using var reader = new System.IO.StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();

            // Answer before processing anything. PXRRS delivers notifies
            // synchronously from the same queue that serves our REST calls, so
            // every millisecond spent inside this handler stalls the terminal
            // for everyone â€” a slow subscriber is indistinguishable from an
            // unreachable one. PAX's node-ECR sample answers a bare 200 "ok"
            // and that is the shape the terminal is tested against.
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("ok");
            await context.Response.CompleteAsync();

            Console.WriteLine(
                $"[JpxRestLink] inbound {request.Method} {request.Path}{request.QueryString} " +
                $"body={(string.IsNullOrWhiteSpace(body) ? "<empty>" : body)}");

            var payload = body;
            if (string.IsNullOrWhiteSpace(payload))
            {
                // Some senders put the event in the query string instead of a body.
                var name = request.Query["name"].ToString();
                var value = request.Query["value"].ToString();
                if (string.IsNullOrWhiteSpace(name)) return;
                payload = JsonSerializer.Serialize(new { name, value });
            }

            // Off the request thread: MessageReceived handlers run VM code.
            _ = Task.Run(() => HandleNotify(payload));
        });

        await _notifyServer.StartAsync();
    }

    private void HandleNotify(string body)
    {
        Console.WriteLine($"[JpxRestLink] notify: {body}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = doc.RootElement.TryGetProperty("value", out var v) ? v.ToString() : null;

            // Relax the fast trigger poll only once a form EVENT arrives this
            // way â€” that is the traffic the poll substitutes for. Anything else
            // inbound (async EMV command responses, putFile results, test
            // curls) proves transport but NOT that the tender buttons carry
            // FireEvent actions; on a package where they only write the
            // SetVariable trigger, relaxing on those would add up to 2s of
            // tender latency with nothing to replace it (observed 2026-09-15:
            // the bench package's Face button fires no event at all).
            if (!_notifyHealthy && name is EventTransState or "PAYMENTSTATUS")
            {
                _notifyHealthy = true;
                Console.WriteLine(
                    "[JpxRestLink] form events arriving via notify â€” trigger poll relaxed to fallback cadence");
            }

            // IS_TRANS_STARTED=2 -> the terminal published TRANS_RESULT.
            if (name == EventTransState && value == "2")
            {
                StopResultPolling(); // the notify beat the mailbox poll to it
                _ = Task.Run(FetchResultAsync);
            }

            // A tender button on the PxRetailer form. PAX's custom package
            // wires the Face button as FireEvent IS_TRANS_STARTED="face"
            // (the same event name as the handshake flag, but with the tender
            // as the value instead of 0/1/2); older builds used a separate
            // PAYMENTSTATUS event. Accept both and surface to the VM so it can
            // route the sale to WinkPay or the EMV flow.
            var isTender = name == "PAYMENTSTATUS"
                || (name == EventTransState && value?.Trim().ToLowerInvariant()
                        is "face" or "palm" or "card" or "credit" or "debit");
            if (isTender && !string.IsNullOrWhiteSpace(value))
            {
                // The same button press also wrote the SetVariable trigger
                // mailbox; consume it so the fallback poll cannot re-raise the
                // tender after we already handled it here. NOT when the trigger
                // doubles as the state mailbox (stock package: both are
                // STR.GENERIC_2): the handoff below replaces "face" with "1" for
                // WinkPay itself, and this fire-and-forget "none" lands seconds
                // later on top of that flag — observed on the A3700 after every
                // notify-driven sale. The poll's TenderInFlight guard stops the
                // re-raise in that case.
                if (_triggerVar.Length > 0 && _triggerVar != _stateVar)
                {
                    _ = Task.Run(() => SetVariableAsync(_triggerVar, "none"));
                }

                RaiseTender(value.Trim().ToUpperInvariant(), "notify");
            }
        }
        catch (JsonException)
        {
            // Not an event object; ignore.
        }
    }

    /// <summary>
    /// The notify callback and the fallback trigger poll can both see the same
    /// button press (FireEvent and the SetVariable trigger ride on one button);
    /// whichever route arrives first wins and the echo inside the window is
    /// dropped. A genuine repeat press lands outside it â€” while a tender is in
    /// flight the trigger poll is paused anyway.
    /// </summary>
    /// <summary>
    /// True when <paramref name="method"/> was raised within the dedupe window
    /// or a handoff's result poll is running — i.e. some other source (notify,
    /// an earlier poll) already owns this tender.
    /// </summary>
    private bool TenderInFlight(string method)
    {
        if (_resultPoll is not null) return true;
        lock (_tenderLock)
        {
            return _lastTenderMethod == method
                && Environment.TickCount64 - _lastTenderAtTick < 5000;
        }
    }

    private void RaiseTender(string method, string source)
    {
        var now = Environment.TickCount64;
        lock (_tenderLock)
        {
            if (_lastTenderMethod == method && now - _lastTenderAtTick < 5000)
            {
                Console.WriteLine($"[JpxRestLink] duplicate {method} tender via {source} ignored");
                return;
            }

            _lastTenderMethod = method;
            _lastTenderAtTick = now;
        }

        Console.WriteLine($"[JpxRestLink] tender selected via {source}: {method}");
        MessageReceived?.Invoke(new PosMessage
        {
            Type = PosMessageTypes.TenderSelected,
            Method = method,
        });
    }

    private async Task FetchResultAsync()
    {
        try
        {
            var json = await GetVariableAsync(VarResult);
            if (json is null) return;
            var message = PosJson.Deserialize(json);
            if (message is not null) MessageReceived?.Invoke(message);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[JpxRestLink] failed to fetch {VarResult}: {e.Message}");
        }
    }

    /// <summary>Init per the sequence diagram, then poll for liveness.</summary>
    private async Task MaintainLinkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var alive = await ProbeAsync();

            if (alive && !_subscribed)
            {
                // PxRetailer is the default start config on the demo bench:
                // bring it to the foreground on connect (it sometimes launches
                // behind other apps). NEVER while a tender is in flight though â€”
                // a link blip mid-capture (PXRRS's periodic ~10s lockup can fail
                // one liveness probe) used to re-run this init and yank
                // PxRetailer in front of the WinkPay camera.
                var foregroundOk = _resultPoll is not null || await SetForegroundAsync(true);
                _subscribed = await SubscribeAsync();
                // Subscribing is what actually matters; a package that does not
                // define the foreground flag should not fail the whole link.
                if (!foregroundOk)
                {
                    Console.WriteLine($"[JpxRestLink] note: {VarForeground} not settable on this package");
                }
            }
            else if (alive && ++_cyclesSinceSubscribe >= ResubscribeCycles)
            {
                // Nothing reports that our callback was taken over â€” another
                // client on this machine subscribing with the same identity
                // simply replaces it, and we would keep reporting connected
                // while receiving nothing. Re-claiming it is cheap and
                // idempotent, so do it periodically rather than trusting the
                // original subscribe to hold.
                _cyclesSinceSubscribe = 0;
                await SubscribeAsync();
            }

            var connected = alive && _subscribed;
            if (connected != _connected)
            {
                _connected = connected;
                // Whatever was on the screen before the blip is unknown now —
                // PxRetailer may have restarted with an empty list and its
                // default form. Force the next cart sync to rebuild and
                // re-display rather than append onto rows that are gone.
                // (Flag only — this runs off the send lock, and the row list
                // is owned by SendCartAsync, which clears it on rebuild.)
                _cartNeedsRebuild = true;
                _lastDisplayedForm = null;
                Console.WriteLine($"[JpxRestLink] terminal {(connected ? "connected" : "disconnected")} ({_baseUrl})");
                if (connected) ClientConnected?.Invoke();
                else
                {
                    _subscribed = false; // re-subscribe on reconnect
                    ClientDisconnected?.Invoke();
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One command at a time: the cart render is several sequential REST calls
    /// and a tender command racing into the middle of it makes the terminal
    /// flash through stray screens. Serializing here keeps every screen
    /// transition intentional.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>What we believe PxRetailer currently shows / whether it owns the screen.</summary>
    private string? _lastDisplayedForm;
    private bool _foreground = true;

    public async Task<bool> SendAsync(PosMessage message)
    {
        await _sendLock.WaitAsync();
        try
        {
            return await SendLockedAsync(message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<bool> SendLockedAsync(PosMessage message)
    {
        // START_PAYMENT and CANCEL_PAYMENT both travel in START_TRANS_REQ_DATA;
        // the terminal app reads the JSON "type" to tell them apart. Re-showing
        // StartTransaction re-fires IS_TRANS_STARTED=1 so the terminal always
        // re-reads the variable.
        if (message.Type == PosMessageTypes.DisplayCart)
        {
            // A cart render means no tender is in flight (syncs are held while
            // one is), so any result poll still running is an orphan â€” the
            // outcome arrived over the WebSocket instead of the mailbox. Left
            // alone it hits PXRRS every second forever, and the terminal
            // serializes requests, so everything after the first sale turns
            // sluggish.
            StopResultPolling();
            return await SendCartAsync(message);
        }

        if (message.Type == PosMessageTypes.ShowThanks)
        {
            // Sale resolved â€” end the mailbox result poll (the result came in
            // over the WebSocket). WinkPay is showing its own thank-you page,
            // so no screen change here either: PxRetailer is reclaimed by the
            // next cart sync (the register's new sale), one clean transition
            // later.
            StopResultPolling();
            Console.WriteLine("[JpxRestLink] sale complete â€” leaving the screen to WinkPay until the next sale");
            return true;
        }

        // Manual rescue: PxRetailer sometimes launches into the background;
        // this yanks it back in front of whatever is on the terminal.
        if (message.Type == PosMessageTypes.ShowRetailer)
        {
            return await SetForegroundAsync(true);
        }

        // "Biometric Pay" on the register's checkout: navigate the terminal to
        // the stock payment-options form. The Face/Palm buttons on it fire the
        // IS_TRANS_STARTED tender FireEvent, which comes back through the
        // notify callback (or the trigger-variable poll) and starts the real
        // FACE/PALM flow below. PxRetailer keeps the screen until then.
        //
        // One sendBatchCmd (foreground + displayForm) rather than two POSTs:
        // each round-trip to PXRRS can stall ~10s when a notify delivery times
        // out, so halving them makes the button visibly snappier.
        if (message.Type == PosMessageTypes.StartPayment && message.Method == "BIOMETRIC")
        {
            var biometricBatch = JsonSerializer.Serialize(new object[]
            {
                new
                {
                    commandName = "SetVariable",
                    variables = new[] { new { name = VarForeground, value = "true" } },
                },
                new { commandName = "DisplayForm", formName = PosSettings.StockStartForm },
            });
            // PxRetailer transiently refuses SetVariable/DisplayForm right
            // after being backgrounded (observed while WinkPay still owned the
            // screen) â€” one short retry rides out that window.
            var shown = await PostAsync("/sendBatchCmd", biometricBatch);
            if (!shown)
            {
                Console.WriteLine("[JpxRestLink] payment-options batch refused â€” retrying once");
                await Task.Delay(500);
                shown = await PostAsync("/sendBatchCmd", biometricBatch);
            }
            if (shown)
            {
                _foreground = true;
                _lastDisplayedForm = PosSettings.StockStartForm;
                InvalidateCartRender();
            }
            return shown;
        }

        // A biometric tender is captured by WinkPay, which is a separate Android
        // app. PxRetailer owns the display, so it has to drop the foreground or
        // the customer never sees WinkPay come up.
        if (message.Type == PosMessageTypes.StartPayment && message.Method is "FACE" or "PALM")
        {
            Console.WriteLine($"[JpxRestLink] {message.Method} tender â€” handing the sale to WinkPay");

            // Custom package installed: run the diagram's Phase 2 verbatim â€”
            // one batch publishes the order and shows StartTransaction, and the
            // package raises IS_TRANS_STARTED=1 itself (PXRRS notifies both
            // parties). The register does not touch the state flag. On a stock
            // package this batch fails every time â€” learn that once instead of
            // burning a round-trip (and a possible stall) on every sale.
            if (_startForm == FormStart && !_phase2Unavailable)
            {
                var batch = new object[]
                {
                    new
                    {
                        commandName = "SetVariable",
                        variables = new[] { new { name = VarRequest, value = PosJson.Serialize(message) } },
                    },
                    new { commandName = "DisplayForm", formName = FormStart },
                };
                if (await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(batch)))
                {
                    _lastDisplayedForm = FormStart;
                    InvalidateCartRender();
                    StartResultPolling(); // fallback if the =2 notify never lands
                    var handed = await SetForegroundAsync(false);
                    Console.WriteLine(
                        $"[JpxRestLink] Phase-2 batch sent ({VarRequest} + DisplayForm {FormStart}), PxRetailer backgrounded={handed}");
                    return handed;
                }
                _phase2Unavailable = true;
                Console.WriteLine(
                    "[JpxRestLink] Phase-2 batch failed â€” using the mailbox handshake from now on (custom package not installed)");
            }

            // Stock package: mailboxes stand in for the notify. Publish the
            // order, raise the handshake flag WinkPay polls, and drop
            // PxRetailer's foreground â€” all in one sendBatchCmd. A batch is
            // applied in order, so the flag still lands after the order JSON is
            // readable, and it collapses three PXRRS round-trips (each a chance
            // at a ~10s notify-timeout stall) into one.
            var handoff = new object[]
            {
                new
                {
                    commandName = "SetVariable",
                    variables = new[] { new { name = _requestVar, value = PosJson.Serialize(message) } },
                },
                new
                {
                    commandName = "SetVariable",
                    variables = new[] { new { name = _stateVar, value = StateOrderReady } },
                },
                new
                {
                    commandName = "SetVariable",
                    variables = new[] { new { name = VarForeground, value = "false" } },
                },
            };
            if (!await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(handoff)))
            {
                Console.WriteLine("[JpxRestLink] mailbox handoff batch failed");
                return false;
            }

            _foreground = false;
            InvalidateCartRender(); // WinkPay owns the screen until the next cart sync reclaims it
            StartResultPolling();
            Console.WriteLine(
                $"[JpxRestLink] order published to {_requestVar}, {_stateVar}=1, PxRetailer backgrounded");
            return true;
        }

        if (message.Type is not (PosMessageTypes.StartPayment or PosMessageTypes.CancelPayment))
        {
            return false;
        }

        // Cancelling out of a biometric means WinkPay is on screen; reclaim it
        // before displaying the cancel/idle form below.
        if (message.Type == PosMessageTypes.CancelPayment)
        {
            StopResultPolling();
            await SetForegroundAsync(true);
        }

        // Stock-form mode (custom StartTransaction package not installed yet):
        // CARD tenders jump straight into the EMV prompt, everything else
        // (BD Loyallist Pay) lands on the payment-options page; a cancel
        // drops the terminal back to the idle cart screen.
        var stockMode = _startForm != FormStart;
        var form = message.Type == PosMessageTypes.CancelPayment
            ? (stockMode ? FormCartIdle : FormStart)
            : stockMode && message.Method == "CARD" ? "InsertTapScreen"
            : _startForm;

        // The request mailbox only exists in PAX's custom package. Writing it in
        // stock mode fails every send with "one or more variables could not be
        // set" â€” and since a batch is only OK when every command is, that made
        // an otherwise successful DisplayForm look like a failure.
        var commands = new List<object>();
        if (!stockMode)
        {
            commands.Add(new
            {
                commandName = "SetVariable",
                variables = new[] { new { name = VarRequest, value = PosJson.Serialize(message) } },
            });
        }

        commands.Add(new { commandName = "DisplayForm", formName = form });
        var displayed = await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(commands));
        if (displayed) _lastDisplayedForm = form;
        // Even a cancel back to the cart form counts: the list may have been
        // reset by the form navigation in between, so rebuild on the next sync.
        InvalidateCartRender();
        return displayed;
    }

    /// <summary>Rows as last rendered on the terminal, for the append fast path.</summary>
    private readonly List<string> _renderedRows = new();

    /// <summary>
    /// True whenever LIST.ITEM on the terminal may not match <see cref="_renderedRows"/>:
    /// at startup and on every (re)connect (PxRetailer may have restarted with an
    /// empty list), after any screen other than the cart was shown (form
    /// navigation can re-instantiate the control), and after a failed or
    /// partial cart send. The next sync then clears and rebuilds the list
    /// instead of appending onto rows that may no longer be there — the
    /// append fast path is only taken from a state we positively know.
    /// </summary>
    private bool _cartNeedsRebuild = true;

    /// <summary>Consecutive ListBoxRemoveItem failures; see <see cref="SendCartAsync"/>.</summary>
    private int _cartClearFailures;

    private void InvalidateCartRender()
    {
        _cartNeedsRebuild = true;
        _renderedRows.Clear();
    }

    /// <summary>
    /// Mirror the register basket onto PxRetailer's stock idle screen:
    /// LIST.ITEM holds the line items and STR.SUBTOTAL / STR.TAX /
    /// STR.AMOUNTOK the money row (names from the PxRetailer API doc, present
    /// in the stock PxRetail packages).
    ///
    /// Speed: ListBoxInsertItem and SetVariable batch (verified on the A3700;
    /// ListBoxRemoveItem does not), so the common ring-another-item case is a
    /// single sendBatchCmd appending just the new rows. Only a shrink/edit
    /// falls back to clear-and-rebuild (one extra call). Every sync also
    /// re-asserts FOREGROUND=true inside the same batch â€” free, and it
    /// self-heals the tracked foreground state if PxRetailer slipped behind
    /// another app without us knowing.
    /// </summary>
    private async Task<bool> SendCartAsync(PosMessage message)
    {
        static string Money(long cents) => "$" + (cents / 100m).ToString("N2");
        static string Line(CartLine item)
        {
            var name = item.Name.Length > 30 ? item.Name[..30] : item.Name;
            if (item.Qty > 1) name += $"  x{item.Qty}";
            return name.PadRight(36) + Money(item.AmountCents).PadLeft(10);
        }

        var items = message.Items ?? Array.Empty<CartLine>();
        var rows = items.Select(Line).ToList();

        // Append-only when the rendered rows are a strict prefix of the new
        // ones; anything else (void, qty change, new sale) needs the rebuild.
        // Never append while the terminal's list is in doubt (see
        // _cartNeedsRebuild) — that is how a stale belief turns into a
        // basket with rows missing or doubled after a few transactions.
        var appendOnly = !_cartNeedsRebuild
            && rows.Count >= _renderedRows.Count
            && _renderedRows.SequenceEqual(rows.Take(_renderedRows.Count));

        var ok = true;
        var listKnownEmpty = appendOnly;
        if (!appendOnly)
        {
            var cleared = await PostAsync("/listBoxRemoveItem", """{"listControlId":"LIST.ITEM"}""");
            _renderedRows.Clear();
            if (cleared)
            {
                _cartClearFailures = 0;
                listKnownEmpty = true;
            }
            else if (++_cartClearFailures >= 3)
            {
                // Clearing keeps failing — most plausibly the list is already
                // empty and this package rejects a no-op remove. Refusing to
                // draw forever would be worse than a possible duplicate, so
                // assume empty and carry on; a later successful clear resets.
                Console.WriteLine(
                    $"[JpxRestLink] listBoxRemoveItem failed {_cartClearFailures}x in a row — assuming LIST.ITEM is empty");
                listKnownEmpty = true;
            }
            else
            {
                // Unknown list contents: update the totals below, but do not
                // insert rows on top of what may still be there. The caller
                // retries and the clear runs again.
                ok = false;
            }
        }

        var commands = new List<object>
        {
            new
            {
                commandName = "SetVariable",
                variables = new[]
                {
                    new { name = VarForeground, value = "true" },
                    new { name = "STR.SUBTOTAL", value = Money(message.SubtotalCents ?? 0) },
                    new { name = "STR.TAX", value = Money(message.TaxCents ?? 0) },
                    new { name = "STR.AMOUNTOK", value = Money(message.AmountCents ?? 0) },
                },
            },
        };

        if (listKnownEmpty && rows.Count > _renderedRows.Count)
        {
            var listItems = new object[rows.Count - _renderedRows.Count];
            for (var i = _renderedRows.Count; i < rows.Count; i++)
            {
                listItems[i - _renderedRows.Count] = new { text = rows[i], itemId = i + 1 };
            }
            commands.Add(new { commandName = "ListBoxInsertItem", listControlId = "LIST.ITEM", listItems });
        }

        // Empty basket -> the secure idle screen; the first item rings the cart
        // screen in, and a void back to empty or a new sale returns to secure.
        // Only re-display when the terminal is showing something else — doing
        // it on every ring makes the terminal blink.
        var idleForm = rows.Count == 0 ? FormSecureIdle : FormCartIdle;
        var displayIdle = _lastDisplayedForm != idleForm;
        if (displayIdle)
        {
            commands.Add(new { commandName = "DisplayForm", formName = idleForm });
        }

        var sent = await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(commands));
        ok &= sent;
        if (sent)
        {
            _foreground = true;
            if (displayIdle) _lastDisplayedForm = idleForm;
        }

        if (ok)
        {
            // Everything landed: the terminal shows exactly `rows`, so the
            // next ring may take the append fast path.
            _renderedRows.Clear();
            _renderedRows.AddRange(rows);
            _cartNeedsRebuild = false;
        }
        else
        {
            InvalidateCartRender(); // full rebuild on the retry
        }
        return ok;
    }

    // ----- REST helpers -----

    /// <summary>
    /// Hand the terminal display to (false) or take it back from (true) other
    /// Android apps on the device â€” this is how WinkPay gets the screen for
    /// biometric capture while PxRetailer keeps running underneath.
    /// </summary>
    private async Task<bool> SetForegroundAsync(bool foreground)
    {
        var ok = await PostAsync(
            "/setVariable",
            $$"""{"variables":[{"name":"{{VarForeground}}","value":"{{(foreground ? "true" : "false")}}"}]}""");
        if (ok) _foreground = foreground;
        return ok;
    }

    private Task<bool> SetVariableAsync(string name, string value) => PostAsync(
        "/setVariable",
        JsonSerializer.Serialize(new { variables = new[] { new { name, value } } }));

    private void StartResultPolling()
    {
        StopResultPolling();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _resultPoll = cts;
        _ = Task.Run(() => PollForResultAsync(cts));
    }

    private void StopResultPolling()
    {
        var poll = Interlocked.Exchange(ref _resultPoll, null);
        if (poll is null) return;
        poll.Cancel();
        poll.Dispose();
    }

    /// <summary>
    /// winkpos publishes the biometric outcome into a form variable. The
    /// sequence diagram has PXRRS notify us (IS_TRANS_STARTED=2) and that path
    /// is live again, but a plain setVariable from winkpos raises no event on a
    /// stock package and delivery can silently die (stolen subscriber slot,
    /// firewall) â€” without a result the sale sits on "awaiting" forever. So the
    /// mailbox is also read directly while a biometric tender is in flight;
    /// whichever route lands first stops the other.
    /// </summary>
    private async Task PollForResultAsync(CancellationTokenSource cts)
    {
        try
        {
            await PollForResultCoreAsync(cts.Token);
        }
        finally
        {
            // Release the handle when the loop exits on its own (mailbox
            // delivery) so the trigger poll resumes; StopResultPolling may
            // have already swapped it out, in which case this is a no-op.
            if (Interlocked.CompareExchange(ref _resultPoll, null, cts) == cts)
            {
                cts.Dispose();
            }
        }
    }

    private async Task PollForResultCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string? state;
            try
            {
                state = await GetVariableAsync(_stateVar);
            }
            catch (Exception)
            {
                continue; // transient; keep polling until cancelled
            }

            if (state?.Trim() != StateResultReady) continue;

            string? json = null;
            try
            {
                json = await GetVariableAsync(_resultVar);
            }
            catch (Exception)
            {
                // Flag is up but the payload did not read; try again next tick.
                continue;
            }

            var message = json is null ? null : PosJson.Deserialize(json);
            if (message is null) continue;

            Console.WriteLine($"[JpxRestLink] result via {_resultVar}: {json}");

            // Drop the flag so the next sale starts from a clean handshake.
            // PXRRS rejects an empty value, hence "0" rather than "".
            await SetVariableAsync(_stateVar, StateIdle);

            MessageReceived?.Invoke(message);
            return;
        }
    }

    /// <summary>
    /// /subscribe takes the callback server's X509 certificate as a file
    /// attachment, and per the API docs PXRRS needs it to authenticate an
    /// HTTPS replyURL before it will post anything there. Without it the
    /// subscription still registers â€” getSubscriptionData happily reports the
    /// replyAddress â€” but every notification is dropped at the TLS handshake,
    /// which looks exactly like the terminal never firing an event.
    ///
    /// Shaped after PAX's working curl recipe:
    ///   curl 'https://&lt;terminal&gt;:9090/subscribe?replyURL=&lt;url&gt;' \
    ///        --form 'fileName=@server_pci7.cert'
    /// — multipart, part named "fileName", hand-rolled (see below) and carrying
    /// the LF-terminated certificate. Verified delivering on an A3700; the
    /// replyURL is the full https URL the RetailDemoApplication registers.
    /// The certificate sent is exported from
    /// the very keystore the notify listener presents, so the two can never
    /// drift apart.
    /// </summary>
    private async Task<bool> SubscribeAsync()
    {
        _cyclesSinceSubscribe = 0;

        // Leave an externally-owned subscription alone. Ours is accepted but
        // never delivers, whereas the RetailDemoApplication's does â€” and since
        // both point at this same callback, the events still arrive here.
        if (!_manageSubscription)
        {
            Console.WriteLine("[JpxRestLink] not managing the subscription â€” listening only");
            return true;
        }

        // Notify-first: subscribe the real callback so tender FireEvents and
        // IS_TRANS_STARTED arrive as pushes instead of waiting on the trigger
        // poll (which PXRRS's periodic lockups starve). The old failure mode â€”
        // PXRRS serializes its request queue and blocks ~10s every time it
        // fails to DELIVER a notify (firewalled callback, and no unsubscribe
        // API exists) â€” is handled by the watchdog in NoteLatency: repeated
        // ~10s stalls re-park the subscription on the terminal's own loopback
        // (instant connection-refused, no stalls) for the rest of the session,
        // and the fast polls carry the demo exactly as before. Re-claiming the
        // slot periodically also keeps stray tools (PAX test tool, node
        // listeners) from stealing it â€” last subscriber wins.
        //   POS_NOTIFY_PARK=1            start parked (the old always-poll mode)
        //   POS_NOTIFY_SUBSCRIBE_REAL=1  never park, even if the watchdog trips
        var parked = ForceRealSubscription
            ? false
            : _parkedByWatchdog || Environment.GetEnvironmentVariable("POS_NOTIFY_PARK") == "1";
        var subscribeUrl = parked ? "http://127.0.0.1:9/notify" : _notifyUrl;
        _subscribedReal = !parked;
        var path = $"/subscribe?replyURL={Uri.EscapeDataString(subscribeUrl)}";

        // Plain-http callback: subscribe bare, no certificate â€” attaching one
        // makes PXRRS treat the callback as TLS.
        if (!subscribeUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase))
        {
            var plainOk = await PostAsync(path, null);
            Console.WriteLine($"[JpxRestLink] subscribe ({(parked ? "parked on terminal loopback" : $"real callback {subscribeUrl}")}) ok={plainOk}");
            return plainOk;
        }

        var certificate = LoadNotifyCertificate();
        if (certificate is null) return await PostAsync(path, null);

        try
        {
            // Prefer the PEM file over re-encoding the certificate, and send it
            // LF-terminated: the RetailDemoApplication uploads its own
            // server_pci7.cert byte for byte — same certificate, LF line
            // endings, 1525 bytes — and that upload delivers. Our copy differed
            // only by CRLF, and PXRRS's PEM parser is evidently strict about
            // it: Success is still returned but no certificate is kept.
            var pemFile = System.IO.Path.Combine(
                AppContext.BaseDirectory, "certs", "server_pci7.cert");
            var pem = System.IO.File.Exists(pemFile)
                ? await System.IO.File.ReadAllTextAsync(pemFile)
                : System.Security.Cryptography.PemEncoding.WriteString(
                    "CERTIFICATE", certificate.RawData);
            pem = pem.Replace("\r\n", "\n");

            // Multipart, part named "fileName", per PAX's curl recipe
            // (--form 'fileName=@server_pci7.cert'); a bare non-multipart body
            // is rejected outright ("Failed to upload attached file"). With the
            // hand-rolled body below and the LF certificate, PXRRS answers
            // "Success, certificate applied successfully" and dials the https
            // callback — verified on an A3700 (two Face presses, both received).
            // Hand-rolled multipart mirroring the RetailDemoApplication's
            // Communication class byte for byte: UNQUOTED boundary in the
            // Content-Type header, QUOTED name/filename in the part's
            // Content-Disposition, application/octet-stream, CRLF throughout.
            // MultipartFormDataContent quotes the boundary, leaves name= and
            // filename= bare and appends filename*=utf-8''â€¦ â€” PXRRS's parser
            // then finds no part, keeps the address without a certificate and
            // still answers Success, so the https callback is never dialled.
            var boundary = "----PxrrsSubscribe" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"fileName\"; filename=\"server_pci7.cert\"\r\n");
            sb.Append("Content-Type: application/octet-stream\r\n\r\n");
            sb.Append(pem);
            sb.Append("\r\n"); // framing CRLF belongs to the boundary, not the content
            sb.Append("--").Append(boundary).Append("--\r\n");
            var requestBody = new ByteArrayContent(Encoding.ASCII.GetBytes(sb.ToString()));
            requestBody.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

            var response = await _http.PostAsync($"{_baseUrl}{path}", requestBody);
            var body = await response.Content.ReadAsStringAsync();
            var ok = response.IsSuccessStatusCode && IsResultOk(body);
            Console.WriteLine($"[JpxRestLink] subscribe (with callback cert) -> {body}");
            return ok;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[JpxRestLink] subscribe with certificate failed: {e.Message}");
            return await PostAsync(path, null);
        }
    }

    /// <summary>
    /// Read the trigger variable and, if it names a biometric tender, raise it
    /// once. The value is stamped back to a neutral marker so holding on the
    /// screen does not re-fire it â€” PXRRS rejects an empty value, hence "none"
    /// rather than blank. Returns true when a tender was raised.
    /// </summary>
    private async Task<bool> ReadBiometricTriggerAsync()
    {
        string? value;
        try
        {
            value = await GetVariableAsync(_triggerVar);
        }
        catch (Exception)
        {
            return false;
        }

        var method = value?.Trim().ToUpperInvariant() switch
        {
            "FACE" => "FACE",
            "PALM" => "PALM",
            _ => null,
        };
        if (method is null) return false;

        // The read above takes seconds on this terminal. If notify (or an
        // earlier poll) raised this same tender meanwhile, its handoff has
        // already written "1" here for WinkPay to claim — stamping "none" now
        // would land on top of that flag and WinkPay would never start. Let
        // the in-flight tender own the variable.
        if (TenderInFlight(method))
        {
            Console.WriteLine($"[JpxRestLink] {_triggerVar}={value} seen, but a {method} tender is already in flight — leaving the flag alone");
            return true;
        }

        await SetVariableAsync(_triggerVar, "none");
        Console.WriteLine($"[JpxRestLink] {_triggerVar}={value} â€” starting {method} tender");
        RaiseTender(method, "trigger poll");
        return true;
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            // PXRRS methods are POST-only; GET gets an empty reply.
            var response = await _http.PostAsync($"{_baseUrl}/getPackageList", null);
            if (!response.IsSuccessStatusCode) return false;
            NoteTerminalUptime(await response.Content.ReadAsStringAsync());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Every PXRRS reply carries the terminal's boot timestamp. A change means
    /// it restarted and dropped our subscription â€” which the 5s liveness poll
    /// can easily miss entirely â€” so force a fresh subscribe instead of sitting
    /// there "connected" with a dead callback.
    /// </summary>
    private void NoteTerminalUptime(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("terminalUptime", out var info)) return;
            if (!info.TryGetProperty("uptime", out var value)) return;

            var uptime = value.GetString();
            if (string.IsNullOrEmpty(uptime)) return;

            if (_lastUptime is not null && _lastUptime != uptime)
            {
                Console.WriteLine(
                    $"[JpxRestLink] terminal restarted ({_lastUptime} -> {uptime}) â€” re-subscribing");
                _subscribed = false;
                // Delivery must re-prove itself against the fresh PXRRS before
                // the trigger poll relaxes again.
                _notifyHealthy = false;
            }

            _lastUptime = uptime;
        }
        catch (JsonException)
        {
            // Not the shape we expected; liveness is unaffected.
        }
    }

    private async Task<bool> PostAsync(string path, string? jsonBody)
    {
        var started = Environment.TickCount64;
        try
        {
            var content = jsonBody is null
                ? null
                : new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{_baseUrl}{path}", content);
            var body = await response.Content.ReadAsStringAsync();
            var ok = response.IsSuccessStatusCode && IsResultOk(body);
            if (!ok) Console.WriteLine($"[JpxRestLink] POST {path} -> {body}");
            return ok;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[JpxRestLink] POST {path} failed: {e.Message}");
            return false;
        }
        finally
        {
            NoteLatency("POST " + path, started);
        }
    }

    /// <summary>
    /// A normal PXRRS call answers in ~100â€“300ms; ~10s means the terminal
    /// stalled its request queue delivering a notify to an unreachable or
    /// TLS-refusing callback. Surfacing it makes "the demo went sluggish"
    /// diagnosable from the console instead of by feel.
    ///
    /// Doubles as the notify watchdog: two ~10s stalls inside 90s while we are
    /// subscribed with the real callback means our replyURL is poisoning PXRRS
    /// (firewall dropping the inbound SYN is the classic cause, and it silently
    /// recurs on every rebuild) â€” re-park the subscription on the terminal's
    /// loopback and let the fast polls carry the session. PXRRS also stalls
    /// ~10s on its own once a minute or so, hence two-within-a-window rather
    /// than a hair trigger on the first.
    /// </summary>
    private void NoteLatency(string what, long startedTickMs)
    {
        var elapsed = Environment.TickCount64 - startedTickMs;
        if (elapsed <= 1500) return;

        Console.WriteLine(
            $"[JpxRestLink] SLOW: {what} took {elapsed / 1000.0:F1}s â€” PXRRS likely stalled on a notify delivery");

        if (elapsed < 5000) return;                      // ordinary slowness, not the delivery timeout
        if (!_subscribedReal || _parkedByWatchdog) return; // already parked; stall is PXRRS-internal

        lock (_tenderLock)
        {
            var now = Environment.TickCount64;
            _stalledCalls.Enqueue(now);
            while (_stalledCalls.Count > 0 && now - _stalledCalls.Peek() > 90_000)
            {
                _stalledCalls.Dequeue();
            }

            if (_stalledCalls.Count < 2) return;
            _stalledCalls.Clear();

            if (ForceRealSubscription)
            {
                Console.WriteLine(
                    "[JpxRestLink] repeated ~10s stalls but POS_NOTIFY_SUBSCRIBE_REAL=1 â€” keeping the real callback; check the firewall on this PC");
                return;
            }

            _parkedByWatchdog = true;
        }

        _notifyHealthy = false;
        _subscribed = false; // MaintainLinkAsync re-subscribes (parked) within 5s
        Console.WriteLine(
            "[JpxRestLink] repeated ~10s stalls â€” our callback is poisoning PXRRS (firewall?); parking the subscription and falling back to polling for this session");
    }

    private async Task<string?> GetVariableAsync(string name)
    {
        var started = Environment.TickCount64;
        var response = await _http.GetAsync(
            $"{_baseUrl}/getVariable?variableNames={Uri.EscapeDataString(name)}");
        NoteLatency($"GET {name}", started);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("resultItems", out var items)) return null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() == name
                && item.TryGetProperty("value", out var v))
            {
                return v.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Most endpoints answer with a single result object, but /sendBatchCmd
    /// answers with one per command in the batch. TryGetProperty throws rather
    /// than returning false on a non-object root, so the array case has to be
    /// handled explicitly â€” otherwise every batch send looks like a transport
    /// failure and the caller retries forever.
    /// </summary>
    private static bool IsResultOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var sawResult = false;
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("resultCode", out var code)) continue;
                    sawResult = true;
                    if (code.ToString() != "0") return false;
                }

                return sawResult;
            }

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("resultCode", out var rc)
                && rc.ToString() == "0";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopResultPolling();
        _cts.Cancel();
        if (_notifyServer is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _notifyServer.StopAsync(cts.Token);
            await _notifyServer.DisposeAsync();
        }

        _http.Dispose();
    }
}
