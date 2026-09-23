using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MerchantTerminal.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MerchantTerminal.Services;

/// <summary>
/// Hosts a WebSocket endpoint (ws://0.0.0.0:8181/pos) that the Android
/// customer-facing app connects to. One client at a time; a new connection
/// replaces the previous one.
/// </summary>
public sealed class TerminalLink : ITerminalLink
{
    public const int DefaultPort = 8181;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private WebApplication? _app;
    private WebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>
    /// Serial number of the terminal this register is driving (from PXRRS's
    /// replies). Set, only the WinkPay app running ON that terminal is let in:
    /// the app passes its own serial as ?terminal= on the WebSocket URL, and a
    /// mismatch is refused with 403. Without this, a register that had ever
    /// advertised itself to two terminals had both apps connecting and
    /// replacing each other every few seconds, and a sale's result landed on
    /// whichever device held the socket at that instant.
    /// </summary>
    public string? RequiredTerminalSerial { get; set; }

    /// <summary>Serial the connected app reported, if any.</summary>
    public string? ClientTerminalSerial { get; private set; }

    /// <summary>
    /// IP of the terminal this register drives. The WinkPay app runs on that
    /// very device, so a client from any other address is another terminal's
    /// app — refused even when it sends no serial (older app builds, or an app
    /// that could not learn its serial from PXRRS).
    /// </summary>
    public string? RequiredTerminalHost { get; set; }

    public Task StartAsync() => StartAsync(DefaultPort);

    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/pos", HandleClientAsync);

        await _app.StartAsync();
        Console.WriteLine($"[TerminalLink] listening on ws://0.0.0.0:{port}/pos");
    }

    private async Task HandleClientAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is { IsIPv4MappedToIPv6: true }) remoteAddress = remoteAddress.MapToIPv4();
        var remote = remoteAddress?.ToString() ?? "?";
        var clientSerial = context.Request.Query["terminal"].ToString().Trim();
        var required = RequiredTerminalSerial;
        var requiredHost = RequiredTerminalHost;

        string? refusal = null;
        if (!string.IsNullOrEmpty(required) && !string.IsNullOrEmpty(clientSerial)
            && !clientSerial.Equals(required, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"terminal {clientSerial} is not the one this register drives ({required})";
        }
        else if (string.IsNullOrEmpty(clientSerial) && !string.IsNullOrEmpty(requiredHost)
            && !remote.Equals(requiredHost, StringComparison.OrdinalIgnoreCase)
            && !IPAddress.IsLoopback(remoteAddress ?? IPAddress.None))
        {
            refusal = $"client at {remote} sent no terminal serial and is not the driven terminal ({requiredHost})";
        }

        if (refusal is not null)
        {
            // Not the terminal this register drives: refuse before the upgrade
            // so the app sees a clean 403 and drops this address from its list.
            Console.WriteLine($"[TerminalLink] refused {remote} — {refusal}");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers["X-Reject-Reason"] = "wrong-terminal";
            await context.Response.WriteAsync($"this register drives terminal {required ?? requiredHost}");
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is not null)
        {
            Console.WriteLine("[TerminalLink] replacing the previous client");
            try { previous.Abort(); } catch { /* replaced connection */ }
        }

        ClientTerminalSerial = string.IsNullOrEmpty(clientSerial) ? null : clientSerial;
        Console.WriteLine(
            $"[TerminalLink] client connected from {remote}"
            + (ClientTerminalSerial is null
                ? (required is null ? "" : " (no terminal serial sent — older app build)")
                : $" (terminal {ClientTerminalSerial})"));
        ClientConnected?.Invoke();
        await ReceiveLoopAsync(socket);

        if (Interlocked.CompareExchange(ref _socket, null, socket) == socket)
        {
            Console.WriteLine("[TerminalLink] client disconnected");
            ClientDisconnected?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        var message = new StringBuilder();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var parsed = PosJson.Deserialize(message.ToString());
                message.Clear();
                if (parsed is not null)
                {
                    MessageReceived?.Invoke(parsed);
                }
            }
        }
        catch (WebSocketException)
        {
            // Client dropped; the disconnect event fires in HandleClientAsync.
        }
    }

    /// <summary>
    /// Drop the connected app on purpose (this register lost the terminal to
    /// another one). The app reconnects, re-reads the advertised register
    /// address from its terminal and lands on the new owner — so the app
    /// itself only needs to poll for the owner rarely.
    /// </summary>
    public void DropClient(string reason)
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null) return;
        Console.WriteLine($"[TerminalLink] dropping client ({reason})");
        try { socket.Abort(); } catch { /* already gone */ }
        ClientDisconnected?.Invoke();
    }

    public async Task<bool> SendAsync(PosMessage message)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return false;

        var bytes = Encoding.UTF8.GetBytes(PosJson.Serialize(message));
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _socket?.Abort(); } catch { /* shutting down */ }
        if (_app is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
    }
}
