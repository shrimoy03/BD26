using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MerchantTerminal.Services;

namespace MerchantTerminal.ViewModels;

/// <summary>
/// Backs the setup screen (F9): type the terminal's IP, test it, save it.
/// Edits a working copy of <see cref="PosSettings"/> so Cancel really cancels;
/// Save writes %APPDATA%\MerchantTerminal\settings.json and rebuilds the live
/// link in place.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private readonly TerminalLinkHost? _host;
    private readonly Action? _close;

    public SetupViewModel() : this(null, null) { }

    public SetupViewModel(TerminalLinkHost? host, Action? close)
    {
        _host = host;
        _close = close;
        LoadFrom(host?.Settings ?? PosSettings.Load());
    }

    // ----- editable fields -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpxss), nameof(IsWebSocket), nameof(IsPcl), nameof(TargetPreview))]
    public partial string Mode { get; set; } = PosSettings.ModeWebSocket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPreview), nameof(NotifyUrlPreview))]
    public partial string TerminalHost { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPreview))]
    public partial string TerminalPort { get; set; } = PosSettings.DefaultPxrrsPort.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPreview), nameof(TlsLabel))]
    public partial bool UseTls { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotifyUrlPreview))]
    public partial string NotifyPort { get; set; } = PosSettings.DefaultNotifyPort.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotifyUrlPreview))]
    public partial string NotifyHost { get; set; } = "";

    [ObservableProperty]
    public partial string StartForm { get; set; } = PosSettings.DefaultStartForm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CertStatus), nameof(CertFound))]
    public partial string ClientCertPath { get; set; } = "";

    [ObservableProperty]
    public partial string ClientCertPassword { get; set; } = PosSettings.DefaultCertPassword;

    [ObservableProperty]
    public partial string WebSocketPort { get; set; } = Services.TerminalLink.DefaultPort.ToString();

    [ObservableProperty]
    public partial string PclHost { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial string PclPort { get; set; } = PclSocketLink.DefaultPort.ToString();

    // ----- transient UI state -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string Result { get; set; } = "";

    [ObservableProperty]
    public partial bool ResultIsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyLabel))]
    public partial bool IsBusy { get; set; }

    public bool HasResult => Result.Length > 0;
    public string BusyLabel => IsBusy ? "Testing…" : "Test connection";

    // ----- radio-button adapters -----

    public bool IsJpxss
    {
        get => Mode == PosSettings.ModeJpxss;
        set { if (value) Mode = PosSettings.ModeJpxss; }
    }

    public bool IsWebSocket
    {
        get => Mode == PosSettings.ModeWebSocket;
        set { if (value) Mode = PosSettings.ModeWebSocket; }
    }

    public bool IsPcl
    {
        get => Mode == PosSettings.ModePcl;
        set { if (value) Mode = PosSettings.ModePcl; }
    }

    // ----- previews -----

    public string TlsLabel => UseTls
        ? "HTTPS + client certificate (PXRRS on the terminal)"
        : "Plain HTTP (JPxSerialServer on this PC)";

    public string TargetPreview => Draft().DefaultBaseUrl();

    public string NotifyUrlPreview => Draft().DefaultNotifyUrl();

    public string SettingsPath => PosSettings.FilePath;

    public string DetectedLanIp =>
        PosSettings.LocalAddressFor(TerminalHost, ParsePort(TerminalPort, PosSettings.DefaultPxrrsPort))
        ?? "none detected";

    public bool CertFound => File.Exists(EffectiveCertPath);

    public string EffectiveCertPath => string.IsNullOrWhiteSpace(ClientCertPath)
        ? JpxRestLink.DefaultCertPath
        : ClientCertPath.Trim();

    public string CertStatus => CertFound
        ? $"Found · {EffectiveCertPath}"
        : $"Missing · {EffectiveCertPath}";

    /// <summary>Environment variables currently overriding the saved settings.</summary>
    public string EnvOverrideNote
    {
        get
        {
            var overrides = Draft().Resolve().EnvOverrides;
            return overrides.Count == 0
                ? ""
                : "Environment is overriding these — unset them for the saved values to apply: "
                  + string.Join(", ", overrides);
        }
    }

    public bool HasEnvOverrides => EnvOverrideNote.Length > 0;

    // ----- commands -----

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy) return;

        var draft = Draft();
        if (draft.LinkMode != PosSettings.ModeJpxss)
        {
            Show(false, "Test only applies to the wireless PXRRS / JPxSerialServer mode.");
            return;
        }

        if (!IsHostValid())
        {
            Show(false, "Enter the terminal's IP address, e.g. 192.168.1.234");
            return;
        }

        IsBusy = true;
        Show(false, $"Contacting {draft.DefaultBaseUrl()}…");
        ResultIsError = false;

        var (ok, message) = await JpxRestLink.TestAsync(draft.Resolve());

        IsBusy = false;
        Show(!ok, message);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var draft = Draft();

        if (draft.LinkMode == PosSettings.ModeJpxss && !IsHostValid())
        {
            Show(true, "Enter the terminal's IP address, e.g. 192.168.1.234");
            return;
        }

        try
        {
            draft.Save();
        }
        catch (Exception e)
        {
            Show(true, $"Could not save {PosSettings.FilePath}: {e.Message}");
            return;
        }

        if (_host is null)
        {
            _close?.Invoke();
            return;
        }

        IsBusy = true;
        Show(false, "Saved — reconnecting…");
        try
        {
            await _host.ApplyAsync(draft);
        }
        catch (Exception e)
        {
            IsBusy = false;
            Show(true, $"Saved, but the link failed to start: {e.Message}");
            return;
        }

        IsBusy = false;
        _close?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        LoadFrom(_host?.Settings ?? PosSettings.Load());
        Result = "";
        _close?.Invoke();
    }

    /// <summary>Re-read the detected LAN address and cert state.</summary>
    [RelayCommand]
    private void Rescan()
    {
        OnPropertyChanged(nameof(DetectedLanIp));
        OnPropertyChanged(nameof(NotifyUrlPreview));
        OnPropertyChanged(nameof(CertStatus));
        OnPropertyChanged(nameof(CertFound));
        OnPropertyChanged(nameof(EnvOverrideNote));
        OnPropertyChanged(nameof(HasEnvOverrides));
    }

    // ----- helpers -----

    /// <summary>Called by the view when the operator picks a .p12 from disk.</summary>
    public void SetCertificatePath(string path)
    {
        ClientCertPath = path;
        Show(false, $"Client certificate set to {path}");
    }

    private void Show(bool isError, string message)
    {
        ResultIsError = isError;
        Result = message;
    }

    private bool IsHostValid() => !string.IsNullOrWhiteSpace(TerminalHost)
        && !TerminalHost.Contains(' ')
        && !TerminalHost.Contains('/');

    /// <summary>The edited fields as a <see cref="PosSettings"/>.</summary>
    private PosSettings Draft() => new()
    {
        LinkMode = Mode,
        TerminalHost = TerminalHost.Trim(),
        TerminalPort = ParsePort(TerminalPort, PosSettings.DefaultPxrrsPort),
        UseTls = UseTls,
        NotifyPort = ParsePort(NotifyPort, PosSettings.DefaultNotifyPort),
        NotifyHost = NotifyHost.Trim(),
        StartForm = string.IsNullOrWhiteSpace(StartForm) ? PosSettings.DefaultStartForm : StartForm.Trim(),
        ClientCertPath = ClientCertPath.Trim(),
        ClientCertPassword = ClientCertPassword,
        WebSocketPort = ParsePort(WebSocketPort, Services.TerminalLink.DefaultPort),
        PclHost = PclHost.Trim(),
        PclPort = ParsePort(PclPort, PclSocketLink.DefaultPort),
    };

    private void LoadFrom(PosSettings s)
    {
        Mode = s.LinkMode;
        TerminalHost = s.TerminalHost;
        TerminalPort = s.TerminalPort.ToString();
        UseTls = s.UseTls;
        NotifyPort = s.NotifyPort.ToString();
        NotifyHost = s.NotifyHost;
        StartForm = s.StartForm;
        ClientCertPath = s.ClientCertPath;
        ClientCertPassword = s.ClientCertPassword;
        WebSocketPort = s.WebSocketPort.ToString();
        PclHost = s.PclHost;
        PclPort = s.PclPort.ToString();
        Rescan();
    }

    private static int ParsePort(string text, int fallback) =>
        int.TryParse(text?.Trim(), out var value) && value is > 0 and <= 65535 ? value : fallback;
}
