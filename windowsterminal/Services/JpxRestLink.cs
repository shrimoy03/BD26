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

    // Stock PxRetail EMV screens (from getFormList on the A3700/A380 packages),
    // used by the bankcard flow: prompt the tap, ask again, show the outcome.
    /// <summary>Stock insert/swipe/tap prompt; the RetailDemoApplication drives EMV from this form.</summary>
    private const string FormTapCard = "SwipeScreen";
    private const string FormTapCardAgain = "CLSSTapCardAgain";
    private const string FormApproved = "ApprovedScreen";
    private const string FormDeclined = "DeclinedScreen";

    /// <summary>Seconds the contactless reader waits for a tap per arm; re-armed while the tender is live.</summary>
    private const int ContactlessTimeoutSeconds = 45;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly string _baseUrl;
    private readonly string _notifyUrl;
    private readonly int _webSocketPort;

    /// <summary>
    /// Another register has claimed this terminal (its callback holds PXRRS's
    /// single notify slot). While yielded this register stays quiet — no
    /// re-subscribe, no trigger/result polling, no cart repaint — until the
    /// operator does something here, which takes the terminal back.
    /// </summary>
    private volatile bool _yielded;

    /// <summary>Serial of the terminal behind <c>_baseUrl</c>, learned from every PXRRS reply.</summary>
    public string? TerminalSerial { get; private set; }
    public event Action<string>? TerminalSerialChanged;

    /// <summary>IP/host of the terminal this link drives (from the configured base URL); null for a loopback proxy.</summary>
    public string? TerminalHost
    {
        get
        {
            try
            {
                var host = new Uri(_baseUrl).Host;
                return host is "127.0.0.1" or "localhost" ? null : host;
            }
            catch (UriFormatException) { return null; }
        }
    }
    private string? _terminalOwner;
    private int _ownershipCycle;
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
    /// <summary>
    /// Serialises the screen writers: a cart sync and a biometric handoff must
    /// never interleave on PXRRS. Without this a sync already in flight when
    /// Face was pressed landed its BOOL.FOREGROUND=true seconds after the
    /// handoff's =false and yanked PxRetailer back over the WinkPay camera —
    /// seen on the A3700 on every press made while items were still ringing.
    /// </summary>
    private readonly SemaphoreSlim _screenGate = new(1, 1);
    /// <summary>Tick of the last tender raised by the customer's button (notify or poll), 0 if none.</summary>
    private long _lastButtonTenderAtTick;
    private readonly Queue<long> _stalledCalls = new();

    /// <summary>The bankcard tender the contactless reader is armed for, if any.</summary>
    private sealed record CardTender(string? OrderId, long AmountCents, string? Currency, string Method);
    private CardTender? _cardTender;
    private int _emvSequence;

    /// <summary>
    /// A bankcard sale was just approved on this link: the register's SHOW_THANKS
    /// should paint PxRetailer's approved screen (there is no WinkPay page to
    /// leave on screen in that flow).
    /// </summary>
    private bool _cardApprovedPending;
    private long _lastTenderAtTick;
    private string? _lastTenderMethod;

    private static bool ForceRealSubscription =>
        Environment.GetEnvironmentVariable("POS_NOTIFY_SUBSCRIBE_REAL") == "1";

    /// <summary>Poll cycles (5s each) between re-claiming the notify callback.</summary>
    private const int ResubscribeCycles = 20; // x15s probe cadence = every 5 minutes

    public JpxRestLink(LinkConfig config)
    {
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _notifyUrl = config.NotifyUrl;
        _webSocketPort = config.WebSocketPort;
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
        _http = new HttpClient(BuildHandler(config)) { Timeout = TimeSpan.FromSeconds(12) };
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
                await Task.Delay(TimeSpan.FromMilliseconds(2000), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_connected || _yielded) continue;

            // While a tender is in flight the result poll owns the mailbox â€”
            // pausing the trigger poll halves the request pressure on PXRRS
            // (which serializes) and avoids double-reading the shared variable.
            if (_resultPoll is not null) continue;

            // Notify is delivering (proven) and we hold the real subscription:
            // the Face press reaches us as IS_TRANS_STARTED=face within a second.
            // Polling the same variable on top of that only loads PXRRS's
            // single request queue (every GET here delays a cart update) and,
            // worse, a poll that read "face" just before the notify arrived
            // stamped "none" over the handoff's "1" — WinkPay never saw the
            // order. The poll is a fallback for a dead subscription only.
            if (_notifyHealthy && _subscribedReal && !_parkedByWatchdog) continue;

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
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("commandName", out var cmd)
                && cmd.GetString() is { Length: > 0 } commandName)
            {
                // Asynchronous command outcome (EMV reader etc.), not a form event.
                HandleEmvResponse(commandName, doc.RootElement);
                return;
            }

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
        if (source != "register") _lastButtonTenderAtTick = Environment.TickCount64;
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
                if (_subscribed) await PublishRegisterAddressAsync();
                // Subscribing is what actually matters; a package that does not
                // define the foreground flag should not fail the whole link.
                if (!foregroundOk)
                {
                    Console.WriteLine($"[JpxRestLink] note: {VarForeground} not settable on this package");
                }
            }
            else if (alive && !_yielded && ++_cyclesSinceSubscribe >= ResubscribeCycles)
            {
                // Nothing reports that our callback was taken over â€” another
                // client on this machine subscribing with the same identity
                // simply replaces it, and we would keep reporting connected
                // while receiving nothing. Re-claiming it is cheap and
                // idempotent, so do it periodically rather than trusting the
                // original subscribe to hold.
                _cyclesSinceSubscribe = 0;
                await SubscribeAsync();
                await PublishRegisterAddressAsync();
            }

            // Every other cycle (30s): is our callback still the one PXRRS
            // posts to? If a different register has subscribed since, it is
            // driving this terminal now — step back rather than fight it.
            if (alive && _subscribed && ++_ownershipCycle % 2 == 0)
            {
                await CheckOwnershipAsync();
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
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
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
        // Operator activity on a yielded register means they moved back to
        // this PC: take the terminal back before doing anything on it.
        if (_yielded) await ClaimTerminalAsync("operator activity");

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
            // Bankcard sale: PxRetailer owned the screen throughout, so it also
            // shows the outcome. The next sale's cart sync brings the idle
            // screen back.
            if (_cardApprovedPending)
            {
                _cardApprovedPending = false;
                var approvedShown = await PostAsync(
                    "/sendBatchCmd",
                    JsonSerializer.Serialize(new object[] { new { commandName = "DisplayForm", formName = FormApproved } }));
                if (approvedShown) _lastDisplayedForm = FormApproved;
                InvalidateCartRender();
                Console.WriteLine($"[JpxRestLink] bankcard sale complete — {FormApproved} shown={approvedShown}");
                return approvedShown;
            }

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
            await _screenGate.WaitAsync();
            try
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

            await SettleButtonWritesAsync();

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
            _ = Task.Run(() => VerifyHandoffAsync(handoff, message.OrderId));
            return true;
            }
            finally
            {
                _screenGate.Release();
            }
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
            if (DisarmCardTender("cancelled by the register"))
            {
                // The reader is still waiting for a tap from the arm we just
                // abandoned; release it so the next tender's arm is not
                // refused with "card already detected"/"detecting card".
                await ReleaseContactlessAsync("cancel");
            }
            await SetForegroundAsync(true);
        }

        // Bankcard: the register drives the EMV contactless read itself over
        // PXRRS (semi-integrated) — show the stock tap prompt and arm the
        // reader below. Everything else in stock-form mode (BD Loyallist Pay)
        // lands on the payment-options page; a cancel drops the terminal back
        // to the idle cart screen.
        var stockMode = _startForm != FormStart;
        var isCard = message.Type == PosMessageTypes.StartPayment && message.Method is "CARD" or "BD_LOYALLIST";
        var form = message.Type == PosMessageTypes.CancelPayment
            ? (stockMode ? FormCartIdle : FormStart)
            : isCard ? FormTapCard
            : _startForm;

        // The request mailbox only exists in PAX's custom package. Writing it in
        // stock mode fails every send with "one or more variables could not be
        // set" â€” and since a batch is only OK when every command is, that made
        // an otherwise successful DisplayForm look like a failure.
        var commands = new List<object>();
        if (!stockMode && !isCard)
        {
            commands.Add(new
            {
                commandName = "SetVariable",
                variables = new[] { new { name = VarRequest, value = PosJson.Serialize(message) } },
            });
        }

        if (isCard)
        {
            // Prompt text and amount for the stock SwipeScreen. The tap itself
            // is read by emvBeginContactlessTxn (BeginContactlessAsync below).
            // Do NOT add EMVDetectICCard here: on the live A3700 it claims the
            // card interfaces for contact/MSR detection, the contactless arm
            // then never gets the tap (seen 2026-09-16: tap -> nothing, stuck on
            // the swipe prompt), and its own detect result is not a payment.
            // PXRRS runs one card session at a time — tap is the demo's path.
            commands.Add(new
            {
                commandName = "SetVariable",
                variables = new object[]
                {
                    new { name = "STR.SWIPE", value = "Please Tap Card" },
                    new { name = "STR.AMOUNTOK", value = "$" + ((message.AmountCents ?? 0) / 100m).ToString("N2") },
                },
            });
        }
        commands.Add(new { commandName = "DisplayForm", formName = form });
        var displayed = await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(commands));
        if (displayed) _lastDisplayedForm = form;
        // Even a cancel back to the cart form counts: the list may have been
        // reset by the form navigation in between, so rebuild on the next sync.
        InvalidateCartRender();

        if (isCard)
        {
            lock (_tenderLock)
            {
                _cardTender = new CardTender(message.OrderId, message.AmountCents ?? 0, message.Currency, message.Method ?? "CARD");
                _cardApprovedPending = false;
            }
            Console.WriteLine(
                $"[JpxRestLink] bankcard tender {message.OrderId} amountCents={message.AmountCents} — arming the contactless reader");
            // Finish whatever contactless session a previous tender left open
            // (abandoned arm, cancel, crash) before claiming the reader again —
            // PAX's own pre-transaction batch does the same (API doc example 3).
            await ReleaseContactlessAsync("new tender");
            // The tap screen is what the customer needs; the reader arming can
            // still be retried from the async response path if it fails here.
            var armed = await BeginContactlessAsync();
            return displayed || armed;
        }

        return displayed;
    }

    // ----- Bankcard: EMV contactless over PXRRS -----

    /// <summary>
    /// Arm the contactless reader for the live bankcard tender
    /// (emvBeginContactlessTxn, PXRRS API doc p.54). The POST answers "In
    /// progress" at once; the actual outcome arrives asynchronously on the
    /// notify callback as a JSON object carrying commandName — see
    /// <see cref="HandleEmvResponse"/>. Amount and the other mandatory TLVs
    /// follow the doc's contactless request table.
    /// </summary>
    private async Task<bool> BeginContactlessAsync()
    {
        CardTender? tender;
        int sequence;
        lock (_tenderLock)
        {
            tender = _cardTender;
            sequence = ++_emvSequence;
        }
        if (tender is null) return false;

        var now = DateTime.Now;
        var tlvs = new object[]
        {
            new { tag = "9F02", value = tender.AmountCents.ToString("D12") }, // amount, n12
            new { tag = "9F03", value = "000000000000" },                    // cashback
            new { tag = "9C", value = "00" },                                // purchase
            new { tag = "9A", value = now.ToString("yyMMdd") },
            new { tag = "9F21", value = now.ToString("HHmmss") },
            new { tag = "5F2A", value = "0840" },                            // USD
            new { tag = "5F36", value = "2" },                               // 2 decimals
            new { tag = "9F41", value = sequence.ToString("D8") },           // transaction sequence
        };

        var ok = await PostAsync(
            $"/emvBeginContactlessTxn?timeout={ContactlessTimeoutSeconds}",
            JsonSerializer.Serialize(tlvs));
        if (!ok)
        {
            // PxRetailer transiently refuses right after a form change, or a
            // stale session is still holding the reader; release it and retry
            // once before giving the register a decline.
            await Task.Delay(600);
            await ReleaseContactlessAsync("arm refused");
            ok = await PostAsync(
                $"/emvBeginContactlessTxn?timeout={ContactlessTimeoutSeconds}",
                JsonSerializer.Serialize(tlvs));
        }

        if (!ok)
        {
            Console.WriteLine("[JpxRestLink] contactless reader could not be armed");
            FinishCardTender("DECLINED", "Card reader unavailable", showDeclined: true);
        }
        else
        {
            Console.WriteLine($"[JpxRestLink] contactless reader armed (seq {sequence}, {ContactlessTimeoutSeconds}s)");
        }
        return ok;
    }

    /// <summary>Forget the live bankcard tender; true if there was one.</summary>
    private bool DisarmCardTender(string why)
    {
        lock (_tenderLock)
        {
            if (_cardTender is null) return false;
            _cardTender = null;
        }
        Console.WriteLine($"[JpxRestLink] bankcard tender disarmed — {why}");
        return true;
    }

    /// <summary>
    /// emvReleaseContactlessService: "release the contactless service and
    /// finish the transaction so that new transactions can be performed"
    /// (PXRRS API doc). Synchronous; a failure only means there was nothing
    /// to release, so the outcome is logged and otherwise ignored.
    /// </summary>
    private async Task ReleaseContactlessAsync(string why)
    {
        var released = await PostAsync("/emvReleaseContactlessService", null);
        Console.WriteLine($"[JpxRestLink] contactless service release ({why}) ok={released}");
    }

    /// <summary>
    /// Resolve the live bankcard tender into a PAYMENT_RESULT for the register
    /// and, for declines, paint the stock declined screen. No-op when no
    /// tender is armed (late or duplicate reader responses).
    /// </summary>
    private void FinishCardTender(string status, string? reason, bool showDeclined)
    {
        CardTender? tender;
        lock (_tenderLock)
        {
            tender = _cardTender;
            _cardTender = null;
            if (tender is not null && status == "APPROVED") _cardApprovedPending = true;
        }
        if (tender is null) return;

        Console.WriteLine($"[JpxRestLink] bankcard {tender.OrderId}: {status}{(reason is null ? "" : $" — {reason}")}");

        if (showDeclined)
        {
            _ = Task.Run(async () =>
            {
                var shown = await PostAsync(
                    "/sendBatchCmd",
                    JsonSerializer.Serialize(new object[] { new { commandName = "DisplayForm", formName = FormDeclined } }));
                if (shown) _lastDisplayedForm = FormDeclined;
                InvalidateCartRender();
            });
        }

        MessageReceived?.Invoke(new PosMessage
        {
            Type = PosMessageTypes.PaymentResult,
            OrderId = tender.OrderId,
            AmountCents = status == "APPROVED" ? tender.AmountCents : null,
            Currency = tender.Currency,
            Status = status,
            Method = tender.Method,
            Reason = reason,
        });
    }

    /// <summary>
    /// Asynchronous EMV command outcomes delivered to the notify callback. The
    /// tap itself is the demo's "authorization": a clean
    /// EMVBeginContactlessTxn result marks the sale approved and the kernel
    /// transaction is closed with emvEndContactlessTxn (no gateway behind this
    /// register). Reader timeouts re-arm while the tender is live so the
    /// customer can take their time; a cancel on the terminal or a hard EMV
    /// failure resolves the tender accordingly.
    /// </summary>
    private void HandleEmvResponse(string commandName, JsonElement root)
    {
        var resultCode = root.TryGetProperty("resultCode", out var rc) ? rc.ToString().Trim() : "";
        var message = root.TryGetProperty("message", out var msg) ? msg.ToString() : "";
        Console.WriteLine($"[JpxRestLink] EMV {commandName}: resultCode={resultCode} {message}");

        if (!commandName.Equals("EMVBeginContactlessTxn", StringComparison.OrdinalIgnoreCase))
        {
            return; // emvEndContactlessTxn / detect results: informational
        }

        bool armed;
        lock (_tenderLock) armed = _cardTender is not null;
        if (!armed)
        {
            Console.WriteLine("[JpxRestLink] contactless result with no bankcard tender armed — ignored");
            return;
        }

        switch (resultCode.ToLowerInvariant())
        {
            case "0":
                if (root.TryGetProperty("tlvs", out var tlvs) && tlvs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tlv in tlvs.EnumerateArray())
                    {
                        var tag = tlv.TryGetProperty("tag", out var t) ? t.GetString() : null;
                        if (tag is "50" or "5F20" && tlv.TryGetProperty("value", out var v))
                        {
                            Console.WriteLine($"[JpxRestLink]   {tag} = {HexToAscii(v.GetString())}");
                        }
                    }
                }
                // Close the kernel transaction; its own outcome is logged when it lands.
                _ = Task.Run(() => PostAsync("/emvEndContactlessTxn", ""));
                FinishCardTender("APPROVED", null, showDeclined: false);
                break;

            case "0x65": // tap card again
                _ = Task.Run(async () =>
                {
                    await PostAsync(
                        "/sendBatchCmd",
                        JsonSerializer.Serialize(new object[] { new { commandName = "DisplayForm", formName = FormTapCardAgain } }));
                    await BeginContactlessAsync();
                });
                break;

            case "0x86": // reader timeout — keep waiting while the register is awaiting
            case "0x8a": // still detecting
            case "0x8b": // card already detected
            case "0x8d": // card already swiped
            case "0x8f": // card already tapped
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    await BeginContactlessAsync();
                });
                break;

            case "0x88": // customer cancelled on the terminal
                FinishCardTender("CANCELLED", "Cancelled on the customer terminal", showDeclined: false);
                break;

            default:
                var emvCode = root.TryGetProperty("emvResultCode", out var ec) ? ec.ToString() : null;
                FinishCardTender(
                    "DECLINED",
                    string.IsNullOrWhiteSpace(message) ? $"Card read failed ({resultCode}{(emvCode is null ? "" : $"/{emvCode}")})" : message,
                    showDeclined: true);
                break;
        }
    }

    private static string HexToAscii(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0) return hex ?? "";
        try
        {
            var bytes = Convert.FromHexString(hex);
            return Encoding.ASCII.GetString(bytes).Trim();
        }
        catch (FormatException)
        {
            return hex;
        }
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
    private static object[] CartVariables(PosMessage message, bool foreground)
    {
        static string Money(long cents) => "$" + (cents / 100m).ToString("N2");
        var vars = new List<object>();
        if (foreground) vars.Add(new { name = VarForeground, value = "true" });
        vars.Add(new { name = "STR.SUBTOTAL", value = Money(message.SubtotalCents ?? 0) });
        vars.Add(new { name = "STR.TAX", value = Money(message.TaxCents ?? 0) });
        vars.Add(new { name = "STR.AMOUNTOK", value = Money(message.AmountCents ?? 0) });
        return vars.ToArray();
    }

    /// </summary>
    private async Task<bool> SendCartAsync(PosMessage message)
    {
        await _screenGate.WaitAsync();
        try
        {
            return await SendCartCoreAsync(message);
        }
        finally
        {
            _screenGate.Release();
        }
    }

    private async Task<bool> SendCartCoreAsync(PosMessage message)
    {
        // A biometric tender is in flight: WinkPay owns the screen. Mirror the
        // totals (STR.AMOUNTOK feeds WinkPay's stock path) but never assert the
        // foreground or repaint a form — that is exactly what covered the camera.
        var biometricInFlight = _resultPoll is not null;
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
            // Standalone clear first — the RetailDemoApplication does this too. A
            // listBoxRemoveItem folded into the redraw batch is accepted but does
            // NOT clear the list (rows piled up: 7 lines for a 3-item basket), so
            // this is one round trip we keep.
            var cleared = await PostAsync("/listBoxRemoveItem", """{"listControlId":"LIST.ITEM"}""");
            _renderedRows.Clear();
            listKnownEmpty = cleared;
            if (!cleared)
            {
                Console.WriteLine("[JpxRestLink] listBoxRemoveItem failed — totals only this pass, rows rebuilt on the next sync");
                ok = false;
            }
        }

        var commands = new List<object>
        {
            new
            {
                commandName = "SetVariable",
                variables = CartVariables(message, foreground: !biometricInFlight),
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
        var displayIdle = !biometricInFlight && _lastDisplayedForm != idleForm;
        if (displayIdle)
        {
            commands.Add(new { commandName = "DisplayForm", formName = idleForm });
        }

        var sent = await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(commands));
        ok &= sent;
        if (sent)
        {
            if (!biometricInFlight) _foreground = true;
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

    /// <summary>
    /// The stock Face/Palm button fires its IS_TRANS_STARTED event BEFORE its
    /// own SetVariable actions land (GENERIC_2="face", GENERIC_1=""), and
    /// those arrive 1-2s later through PXRRS's serialised queue — on top of
    /// whatever we published in between. Seen on the A3700 on every press:
    /// "order published" followed two seconds later by the mailbox reading
    /// face/empty. So after a button-raised tender, wait until the button's
    /// write is visible (or 4s) before publishing, so ours is the last write.
    /// Register-initiated tenders (test keys) have no button and skip this.
    /// </summary>
    private async Task SettleButtonWritesAsync()
    {
        if (Environment.TickCount64 - _lastButtonTenderAtTick > 15_000) return;
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < 4000)
        {
            string? v = null;
            try { v = await GetVariableAsync(_stateVar); } catch (Exception) { }
            if (v?.Trim().ToUpperInvariant() is "FACE" or "PALM")
            {
                Console.WriteLine($"[JpxRestLink] button write landed on {_stateVar} after {Environment.TickCount64 - started}ms — publishing now");
                return;
            }
            await Task.Delay(400);
        }
        Console.WriteLine($"[JpxRestLink] button write not seen on {_stateVar} within 4s — publishing anyway");
    }

    /// <summary>
    /// The published order must survive until WinkPay claims it. On the
    /// A3700 it did not always: STR.GENERIC_1 came back empty and the flag
    /// "none" seconds after a successful handoff (a second Face press — the
    /// stock button clears the mailbox — or a late stamp from any poller).
    /// Re-read at +2s and +5s and re-publish once if the sale is gone and
    /// WinkPay has not claimed it (3) or answered (2).
    /// </summary>
    private async Task VerifyHandoffAsync(object[] handoff, string? orderId)
    {
        var republished = 0;
        foreach (var delayMs in new[] { 2000, 2500, 3000, 3000, 4000 })
        {
            await Task.Delay(delayMs);
            if (_resultPoll is null) return; // sale finished or cancelled meanwhile
            string? state, order;
            try
            {
                state = (await GetVariableAsync(_stateVar))?.Trim();
                order = await GetVariableAsync(_requestVar);
            }
            catch (Exception) { continue; }

            if (state is "3" or "2") return; // WinkPay has it
            if (state == StateOrderReady && !string.IsNullOrWhiteSpace(order)) continue; // intact, check once more

            Console.WriteLine(
                $"[JpxRestLink] handoff for {orderId} was wiped ({_stateVar}='{state}', {_requestVar} {(string.IsNullOrWhiteSpace(order) ? "empty" : "set")}) — re-publishing");
            if (await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(handoff)))
            {
                _foreground = false;
            }
            if (++republished >= 3) return; // the button's writes should long be over by now
        }
    }

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
            // "certificate applied successfully" is PXRRS confirming it kept our
            // cert — the one precondition delivery ever depended on (verified on
            // the A3700). Treat the callback as live from here so the trigger
            // poll stays off instead of loading PXRRS until the first event.
            if (ok && body.Contains("certificate applied", StringComparison.OrdinalIgnoreCase))
            {
                _notifyHealthy = true;
            }
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

        Console.WriteLine($"[JpxRestLink] {_triggerVar}={value} — starting {method} tender");
        RaiseTender(method, "trigger poll");

        // Consume the trigger only once it is clear nobody took the sale: an
        // accepted tender's handoff replaces "face" with "1" itself (and this
        // is the same variable), so a stamp issued now could only ever land on
        // that flag. Decide after the handoff would have landed.
        _ = Task.Run(async () =>
        {
            await Task.Delay(6000);
            if (_resultPoll is not null || TenderInFlight(method)) return;
            var current = await GetVariableAsync(_triggerVar);
            if (current?.Trim().ToUpperInvariant() is "FACE" or "PALM")
            {
                await SetVariableAsync(_triggerVar, "none");
                Console.WriteLine($"[JpxRestLink] {_triggerVar} cleared — the {method} press was not turned into a sale");
            }
        });
        return true;
    }

    /// <summary>
    /// Stock PxRetail variable the register's WebSocket address is parked in
    /// for the WinkPay app to discover. STR.TEXT_12 is defined on every stock
    /// package (getVariableList) and not bound to any form the demo shows.
    /// </summary>
    public const string VarRegisterAddress = "STR.TEXT_12";

    /// <summary>
    /// Tell the WinkPay app where this register is. The app used to have the
    /// register's IP compiled in (the dev Mac's), so on any other PC the
    /// biometric handoff backgrounded PxRetailer and the launch message went
    /// to the wrong machine — WinkPay came up and nothing happened. The host
    /// is the one the notify callback already resolved (the NIC that routes
    /// to the terminal), so a multi-NIC store PC advertises the right one.
    /// </summary>
    private async Task PublishRegisterAddressAsync()
    {
        string host;
        try { host = new Uri(_notifyUrl).Host; }
        catch (UriFormatException) { return; }
        if (host is "127.0.0.1" or "localhost") return; // nothing useful to advertise

        var address = $"ws://{host}:{_webSocketPort}/pos";
        var ok = await SetVariableAsync(VarRegisterAddress, address);
        Console.WriteLine($"[JpxRestLink] register address {address} published to {VarRegisterAddress} ok={ok}");
    }

    /// <summary>
    /// Whose callback PXRRS currently posts to. A different host than ours
    /// means another register claimed the terminal after we did.
    /// </summary>
    private async Task CheckOwnershipAsync()
    {
        string? owner;
        try
        {
            var response = await _http.PostAsync($"{_baseUrl}/getSubscriptionData", null);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            owner = doc.RootElement.TryGetProperty("PXRRS", out var pxrrs)
                && pxrrs.TryGetProperty("replyAddress", out var reply)
                ? reply.GetString()
                : null;
        }
        catch (Exception)
        {
            return; // transient; decide next cycle
        }
        if (string.IsNullOrWhiteSpace(owner)) return;

        string ownerHost, ourHost;
        try
        {
            ownerHost = new Uri(owner).Host;
            ourHost = new Uri(_notifyUrl).Host;
        }
        catch (UriFormatException)
        {
            return;
        }

        if (ownerHost.Equals(ourHost, StringComparison.OrdinalIgnoreCase))
        {
            if (_yielded)
            {
                // The other register let go (or restarted us into ownership).
                _yielded = false;
                _terminalOwner = null;
                LinkHealth.Report(null);
                Console.WriteLine("[JpxRestLink] terminal is ours again");
            }
            return;
        }

        if (!_yielded || _terminalOwner != ownerHost)
        {
            _yielded = true;
            _terminalOwner = ownerHost;
            StopResultPolling();
            DisarmCardTender($"terminal claimed by {ownerHost}");
            Console.WriteLine($"[JpxRestLink] YIELDED — the register at {ownerHost} has claimed this terminal; ring an item or start a sale here to take it back");
            LinkHealth.Report($"terminal is being driven by the register at {ownerHost} — ring an item or start a sale here to take it back");
        }
    }

    /// <summary>
    /// Take the terminal: our callback into PXRRS's notify slot, our address
    /// into the variable WinkPay reads, PxRetailer back on screen. Idempotent.
    /// </summary>
    private async Task ClaimTerminalAsync(string why)
    {
        Console.WriteLine($"[JpxRestLink] claiming the terminal ({why})");
        _subscribed = await SubscribeAsync();
        if (!_subscribed) return;
        await PublishRegisterAddressAsync();
        await SetForegroundAsync(true);
        InvalidateCartRender();
        _yielded = false;
        _terminalOwner = null;
        _cyclesSinceSubscribe = 0;
        LinkHealth.Report(null);
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
            if (info.TryGetProperty("terminalSerialNumber", out var sn)
                && sn.GetString() is { Length: > 0 } serial
                && serial != TerminalSerial)
            {
                TerminalSerial = serial;
                Console.WriteLine($"[JpxRestLink] terminal serial {serial}");
                TerminalSerialChanged?.Invoke(serial);
            }

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
            LinkHealth.Report(null);
            return ok;
        }
        catch (ObjectDisposedException)
        {
            // Setup screen re-applied settings while this call was in flight;
            // the replacement link takes over.
            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[JpxRestLink] POST {path} failed: {e.Message}");
            _ = Task.Run(() => DiagnoseFailureAsync(path, e));
            return false;
        }
        finally
        {
            NoteLatency("POST " + path, started);
        }
    }

    private long _lastDiagnosisTick;

    /// <summary>
    /// A PXRRS call failed: say WHY on the status bar, not just that it did.
    /// The failures seen in the field have distinct transport signatures:
    ///   - TCP connect refused           -> PXRRS is not running on the terminal
    ///   - TCP connect times out, host    -> PXRRS is frozen: Android's cached-app
    ///     answers ping                     freezer stopped its process (seen on
    ///                                      the A380 27s after PXRRS was opened
    ///                                      from its icon rather than at boot)
    ///   - connect OK, no HTTP answer     -> PXRRS is running but its queue is
    ///                                      stuck (aged logger loop / PxRetailer)
    ///   - host does not answer at all    -> Wi-Fi / wrong IP
    /// Rate-limited: one diagnosis per 10s, the failures come in bursts.
    /// </summary>
    private async Task DiagnoseFailureAsync(string path, Exception e)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastDiagnosisTick, now) < 10_000) return;

        var uri = new Uri(_baseUrl);
        string verdict;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(uri.Host, uri.Port);
            var done = await Task.WhenAny(connect, Task.Delay(2500));
            if (done != connect)
            {
                verdict = await HostAnswersPingAsync(uri.Host)
                    ? "PXRRS on the terminal is frozen or hung (port open, no accept) — reopen PxRetailerRestService or reboot the terminal"
                    : $"terminal {uri.Host} is not answering — check Wi-Fi / IP";
            }
            else
            {
                try
                {
                    await connect; // surfaces refused
                    verdict = e is TaskCanceledException
                        ? "PXRRS accepted the connection but never answered — its request queue is stuck; restart PxRetailerRestService"
                        : $"PXRRS reachable but the call failed: {e.GetBaseException().Message}";
                }
                catch (System.Net.Sockets.SocketException se) when (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
                {
                    verdict = "PXRRS is not running on the terminal (connection refused) — open PxRetailerRestService or reboot the terminal";
                }
            }
        }
        catch (Exception probe)
        {
            verdict = $"terminal probe failed: {probe.GetBaseException().Message}";
        }

        Console.WriteLine($"[JpxRestLink] DIAGNOSIS after {path}: {verdict}");
        LinkHealth.Report(verdict);
    }

    private static async Task<bool> HostAnswersPingAsync(string host)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(host, 1500);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch (Exception)
        {
            return false; // no ping privilege: treat as unknown, not as down
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

        // Measured causes on the live A380 (2026-09-17/21): PXRRS's logger
        // failure loop aging the process (3–20s), and Android freezing the
        // PXRRS process outright (connects hang). Notify delivery to this
        // register was never the cause once the callback was https.
        Console.WriteLine(
            $"[JpxRestLink] SLOW: {what} took {elapsed / 1000.0:F1}s — PXRRS on the terminal is slow (aged process or frozen)");

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

            // Never park. Delivery to this callback is verified end to end on
            // the A3700; the stalls seen here came from PxRetailer restarting
            // on the terminal, and parking threw notify away for the session —
            // which put the laggy trigger poll back in charge. Log and go on.
            Console.WriteLine(
                "[JpxRestLink] repeated ~10s stalls — PXRRS is slow or PxRetailer restarted; keeping the real callback");
        }
    }

    private async Task<string?> GetVariableAsync(string name)
    {
        var started = Environment.TickCount64;
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(
                $"{_baseUrl}/getVariable?variableNames={Uri.EscapeDataString(name)}");
        }
        catch (Exception e) when (e is not ObjectDisposedException)
        {
            _ = Task.Run(() => DiagnoseFailureAsync($"GET {name}", e));
            throw;
        }
        finally
        {
            NoteLatency($"GET {name}", started);
        }
        LinkHealth.Report(null);
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

    /// <summary>
    /// Leaving this terminal (setup screen retargeted the register, or the
    /// register is closing): stop being its register. Otherwise the terminal
    /// keeps posting notifies to us, its WinkPay app keeps following our
    /// address and fighting the new terminal's app for our socket, and the
    /// old terminal is left showing whatever we last put there. Best effort,
    /// each step capped at 3s so a dead terminal cannot stall the switch.
    /// </summary>
    private async Task ReleaseTerminalAsync()
    {
        if (!_connected) return;
        Console.WriteLine($"[JpxRestLink] releasing terminal {TerminalSerial ?? _baseUrl}");
        async Task Step(string what, Task<bool> work)
        {
            var done = await Task.WhenAny(work, Task.Delay(3000));
            var ok = done == work && await work;
            Console.WriteLine($"[JpxRestLink]   release: {what} ok={ok}");
        }
        // Advertise nothing (PXRRS rejects an empty value; the app treats a
        // non-ws:// value as "no register").
        await Step("unpublish address", SetVariableAsync(VarRegisterAddress, "none"));
        // Park the notify slot on the terminal's own loopback so it stops
        // dialling us — PXRRS has no unsubscribe.
        await Step("park subscription", PostAsync(
            $"/subscribe?replyURL={Uri.EscapeDataString("http://127.0.0.1:9/notify")}", null));
        await Step("PxRetailer to foreground", SetForegroundAsync(true));
    }

    public async ValueTask DisposeAsync()
    {
        StopResultPolling();
        try { await ReleaseTerminalAsync(); } catch (Exception e) { Console.WriteLine($"[JpxRestLink] release failed: {e.Message}"); }
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
