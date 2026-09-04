using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MerchantTerminal.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MerchantTerminal.Services;

/// <summary>
/// PAX-agreed flow (BloomingdaleDemo sequence diagram): the register drives
/// the terminal through the PxRetailer REST service, using form variables as
/// mailboxes.
///
///   startup  -> POST /setVariable BOOL.FOREGROUND=false; POST /subscribe
///   sale     -> POST /sendBatchCmd [SetVariable START_TRANS_REQ_DATA=json,
///               DisplayForm StartTransaction]
///   result   <- notify IS_TRANS_STARTED=2 -> GET /getVariable TRANS_RESULT
///
/// The REST endpoint is JPxSerialServer on this PC (USB link to the terminal)
/// or PXRRS on the terminal itself over Ethernet/Wi-Fi — same sequence, per
/// PAX's "Assumptions &amp; Clarifications".
///
/// Configured from <see cref="LinkConfig"/> — the setup screen (F9) writes the
/// terminal IP to %APPDATA%\MerchantTerminal\settings.json, and the documented
/// POS_* environment variables still override it. See <see cref="PosSettings"/>.
/// </summary>
public sealed class JpxRestLink : ITerminalLink
{
    // Names from PAX's sequence diagram. PxDesigner variables are type-prefixed
    // (BOOL./STR./INT./LIST.), so the diagram's bare "FOREGROUND" is really
    // BOOL.FOREGROUND — verified against a live A3700 (PxRetailer 2.01.16):
    // getVariable BOOL.FOREGROUND returns "true" and setVariable succeeds,
    // while the unprefixed name is rejected as unknown.
    //
    // The remaining three belong to PAX's custom Bloomingdale's package and do
    // not exist on a stock PxRetail install; see the stock-form fallback in
    // SendAsync. TODO(PAX): confirm their prefixes when that package ships.
    private const string VarRequest = "START_TRANS_REQ_DATA";
    private const string VarResult = "TRANS_RESULT";

    // Handshake states carried in the state mailbox — the pollable equivalent
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
    /// REQUIRES a client certificate (mutual TLS) — plain requests are dropped
    /// mid-handshake. JPxSerialServer on localhost stays plain HTTP.
    /// </summary>
    private static HttpMessageHandler BuildHandler(LinkConfig config)
    {
        var handler = new HttpClientHandler();
        if (!config.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)) return handler;

        // PAX chain is rooted at pxrrs-ca.pax.us (self-signed) — trust it for
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
            Console.WriteLine($"[JpxRestLink] WARNING: client cert not found at {p12} — PXRRS will reject us");
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
            return (false, $"No client certificate at {certPath} — PXRRS requires mutual TLS. See certs/README.md.");
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

