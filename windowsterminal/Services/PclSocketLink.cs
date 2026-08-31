using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MerchantTerminal.Models;

namespace MerchantTerminal.Services;

/// <summary>
/// USB path to the customer terminal: connects to JPxSerialServer's raw PCL
/// TCP socket on this machine (enable it in application.properties:
/// serverSocket.enabled=true, serverSocket.port=7001), which relays each PCL
/// frame to the PAX device over USB serial. Same message protocol as
/// TerminalLink; only the transport differs.
///
/// Config via environment variables: POS_PCL_HOST (default 127.0.0.1),
/// POS_PCL_PORT (default 7001).
/// </summary>
public sealed class PclSocketLink : ITerminalLink
{
    public const int DefaultPort = 7001;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<PosMessage>? MessageReceived;

    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;

    public PclSocketLink(string? host = null, int? port = null)
    {
        _host = host
            ?? Environment.GetEnvironmentVariable("POS_PCL_HOST")
            ?? "127.0.0.1";
        _port = port
            ?? (int.TryParse(Environment.GetEnvironmentVariable("POS_PCL_PORT"), out var p) ? p : DefaultPort);
    }

    public bool IsConnected => _stream is not null;

    public Task StartAsync()
    {
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        Console.WriteLine($"[PclSocketLink] connecting to JPxSerialServer at {_host}:{_port}");
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_host, _port, ct);
                _stream = client.GetStream();
                Console.WriteLine("[PclSocketLink] connected");
                ClientConnected?.Invoke();

                await ReceiveLoopAsync(_stream, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[PclSocketLink] connection failed: {e.Message}");
            }
            finally
            {
                if (_stream is not null)
                {
                    _stream = null;
                    ClientDisconnected?.Invoke();
                    Console.WriteLine("[PclSocketLink] disconnected");
                }

                client?.Dispose();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var decoder = new PclFrameCodec.Decoder();
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break; // socket closed

            decoder.Feed(buffer.AsSpan(0, n), tlvs =>
            {
                foreach (var tlv in tlvs)
                {
                    if (tlv.Tag != PclFrameCodec.TagJson) continue;
                    var parsed = PosJson.Deserialize(Encoding.UTF8.GetString(tlv.Value));
                    if (parsed is not null) MessageReceived?.Invoke(parsed);
                }
            });
        }
    }

    public async Task<bool> SendAsync(PosMessage message)
    {
        var stream = _stream;
        if (stream is null) return false;

        var frame = PclFrameCodec.EncodeMessage(message);
        await _sendLock.WaitAsync();
        try
        {
            await stream.WriteAsync(frame);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[PclSocketLink] send failed: {e.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
