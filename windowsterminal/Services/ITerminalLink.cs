using System;
using System.Threading.Tasks;
using MerchantTerminal.Models;

namespace MerchantTerminal.Services;

/// <summary>
/// Link to the customer-facing terminal. Two implementations:
/// <see cref="TerminalLink"/> hosts a WebSocket server the Android app
/// connects to over Wi-Fi; <see cref="PclSocketLink"/> connects out to
/// JPxSerialServer's raw PCL TCP socket, which relays frames to the terminal
/// over USB serial. Selected in App.axaml.cs via POS_TERMINAL_LINK.
/// </summary>
public interface ITerminalLink : IAsyncDisposable
{
    event Action? ClientConnected;
    event Action? ClientDisconnected;
    event Action<PosMessage>? MessageReceived;

    bool IsConnected { get; }

    Task StartAsync();

    Task<bool> SendAsync(PosMessage message);
}
