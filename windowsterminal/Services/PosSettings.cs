using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerchantTerminal.Services;

/// <summary>
/// Per-register configuration, persisted to
/// <c>%APPDATA%\MerchantTerminal\settings.json</c>. A fresh install only needs
/// the terminal's IP typed into the setup screen (F9) — no environment
/// variables, no rebuild, nothing to edit by hand.
///
/// This type holds the *file* state only, which is what the setup screen edits
/// and saves. <see cref="Resolve"/> layers environment variables on top to
/// produce the <see cref="LinkConfig"/> the transports actually run with, so
/// the README workflows and the <c>tools/fake-*.mjs</c> harnesses keep working
/// unchanged. Anything the environment is holding is listed in
/// <see cref="LinkConfig.EnvOverrides"/> and shown read-only in the UI rather
/// than silently baked into the file.
/// </summary>
public sealed class PosSettings
{
    public const string ModeWebSocket = "websocket";
    public const string ModeJpxss = "jpxss";
    public const string ModePcl = "pcl";

    public const int DefaultPxrrsPort = 9090;
    public const int DefaultNotifyPort = 8080;
    public const string DefaultTerminalHost = "192.168.1.206";
    public const string DefaultStartForm = "StartTransaction";
    public const string StockStartForm = "PaymentScreen";
    public const string DefaultCertPassword = "pax12345";

    /// <summary>websocket | jpxss | pcl — see <c>App.axaml.cs</c>.</summary>
    public string LinkMode { get; set; } = ModeWebSocket;

    // ----- jpxss mode: PXRRS on the terminal, or JPxSerialServer on this PC -----

    /// <summary>
    /// Terminal IP for wireless PXRRS, or 127.0.0.1 when going through
    /// JPxSerialServer over USB on this machine. Defaults to the demo A3700's
    /// address; change it in the setup screen (F9).
    /// </summary>
    public string TerminalHost { get; set; } = DefaultTerminalHost;

    public int TerminalPort { get; set; } = DefaultPxrrsPort;

    /// <summary>
    /// PXRRS on the terminal serves HTTPS and requires a client certificate;
    /// JPxSerialServer on localhost stays plain HTTP.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Port we host the notify callback on for PXRRS to POST results to.</summary>
    public int NotifyPort { get; set; } = DefaultNotifyPort;

    /// <summary>
    /// Blank = auto-detect the LAN address the terminal would reach us on.
    /// Set it only to pin a specific NIC on a multi-homed register.
    /// </summary>
    public string NotifyHost { get; set; } = "";

    /// <summary>
    /// Path of the callback endpoint. PAX's own sample ECR uses
    /// /api/pxserv/notify and that is the shape their terminals are tested
    /// against, so it is the default here; the listener accepts any path
    /// regardless.
    /// </summary>
    public string NotifyPath { get; set; } = "/api/pxserv/notify";

    /// <summary>
    /// Whether the register registers its own callback with /subscribe.
    /// Our subscription is accepted (resultCode 0, and getSubscriptionData
    /// reports our address) yet no notification is ever delivered, while the
    /// same replyURL registered by PAX's RetailDemoApplication does receive
    /// them. Setting this false leaves the subscription alone and just listens
    /// on the callback, so the demo app can own it and the events still land
    /// here. Unblocks the demo while PAX explain the difference.
    /// </summary>
    public bool ManageSubscription { get; set; } = true;

    /// <summary>
    /// Serve the notify callback over HTTPS and advertise it as https://.
    /// PAX's own RetailDemoApplication subscribes with an https replyURL, and
    /// PXRRS appears not to deliver to a plain-http one.
    /// </summary>
    public bool NotifyUseTls { get; set; } = true;

    /// <summary>Blank = the bundled <c>certs/pxrrs-notify-server.p12</c>.</summary>
    public string NotifyCertPath { get; set; } = "";

    public string NotifyCertPassword { get; set; } = DefaultCertPassword;

    /// <summary>
    /// PAX's custom Bloomingdale's form, or <see cref="StockStartForm"/> until
    /// that package is installed on the device.
    /// </summary>
    public string StartForm { get; set; } = DefaultStartForm;

    /// <summary>
    /// Form the customer lands on when they pick the biometric tender, used as
    /// a pollable stand-in for the sequence diagram's IS_TRANS_STARTED notify.
    /// PXRRS does not dispatch custom form events to REST subscribers (verified
    /// on an A3700: a button press changes no variable and delivers no
    /// callback), but SYS.STR.NEXTSCREEN does track the displayed form — so
    /// point the Face button at a form and watch for it. Blank disables it.
    /// </summary>
    public string BiometricTriggerForm { get; set; } = "";

