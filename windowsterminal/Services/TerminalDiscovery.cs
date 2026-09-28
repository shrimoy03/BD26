using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MerchantTerminal.Services;

/// <summary>A terminal that answered a discovery probe.</summary>
public sealed record DiscoveredTerminal(string Serial, string Model, string Host, string Via)
{
    public string Label => $"{Model} · {Serial}";
}

/// <summary>
/// Finds WinkPay terminals on the local links without anyone reading an
/// address off a screen. The register sends a one-line probe to the IPv4
/// broadcast address and to the IPv6 all-nodes group (ff02::1) on every
/// usable interface; the WinkPay app's discovery responder (UDP 8182) answers
/// with the terminal's serial, model and addresses.
///
/// Why IPv6 matters: a phone hotspot on an IPv6-only carrier (T-Mobile
/// iPhones, for one) gives every client a private 192.0.0.x/32 translator
/// address instead of a LAN — clients cannot reach each other over IPv4 at
/// all, while the hotspot's IPv6 /64 is on-link and works. The probe goes
/// out both ways and the reply's own source address tells us which one
/// actually routes.
/// </summary>
public static class TerminalDiscovery
{
    public const int Port = 8182;
    public const string Probe = "WINKPOS_DISCOVER 1";

    public static async Task<IReadOnlyList<DiscoveredTerminal>> ScanAsync(TimeSpan window, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredTerminal>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();
        var probe = Encoding.UTF8.GetBytes(Probe);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }

            var v4 = props.UnicastAddresses
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !PosSettings.IsLinkLocal(a))
                .ToList();
            foreach (var local in v4)
            {
                tasks.Add(ProbeV4Async(local, probe, window, found, ct));
            }

            var hasV6 = props.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(u.Address));
            if (hasV6)
            {
                int index;
                try { index = props.GetIPv6Properties().Index; }
                catch { try { index = props.GetIPv4Properties().Index; } catch { continue; } }
                var globals = props.UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !IPAddress.IsLoopback(a) && !a.IsIPv6Multicast)
                    .ToList();
                tasks.Add(ProbeV6Async(index, globals, probe, window, found, ct));
            }
        }

        try { await Task.WhenAll(tasks); } catch { /* individual probes log their own failures */ }
        return found.Values.OrderBy(t => t.Label).ToList();
    }

    private static async Task ProbeV4Async(IPAddress local, byte[] probe, TimeSpan window,
        Dictionary<string, DiscoveredTerminal> found, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(local, 0));
            await socket.SendToAsync(probe, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, Port));
            await CollectAsync(socket, window, found, ct, replyAddress =>
            {
                if (replyAddress.IsIPv4MappedToIPv6) replyAddress = replyAddress.MapToIPv4();
                return replyAddress.AddressFamily == AddressFamily.InterNetwork ? replyAddress.ToString() : null;
            }, "IPv4");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Discovery] IPv4 probe from {local} failed: {e.Message}");
        }
    }

    private static async Task ProbeV6Async(int ifIndex, List<IPAddress> ourGlobals, byte[] probe, TimeSpan window,
        Dictionary<string, DiscoveredTerminal> found, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, ifIndex);
            var allNodes = new IPAddress(IPAddress.Parse("ff02::1").GetAddressBytes(), ifIndex);
            await socket.SendToAsync(probe, SocketFlags.None, new IPEndPoint(allNodes, Port));
            await CollectAsync(socket, window, found, ct, replyAddress => null, "IPv6", (reply, replyAddress) =>
            {
                // Prefer one of the terminal's global addresses on our own /64
                // (on-link, no scope id needed); a global reply source is next.
                foreach (var s in reply.IPv6)
                {
                    if (!IPAddress.TryParse(s, out var a) || a.IsIPv6LinkLocal) continue;
                    if (ourGlobals.Any(g => SamePrefix64(g, a))) return a.ToString();
                }
                if (!replyAddress.IsIPv6LinkLocal && replyAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    return replyAddress.ToString();
                foreach (var s in reply.IPv6)
                {
                    if (IPAddress.TryParse(s, out var a) && !a.IsIPv6LinkLocal) return a.ToString();
                }
                return null;
            });
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Discovery] IPv6 probe on interface {ifIndex} failed: {e.Message}");
        }
    }

    private sealed class Reply
    {
        public string? Serial { get; set; }
        public string? Model { get; set; }
        public List<string> IPv4 { get; set; } = new();
        public List<string> IPv6 { get; set; } = new();
    }

    private static async Task CollectAsync(Socket socket, TimeSpan window, Dictionary<string, DiscoveredTerminal> found,
        CancellationToken ct, Func<IPAddress, string?> hostFromSource, string via,
        Func<Reply, IPAddress, string?>? hostFromReply = null)
    {
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + window;
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(remaining);
            SocketReceiveFromResult r;
            try { r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            var source = ((IPEndPoint)r.RemoteEndPoint).Address;
            Reply? reply;
            try
            {
                reply = JsonSerializer.Deserialize<Reply>(Encoding.UTF8.GetString(buffer, 0, r.ReceivedBytes),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { continue; }
            if (reply?.Serial is null) continue;

            var host = hostFromReply?.Invoke(reply, source) ?? hostFromSource(source);
            if (host is null) continue;
            var t = new DiscoveredTerminal(reply.Serial, reply.Model ?? "PAX", host, via);
            Console.WriteLine($"[Discovery] {t.Label} at {host} ({via}, reply from {source})");
            lock (found)
            {
                // An IPv4 answer beats an IPv6 one for the same terminal only
                // when it is a real LAN address (not a hotspot translator).
                if (!found.TryGetValue(t.Serial, out var existing) || Better(t, existing)) found[t.Serial] = t;
            }
        }
    }

    private static bool Better(DiscoveredTerminal candidate, DiscoveredTerminal existing)
    {
        if (candidate.Via == existing.Via) return false;
        var candidateV4 = candidate.Via == "IPv4";
        var v4 = candidateV4 ? candidate : existing;
        var usableV4 = IPAddress.TryParse(v4.Host, out var a) && !IsHotspotTranslator(a);
        return candidateV4 ? usableV4 : !usableV4;
    }

    /// <summary>192.0.0.0/29 — RFC 7335 IPv4 service continuity, i.e. a CLAT, not a LAN.</summary>
    public static bool IsHotspotTranslator(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && b[0] == 192 && b[1] == 0 && b[2] == 0 && b[3] < 8;
    }

    public static bool SamePrefix64(IPAddress a, IPAddress b)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != 16 || y.Length != 16) return false;
        for (var i = 0; i < 8; i++) if (x[i] != y[i]) return false;
        return true;
    }
}
