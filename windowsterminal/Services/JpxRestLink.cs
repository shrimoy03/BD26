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

    /// <summary>
    /// Stock-package mailbox winkpos writes its biometric outcome to, since
    /// TRANS_RESULT only exists in PAX's custom package.
    /// </summary>
    private const string VarResultStock = "STR.TRANSACTION_RESULT";

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
    private readonly string _triggerMethod;
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
        _triggerMethod = config.TriggerMethod;
        _http = new HttpClient(BuildHandler(config)) { Timeout = TimeSpan.FromSeconds(10) };
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

        if (_triggerForm.Length > 0)
        {
            _ = Task.Run(() => WatchTriggerFormAsync(_cts.Token));
            Console.WriteLine(
                $"[JpxRestLink] watching for form '{_triggerForm}' as a {_triggerMethod} tender");
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
                if (cert is not null) listen.UseHttps(cert);
                else Console.WriteLine("[JpxRestLink] WARNING: https notify requested but no server certificate — falling back to http");
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

            // PAYMENTSTATUS face|palm|card -> a tender button on the
            // PxRetailer form (PxDesigner FireEvent). Surface to the VM so it
            // can route the sale to WinkPay or the EMV flow.
            if (name == "PAYMENTSTATUS" && !string.IsNullOrWhiteSpace(value))
            {
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

        // A biometric tender is captured by WinkPay, which is a separate Android
        // app. PxRetailer owns the display, so it has to drop the foreground or
        // the customer never sees WinkPay come up.
        if (message.Type == PosMessageTypes.StartPayment && message.Method is "FACE" or "PALM")
        {
            StartResultPolling();
            return await SetForegroundAsync(false);
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
        // PXRRS rejects setVariable with an empty value ("invalid format"), so
        // the mailbox cannot be cleared. Snapshot it instead and treat only a
        // change as this sale's result. Consecutive sales differ by orderId, so
        // an identical payload twice in a row is not a realistic case.
        string? baseline;
        try
        {
            baseline = await GetVariableAsync(VarResultStock);
        }
        catch (Exception)
        {
            baseline = null;
        }

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

            string? json;
            try
            {
                json = await GetVariableAsync(VarResultStock);
            }
            catch (Exception)
            {
                continue; // transient; keep polling until cancelled
            }

            if (string.IsNullOrWhiteSpace(json) || json == baseline) continue;

            var message = PosJson.Deserialize(json);
            if (message is null)
            {
                baseline = json; // not ours; do not re-read it every second
                continue;
            }

            Console.WriteLine($"[JpxRestLink] biometric result via {VarResultStock}: {json}");
            MessageReceived?.Invoke(message);
            return;
        }
    }

    private Task<bool> SubscribeAsync()
    {
        _cyclesSinceSubscribe = 0;
        return PostAsync($"/subscribe?replyURL={Uri.EscapeDataString(_notifyUrl)}", null);
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