    /// <summary>FACE or PALM — which tender <see cref="BiometricTriggerForm"/> means.</summary>
    public string BiometricTriggerMethod { get; set; } = "FACE";

    /// <summary>
    /// Variable the tender button writes with PxDesigner's SetVariable action,
    /// holding "face" or "palm". This is the supported way to signal the
    /// register: FireEvent relies on PXRRS dispatching a notification, which it
    /// does not do on this terminal, whereas a variable can simply be polled.
    /// Create it in PxDesigner's variable manager (or reuse a spare stock
    /// STR.* variable) and set the same name here. Blank disables it.
    /// </summary>
    public string BiometricTriggerVariable { get; set; } = "";

    // ----- Mailbox variables -----
    //
    // The sequence diagram pushes IS_TRANS_STARTED to both parties via notify,
    // but PXRRS on this terminal accepts a subscription and then never posts
    // (reproducible with emvDetectICCard, no custom form involved). Every other
    // arrow in the diagram is already get/setVariable, and the event is really
    // just a state flag — so the whole flow runs by polling these three, with
    // no notify at all. Defaults are stock PxRetail variables, verified to
    // round-trip a full order JSON; point them at PAX's custom names once that
    // package is installed.

    /// <summary>Order details, register -> WinkPay (diagram: START_TRANS_REQ_DATA).</summary>
    public string RequestVariable { get; set; } = "STR.GENERIC_1";

    /// <summary>Handshake flag (diagram: IS_TRANS_STARTED). 0 idle, 1 order ready, 2 result ready.</summary>
    public string StateVariable { get; set; } = "STR.GENERIC_2";

    /// <summary>Biometric outcome, WinkPay -> register (diagram: TRANS_RESULT).</summary>
    public string ResultVariable { get; set; } = "STR.TRANSACTION_RESULT";

    /// <summary>Blank = the bundled <c>certs/pxrrs-integration-client.p12</c>.</summary>
    public string ClientCertPath { get; set; } = "";

    public string ClientCertPassword { get; set; } = DefaultCertPassword;

    // ----- websocket mode: winkpos connects in to us -----

    public int WebSocketPort { get; set; } = TerminalLink.DefaultPort;

    // ----- pcl mode: JPxSerialServer's raw socket -----

    public string PclHost { get; set; } = "127.0.0.1";
    public int PclPort { get; set; } = PclSocketLink.DefaultPort;

