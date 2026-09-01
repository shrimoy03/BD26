using System;
using System.Threading;
using System.Threading.Tasks;
using MerchantTerminal.Models;

namespace MerchantTerminal.Services;

/// <summary>
/// Owns the live <see cref="ITerminalLink"/> and can swap it at runtime, so the
/// setup screen can point the register at a different terminal IP without a
/// restart. The view model holds the host for the app's lifetime and never sees
/// the swap — events are re-forwarded from whichever link is current.
/// </summary>
public sealed class TerminalLinkHost : ITerminalLink
{
    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly SemaphoreSlim _swap = new(1, 1);
    private ITerminalLink? _inner;

    /// <summary>Settings currently in force (file state, pre-env-override).</summary>
    public PosSettings Settings { get; private set; } = new();

    /// <summary>Resolved config the live link was built from.</summary>
    public LinkConfig Config { get; private set; } = new PosSettings().Resolve();

    public bool IsConnected => _inner?.IsConnected ?? false;

    public Task StartAsync() => ApplyAsync(PosSettings.Load());

    /// <summary>Tear down the current link and bring one up from <paramref name="settings"/>.</summary>
    public async Task ApplyAsync(PosSettings settings)
    {
        await _swap.WaitAsync();
        try
        {
            var wasConnected = IsConnected;

            if (_inner is not null)
            {
                Detach(_inner);
                try
                {
                    await _inner.DisposeAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[TerminalLinkHost] disposing old link: {e.Message}");
                }

                _inner = null;
            }

            // The old link's disconnect never fires once detached, so raise it
            // ourselves — otherwise the UI keeps showing a stale "connected".
            if (wasConnected) ClientDisconnected?.Invoke();

            Settings = settings;
            Config = settings.Resolve();

            var link = Build(Config);
            Attach(link);
            _inner = link;

            if (Config.EnvOverrides.Count > 0)
            {
                Console.WriteLine(
                    $"[TerminalLinkHost] environment overrides in effect: {string.Join(", ", Config.EnvOverrides)}");
            }

            Console.WriteLine($"[TerminalLinkHost] mode={Config.Mode} target={Describe(Config)}");
            await link.StartAsync();
        }
        finally
        {
            _swap.Release();
        }
    }

    public static string Describe(LinkConfig config) => config.Mode switch
    {
        PosSettings.ModeJpxss => config.BaseUrl,
        PosSettings.ModePcl => $"{config.PclHost}:{config.PclPort}",
        _ => $"ws://0.0.0.0:{config.WebSocketPort}/pos",
    };

    private static ITerminalLink Build(LinkConfig config) => config.Mode switch
    {
        // jpxss pairs the PXRRS REST link (PxRetailer forms / EMV) with the
        // WebSocket link (winkpos on the same device) — see CompositeLink.
        PosSettings.ModeJpxss => new CompositeLink(
            new JpxRestLink(config),
            new WebSocketLinkOnPort(config.WebSocketPort)),
        PosSettings.ModePcl => new PclSocketLink(config.PclHost, config.PclPort),
        _ => new WebSocketLinkOnPort(config.WebSocketPort),
    };

    private void Attach(ITerminalLink link)
    {
        link.ClientConnected += OnConnected;
        link.ClientDisconnected += OnDisconnected;
        link.MessageReceived += OnMessage;
    }

    private void Detach(ITerminalLink link)
    {
        link.ClientConnected -= OnConnected;
        link.ClientDisconnected -= OnDisconnected;
        link.MessageReceived -= OnMessage;
    }

    private void OnConnected() => ClientConnected?.Invoke();
    private void OnDisconnected() => ClientDisconnected?.Invoke();
    private void OnMessage(PosMessage m) => MessageReceived?.Invoke(m);

    public Task<bool> SendAsync(PosMessage message) =>
        _inner?.SendAsync(message) ?? Task.FromResult(false);

    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
        {
            Detach(_inner);
            await _inner.DisposeAsync();
            _inner = null;
        }

        _swap.Dispose();
    }
}

/// <summary>
/// <see cref="TerminalLink"/> with its listen port fixed at construction, so it
/// satisfies the parameterless <see cref="ITerminalLink.StartAsync"/> the host
/// calls.
/// </summary>
internal sealed class WebSocketLinkOnPort : ITerminalLink
{
    private readonly TerminalLink _link = new();
    private readonly int _port;

    public WebSocketLinkOnPort(int port) =>
        _port = port <= 0 ? TerminalLink.DefaultPort : port;

    public event Action? ClientConnected
    {
        add => _link.ClientConnected += value;
        remove => _link.ClientConnected -= value;
    }

    public event Action? ClientDisconnected
    {
        add => _link.ClientDisconnected += value;
        remove => _link.ClientDisconnected -= value;
    }

    public event Action<PosMessage>? MessageReceived
    {
        add => _link.MessageReceived += value;
        remove => _link.MessageReceived -= value;
    }

    public bool IsConnected => _link.IsConnected;
    public Task StartAsync() => _link.StartAsync(_port);
    public Task<bool> SendAsync(PosMessage message) => _link.SendAsync(message);
    public ValueTask DisposeAsync() => _link.DisposeAsync();
}
