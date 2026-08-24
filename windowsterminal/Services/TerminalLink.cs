using System;
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
public sealed class TerminalLink : IAsyncDisposable
{
    public const int DefaultPort = 8181;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private WebApplication? _app;
    private WebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    public async Task StartAsync(int port = DefaultPort)
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

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is not null)
        {
            try { previous.Abort(); } catch { /* replaced connection */ }
        }

        Console.WriteLine("[TerminalLink] client connected");
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