    // ================= persistence =================

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MerchantTerminal",
        "settings.json");

    public static PosSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                if (JsonSerializer.Deserialize<PosSettings>(json, JsonOptions) is { } loaded)
                {
                    return loaded;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[PosSettings] could not read {FilePath}: {e.Message} — using defaults");
        }

        return new PosSettings();
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        Console.WriteLine($"[PosSettings] saved {path}");
    }

    public PosSettings Clone() => (PosSettings)MemberwiseClone();

    // ================= resolution =================

    /// <summary>
    /// Layer environment variables over the file state. Env wins so the
    /// documented <c>POS_*</c> workflows keep overriding a configured register.
    /// </summary>
    public LinkConfig Resolve()
    {
        var env = new List<string>();

        string? Env(string key)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value)) return null;
            env.Add(key);
            return value;
        }

        var mode = Env("POS_TERMINAL_LINK") ?? LinkMode;

        var baseUrl = (Env("POS_JPXSS_URL") ?? DefaultBaseUrl()).TrimEnd('/');
        var notifyUrl = Env("POS_NOTIFY_URL") ?? DefaultNotifyUrl();
        var startForm = Env("POS_PXRRS_START_FORM") ?? StartForm;
        var certPath = Env("POS_PXRRS_P12") ?? ClientCertPath;
        var certPass = Env("POS_PXRRS_P12_PASS") ?? ClientCertPassword;
        var pclHost = Env("POS_PCL_HOST") ?? PclHost;
        var pclPort = int.TryParse(Env("POS_PCL_PORT"), out var p) ? p : PclPort;

        return new LinkConfig(
            Mode: mode,
            BaseUrl: baseUrl,
            NotifyUrl: notifyUrl,
            StartForm: string.IsNullOrWhiteSpace(startForm) ? DefaultStartForm : startForm,
            CertPath: certPath,
            CertPassword: string.IsNullOrWhiteSpace(certPass) ? DefaultCertPassword : certPass,
            RequestVariable: RequestVariable,
            StateVariable: StateVariable,
            ResultVariable: ResultVariable,
            TriggerForm: Env("POS_BIOMETRIC_TRIGGER_FORM") ?? BiometricTriggerForm,
            TriggerVariable: Env("POS_BIOMETRIC_TRIGGER_VAR") ?? BiometricTriggerVariable,
            TriggerMethod: string.IsNullOrWhiteSpace(BiometricTriggerMethod)
                ? "FACE"
                : BiometricTriggerMethod.Trim().ToUpperInvariant(),
            ManageSubscription: ManageSubscription,
            NotifyCertPath: Env("POS_NOTIFY_P12") ?? NotifyCertPath,
            NotifyCertPassword: string.IsNullOrWhiteSpace(NotifyCertPassword)
                ? DefaultCertPassword
                : NotifyCertPassword,
            WebSocketPort: WebSocketPort,
            PclHost: pclHost,
            PclPort: pclPort,
            EnvOverrides: env);
    }

    /// <summary>REST base for jpxss mode, from the host/port/TLS fields.</summary>
    public string DefaultBaseUrl()
    {
        var host = string.IsNullOrWhiteSpace(TerminalHost) ? "127.0.0.1" : TerminalHost.Trim();
        var scheme = UseTls ? "https" : "http";
        return $"{scheme}://{host}:{TerminalPort}";
    }

    /// <summary>
    /// Where PXRRS should POST results. The terminal has to be able to reach
    /// this, so we advertise the LAN address that routes to it — not 127.0.0.1
    /// and not one of Windows' 169.254 link-local addresses.
    /// </summary>
    public string DefaultNotifyUrl()
    {
        var host = string.IsNullOrWhiteSpace(NotifyHost)
            ? LocalAddressFor(TerminalHost, TerminalPort) ?? "127.0.0.1"
            : NotifyHost.Trim();
        var scheme = NotifyUseTls ? "https" : "http";
        var path = string.IsNullOrWhiteSpace(NotifyPath) ? "/notify" : NotifyPath.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{scheme}://{host}:{NotifyPort}{path}";
    }

    // ================= network helpers =================

    /// <summary>
    /// The local IPv4 the OS would use to reach <paramref name="remoteHost"/>.
    /// A connected UDP socket sends no packets but binds the source address the
    /// routing table picked, which is exactly the address the terminal will see
    /// us come from. Returns null when the host is unset/unroutable.
    /// </summary>
    public static string? LocalAddressFor(string? remoteHost, int remotePort)
    {
        if (string.IsNullOrWhiteSpace(remoteHost)) return FirstLanAddress();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remoteHost.Trim(), remotePort <= 0 ? DefaultPxrrsPort : remotePort);
            var local = (socket.LocalEndPoint as IPEndPoint)?.Address;
            if (local is not null && !IPAddress.IsLoopback(local) && !IsLinkLocal(local))
            {
                return local.ToString();
            }
        }
        catch (SocketException)
        {
            // Terminal unplugged / bad IP — fall through to the NIC scan.
        }

        return FirstLanAddress();
    }

    /// <summary>Best-guess LAN address: an up, non-virtual NIC with a gateway.</summary>
    public static string? FirstLanAddress()
    {
        var candidates =
            from nic in NetworkInterface.GetAllNetworkInterfaces()
            where nic.OperationalStatus == OperationalStatus.Up
                  && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                  && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel
            let props = nic.GetIPProperties()
            let hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any))
            from addr in props.UnicastAddresses
            where addr.Address.AddressFamily == AddressFamily.InterNetwork
                  && !IPAddress.IsLoopback(addr.Address)
                  && !IsLinkLocal(addr.Address)
            orderby hasGateway descending,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet descending
            select addr.Address.ToString();

        return candidates.FirstOrDefault();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }
}

/// <summary>
/// Fully-resolved transport configuration: <see cref="PosSettings"/> with
/// environment overrides applied. This is what the links are constructed from.
/// </summary>
public sealed record LinkConfig(
    string Mode,
    string BaseUrl,
    string NotifyUrl,
    string StartForm,
    string CertPath,
    string CertPassword,
    string RequestVariable,
    string StateVariable,
    string ResultVariable,
    string TriggerForm,
    string TriggerVariable,
    string TriggerMethod,
    bool ManageSubscription,
    string NotifyCertPath,
    string NotifyCertPassword,
    int WebSocketPort,
    string PclHost,
    int PclPort,
    IReadOnlyList<string> EnvOverrides);