            return (true, "Connected — terminal answered getPackageList.");
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
                return (false, $"TLS handshake failed — PXRRS rejected our client certificate ({certPath}).");
            }

            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused =>
                        (false, "Connection refused — nothing is listening on that port. Is PXRRS running on the terminal?"),
                    System.Net.Sockets.SocketError.HostUnreachable or
                    System.Net.Sockets.SocketError.NetworkUnreachable =>
                        (false, "Host unreachable — the terminal is not on this subnet."),
                    _ => (false, socket.Message),
                };
            }

            return (false, inner is null ? e.Message : $"{e.Message} — {inner.Message}");
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
    /// Stand-in for the diagram's <c>notify IS_TRANS_STARTED=1</c>. PXRRS does
    /// not dispatch custom form events to REST subscribers, so instead watch
    /// the form the Face button navigates to. Fires on the transition into the
    /// form, not while it stays there, so holding on the screen does not
    /// restart the tender.
    /// </summary>
    private async Task WatchTriggerFormAsync(CancellationToken ct)
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

            if (!_connected) continue;

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

            Console.WriteLine($"[JpxRestLink] '{screen}' displayed — treating as {_triggerMethod} tender");
            MessageReceived?.Invoke(new PosMessage
            {
                Type = PosMessageTypes.TenderSelected,
                Method = _triggerMethod,
            });
        }
    }

    private async Task StartNotifyServerAsync()
    {
        var uri = new Uri(_notifyUrl);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // PXRRS will not post results to a plain-http replyURL — PAX's own
        // RetailDemoApplication subscribes with an https one. Serve TLS with
        // the PAX server identity when the advertised URL says https.
        var useTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(uri.Port, listen =>
            {
                if (!useTls) return;
                var cert = LoadNotifyCertificate();
                if (cert is null)
                {
                    Console.WriteLine("[JpxRestLink] WARNING: https notify requested but no server certificate — falling back to http");
                    return;
                }

                listen.UseHttps(https =>
                {
                    https.ServerCertificate = cert;

                    // Pin TLS 1.2. Kestrel otherwise prefers 1.3, and the
                    // terminal's Java client fails that handshake — PAX's own
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
        // trace at all — which is indistinguishable from nothing arriving.
        // Everything inbound is logged so the real shape is visible.
        _notifyServer.Run(async context =>
        {
            var request = context.Request;
            using var reader = new System.IO.StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            context.Response.StatusCode = StatusCodes.Status200OK;

            Console.WriteLine(
                $"[JpxRestLink] inbound {request.Method} {request.Path}{request.QueryString} " +
                $"body={(string.IsNullOrWhiteSpace(body) ? "<empty>" : body)}");

            if (!string.IsNullOrWhiteSpace(body))
            {
                HandleNotify(body);
                return;
            }

            // Some senders put the event in the query string instead of a body.
            var name = request.Query["name"].ToString();
            var value = request.Query["value"].ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                HandleNotify(JsonSerializer.Serialize(new { name, value }));
            }
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
                Console.WriteLine($"[JpxRestLink] tender selected on the form: {value}");
                MessageReceived?.Invoke(new PosMessage
                {
                    Type = PosMessageTypes.TenderSelected,
                    Method = value.Trim().ToUpperInvariant(),
                });
            }
        }
        catch (JsonException)
        {
            // Not an event object; ignore.
        }
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
                var foregroundOk = await SetForegroundAsync(false);
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
                // Nothing reports that our callback was taken over — another
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

    public async Task<bool> SendAsync(PosMessage message)
    {
        // START_PAYMENT and CANCEL_PAYMENT both travel in START_TRANS_REQ_DATA;
        // the terminal app reads the JSON "type" to tell them apart. Re-showing
        // StartTransaction re-fires IS_TRANS_STARTED=1 so the terminal always
        // re-reads the variable.
        if (message.Type == PosMessageTypes.DisplayCart)
        {
            return await SendCartAsync(message);
        }

        if (message.Type == PosMessageTypes.ShowThanks)
        {
            // Take the screen back from WinkPay before showing the receipt.
            await SetForegroundAsync(true);
            return await PostAsync("/displayForm?formName=ThankYouScreen", null);
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
        if (message.Type == PosMessageTypes.StartPayment && message.Method == "BIOMETRIC")
        {
            await SetForegroundAsync(true);
            return await PostAsync($"/displayForm?formName={PosSettings.StockStartForm}", null);
        }

        // A biometric tender is captured by WinkPay, which is a separate Android
        // app. PxRetailer owns the display, so it has to drop the foreground or
        // the customer never sees WinkPay come up.
        if (message.Type == PosMessageTypes.StartPayment && message.Method is "FACE" or "PALM")
        {
            Console.WriteLine($"[JpxRestLink] {message.Method} tender — handing the sale to WinkPay");

            // Custom package installed: run the diagram's Phase 2 verbatim —
            // one batch publishes the order and shows StartTransaction, and the
            // package raises IS_TRANS_STARTED=1 itself (PXRRS notifies both
            // parties). The register does not touch the state flag.
            if (_startForm == FormStart)
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
                    StartResultPolling(); // fallback if the =2 notify never lands
                    var handed = await SetForegroundAsync(false);
                    Console.WriteLine(
                        $"[JpxRestLink] Phase-2 batch sent ({VarRequest} + DisplayForm {FormStart}), PxRetailer backgrounded={handed}");
                    return handed;
                }
                Console.WriteLine("[JpxRestLink] Phase-2 batch failed — falling back to the mailbox handshake");
            }

            // Stock package: mailboxes stand in for the notify — publish the
            // order, then raise the handshake flag WinkPay is polling. Order
            // matters; the flag must not go up before the details are readable.
            var published = await SetVariableAsync(_requestVar, PosJson.Serialize(message));
            var flagged = await SetVariableAsync(_stateVar, StateOrderReady);
            if (!published || !flagged)
            {
                Console.WriteLine($"[JpxRestLink] could not hand over: {_requestVar} set={published}, {_stateVar} set={flagged}");
                return false;
            }

            StartResultPolling();
            var yielded = await SetForegroundAsync(false);
            Console.WriteLine(
                $"[JpxRestLink] order published to {_requestVar}, {_stateVar}=1, PxRetailer backgrounded={yielded}");
            return yielded;
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
            ? (stockMode ? "BackgroundScreen" : FormStart)
            : stockMode && message.Method == "CARD" ? "InsertTapScreen"
            : _startForm;

        // The request mailbox only exists in PAX's custom package. Writing it in
        // stock mode fails every send with "one or more variables could not be
        // set" — and since a batch is only OK when every command is, that made
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
        return await PostAsync("/sendBatchCmd", JsonSerializer.Serialize(commands));
    }

    /// <summary>
    /// Mirror the register basket onto PxRetailer's stock idle screen:
    /// LIST.ITEM holds the line items and STR.SUBTOTAL / STR.TAX /
    /// STR.AMOUNTOK the money row (names from the PxRetailer API doc, present
    /// in the stock PxRetail packages).
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

        // Clear, then rebuild the list (empty basket -> empty list is valid).
        var ok = await PostAsync("/listBoxRemoveItem", """{"listControlId":"LIST.ITEM"}""");

        var items = message.Items ?? Array.Empty<CartLine>();
        if (items.Length > 0)
        {
            var listItems = new object[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                listItems[i] = new { text = Line(items[i]), itemId = i + 1 };
            }

            ok &= await PostAsync("/listBoxInsertItem", JsonSerializer.Serialize(
                new { listControlId = "LIST.ITEM", listItems }));
        }

        ok &= await PostAsync("/setVariable", JsonSerializer.Serialize(new
        {
            variables = new[]
            {
                new { name = "STR.SUBTOTAL", value = Money(message.SubtotalCents ?? 0) },
                new { name = "STR.TAX", value = Money(message.TaxCents ?? 0) },
                new { name = "STR.AMOUNTOK", value = Money(message.AmountCents ?? 0) },
            },
        }));
        ok &= await PostAsync("/displayForm?formName=BackgroundScreen", null);
        return ok;
    }

    // ----- REST helpers -----

    /// <summary>
    /// Hand the terminal display to (false) or take it back from (true) other
    /// Android apps on the device — this is how WinkPay gets the screen for
    /// biometric capture while PxRetailer keeps running underneath.
    /// </summary>
    private Task<bool> SetForegroundAsync(bool foreground) => PostAsync(
        "/setVariable",
        $$"""{"variables":[{"name":"{{VarForeground}}","value":"{{(foreground ? "true" : "false")}}"}]}""");

    private Task<bool> SetVariableAsync(string name, string value) => PostAsync(
        "/setVariable",
        JsonSerializer.Serialize(new { variables = new[] { new { name, value } } }));

    private void StartResultPolling()
    {
        StopResultPolling();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _resultPoll = cts;
        _ = Task.Run(() => PollForResultAsync(cts.Token));
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
    /// sequence diagram has PXRRS notify us instead, but no notification has
    /// ever been observed arriving at this PC, and without a result the sale
    /// sits on "awaiting" forever — so read the mailbox directly while a
    /// biometric tender is in flight.
    /// </summary>
    private async Task PollForResultAsync(CancellationToken ct)
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
    /// subscription still registers — getSubscriptionData happily reports the
    /// replyAddress — but every notification is dropped at the TLS handshake,
    /// which looks exactly like the terminal never firing an event.
    ///
    /// Shaped after PAX's working curl recipe:
    ///   curl 'https://&lt;terminal&gt;:9090/subscribe?replyURL=&lt;ip&gt;%3A8080' \
    ///        --form 'fileName=@server_pci7.cert'
    /// — the replyURL is bare host:port (no scheme, no path) and the multipart
    /// field is named "fileName". The certificate sent is exported from the
    /// very keystore the notify listener presents, so the two can never drift
    /// apart.
    /// </summary>
    private async Task<bool> SubscribeAsync()
    {
        _cyclesSinceSubscribe = 0;

        // Leave an externally-owned subscription alone. Ours is accepted but
        // never delivers, whereas the RetailDemoApplication's does — and since
        // both point at this same callback, the events still arrive here.
        if (!_manageSubscription)
        {
            Console.WriteLine("[JpxRestLink] not managing the subscription — listening only");
            return true;
        }

        // Send the whole URL, scheme and path included. A bare "host:port" is
        // still answered with resultCode 0, but PXRRS silently keeps whatever
        // replyAddress it had — getSubscriptionData then shows the previous
        // subscriber and every notification goes there instead of to us.
        var path = $"/subscribe?replyURL={Uri.EscapeDataString(_notifyUrl)}";

        var certificate = LoadNotifyCertificate();
        if (certificate is null) return await PostAsync(path, null);

        try
        {
            // Prefer the PEM file verbatim over re-encoding the certificate.
            // PemEncoding.WriteString emits LF and no trailing newline; the
            // subscription only takes effect when the file's own bytes (CRLF,
            // trailing newline) are sent, so PXRRS's parser is evidently picky.
            var pemFile = System.IO.Path.Combine(
                AppContext.BaseDirectory, "certs", "server_pci7.cert");
            var pem = System.IO.File.Exists(pemFile)
                ? await System.IO.File.ReadAllTextAsync(pemFile)
                : System.Security.Cryptography.PemEncoding.WriteString(
                    "CERTIFICATE", certificate.RawData);

            using var form = new System.Net.Http.MultipartFormDataContent();
            var part = new StringContent(pem, Encoding.ASCII);
            part.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            // The API doc names this part "filename" (all lower case).
            form.Add(part, "filename", "server_pci7.cert");

            var response = await _http.PostAsync($"{_baseUrl}{path}", form);
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
    /// screen does not re-fire it — PXRRS rejects an empty value, hence "none"
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

        await SetVariableAsync(_triggerVar, "none");
        Console.WriteLine($"[JpxRestLink] {_triggerVar}={value} — starting {method} tender");
        MessageReceived?.Invoke(new PosMessage
        {
            Type = PosMessageTypes.TenderSelected,
            Method = method,
        });
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
    /// it restarted and dropped our subscription — which the 5s liveness poll
    /// can easily miss entirely — so force a fresh subscribe instead of sitting
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
                    $"[JpxRestLink] terminal restarted ({_lastUptime} -> {uptime}) — re-subscribing");
                _subscribed = false;
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
    }

    private async Task<string?> GetVariableAsync(string name)
    {
        var response = await _http.GetAsync(
            $"{_baseUrl}/getVariable?variableNames={Uri.EscapeDataString(name)}");
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
    /// handled explicitly — otherwise every batch send looks like a transport
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
