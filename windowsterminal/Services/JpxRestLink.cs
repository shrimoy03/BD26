using System;
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
///   startup  -> POST /setVariable FOREGROUND=false; POST /subscribe
///   sale     -> POST /sendBatchCmd [SetVariable START_TRANS_REQ_DATA=json,
///               DisplayForm StartTransaction]
///   result   <- notify IS_TRANS_STARTED=2 -> GET /getVariable TRANS_RESULT
///
/// The REST endpoint is JPxSerialServer on this PC (USB link to the terminal)
/// or PXRRS on the terminal itself over Ethernet/Wi-Fi — same sequence, per
/// PAX's "Assumptions &amp; Clarifications".
///
/// Config (environment variables):
///   POS_JPXSS_URL    REST base (default http://127.0.0.1:9090)
///   POS_NOTIFY_URL   replyURL we register with /subscribe
///                    (default http://127.0.0.1:8282/notify — use this PC's
///                    LAN IP when the REST endpoint is the terminal itself)
///   POS_PXRRS_P12       client-certificate PKCS#12 for mutual TLS. PXRRS on
///                       the terminal requires it over https (default
///                       certs/pxrrs-integration-client.p12; derived from the
///                       JPxSerialServer bundle's integrationCustomer.jks,
///                       password pax12345 — see certs/README).
///   POS_PXRRS_P12_PASS  keystore password (default pax12345)
///   POS_PXRRS_START_FORM  form shown to start a sale (default
///                       StartTransaction — PAX's custom Bloomingdales form).
///                       Set to a stock PxRetail form (e.g. PaymentScreen)
///                       until the custom package is installed; in that mode
///                       CANCEL_PAYMENT re-displays BackgroundScreen.
/// </summary>
public sealed class JpxRestLink : ITerminalLink
{
    // Names from PAX's sequence diagram. TODO(PAX): confirm exact casing in
    // the form package they ship.
    private const string VarRequest = "START_TRANS_REQ_DATA";
    private const string VarResult = "TRANS_RESULT";
    private const string VarForeground = "FOREGROUND";
    private const string EventTransState = "IS_TRANS_STARTED";
    private const string FormStart = "StartTransaction";

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly string _baseUrl;
    private readonly string _notifyUrl;
    private readonly string _startForm;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private WebApplication? _notifyServer;
    private volatile bool _connected;
    private volatile bool _subscribed;

    public JpxRestLink(string? baseUrl = null, string? notifyUrl = null)
    {
        _baseUrl = (baseUrl
            ?? Environment.GetEnvironmentVariable("POS_JPXSS_URL")
            ?? "http://127.0.0.1:9090").TrimEnd('/');
        _notifyUrl = notifyUrl
            ?? Environment.GetEnvironmentVariable("POS_NOTIFY_URL")
            ?? "http://127.0.0.1:8282/notify";
        _startForm = Environment.GetEnvironmentVariable("POS_PXRRS_START_FORM") ?? FormStart;
        _http = new HttpClient(BuildHandler(_baseUrl)) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// PXRRS on the terminal serves HTTPS with the PAX self-signed chain and
    /// REQUIRES a client certificate (mutual TLS) — plain requests are dropped
    /// mid-handshake. JPxSerialServer on localhost stays plain HTTP.
    /// </summary>
    private static HttpMessageHandler BuildHandler(string baseUrl)
    {
        var handler = new HttpClientHandler();
        if (!baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)) return handler;

        // PAX chain is rooted at pxrrs-ca.pax.us (self-signed) — trust it for
        // the demo instead of installing the root into the OS store.
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var p12 = Environment.GetEnvironmentVariable("POS_PXRRS_P12")
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "certs", "pxrrs-integration-client.p12");
        var pass = Environment.GetEnvironmentVariable("POS_PXRRS_P12_PASS") ?? "pax12345";
        if (System.IO.File.Exists(p12))
        {
            handler.ClientCertificates.Add(
                System.Security.Cryptography.X509Certificates.X509CertificateLoader
                    .LoadPkcs12FromFile(p12, pass));
        }
        else
        {
            Console.WriteLine($"[JpxRestLink] WARNING: client cert not found at {p12} — PXRRS will reject us");
        }

        return handler;
    }

    public bool IsConnected => _connected;

    public async Task StartAsync()
    {
        await StartNotifyServerAsync();
        _ = Task.Run(() => MaintainLinkAsync(_cts.Token));
        Console.WriteLine($"[JpxRestLink] driving {_baseUrl}, notify at {_notifyUrl}");
    }

    private async Task StartNotifyServerAsync()
    {
        var uri = new Uri(_notifyUrl);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{uri.Port}");

        _notifyServer = builder.Build();
        _notifyServer.MapPost(uri.AbsolutePath, async context =>
        {
            using var reader = new System.IO.StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            context.Response.StatusCode = StatusCodes.Status200OK;
            HandleNotify(body);
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
                var foregroundOk = await PostAsync($"/setVariable",
                    $$"""{"variables":[{"name":"{{VarForeground}}","value":"false"}]}""");
                var subscribeOk =
                    await PostAsync($"/subscribe?replyURL={Uri.EscapeDataString(_notifyUrl)}", null);
                // FOREGROUND belongs to PAX's custom Bloomingdales package; a
                // stock PxRetail package may not define it, so only require it
                // when we're actually driving the custom form.
                _subscribed = subscribeOk && (foregroundOk || _startForm != FormStart);
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
            return await PostAsync("/displayForm?formName=ThankYouScreen", null);
        }

        if (message.Type is not (PosMessageTypes.StartPayment or PosMessageTypes.CancelPayment))
        {
            return false;
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

        var batch = JsonSerializer.Serialize(new object[]
        {
            new
            {
                commandName = "SetVariable",
                variables = new[] { new { name = VarRequest, value = PosJson.Serialize(message) } },
            },
            new { commandName = "DisplayForm", formName = form },
        });
        return await PostAsync("/sendBatchCmd", batch);
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

    private async Task<bool> ProbeAsync()
    {
        try
        {
            // PXRRS methods are POST-only; GET gets an empty reply.
            var response = await _http.PostAsync($"{_baseUrl}/getPackageList", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
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

    private static bool IsResultOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("resultCode", out var rc)
                && rc.ToString() == "0";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
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
