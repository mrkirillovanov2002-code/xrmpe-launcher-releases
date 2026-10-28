using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Xrmpe.Launcher;

public static class LanNetwork
{
    public static IEnumerable<(IPAddress Address, IPAddress Mask)> Interfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
        foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address) && !unicast.IPv4Mask.Equals(IPAddress.Any))
                yield return (unicast.Address, unicast.IPv4Mask);
    }
    public static bool SameSubnet(IPAddress left, IPAddress right, IPAddress mask) =>
        left.GetAddressBytes().Zip(right.GetAddressBytes()).Zip(mask.GetAddressBytes()).All(x => (x.First.First & x.Second) == (x.First.Second & x.Second));
    public static bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address) || Interfaces().Any(x => SameSubnet(address, x.Address, x.Mask));
    public static IReadOnlyList<IPAddress> Targets() => new[] { IPAddress.Loopback }.Concat(Interfaces().Select(x =>
        new IPAddress(x.Address.GetAddressBytes().Zip(x.Mask.GetAddressBytes()).Select(p => (byte)(p.First | ~p.Second)).ToArray()))).Distinct().Take(8).ToArray();
}

// Only endpoint + transport are advertised, never settings masquerading as live engine statistics.
public sealed class LanPublisher : IAsyncDisposable
{
    public const int DiscoveryPort = 54549;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public LanPublisher(int gamePort, int queryPort, bool steam, Func<bool> processAlive, int discoveryPort = DiscoveryPort)
    {
        if (gamePort is < 1 or > 65535 || queryPort is < 1 or > 65535) throw new ArgumentException("Некорректный порт.");
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
        _loop = Task.Run(async () =>
        {
            var window = DateTime.UtcNow; var replies = 0;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    UdpReceiveResult packet;
                    try { packet = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false); }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { continue; }
                    if (packet.Buffer.Length is < 48 or > 160 || !LanNetwork.IsLocal(packet.RemoteEndPoint.Address) || !processAlive()) continue;
                    var request = Encoding.ASCII.GetString(packet.Buffer).TrimEnd(' ');
                    const string prefix = "XRMPE-DISCOVER/1 ";
                    if (!request.StartsWith(prefix, StringComparison.Ordinal) || request.Length != prefix.Length + 32) continue;
                    var nonce = request[prefix.Length..];
                    if (!nonce.All(Uri.IsHexDigit)) continue;
                    if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; replies = 0; }
                    if (++replies > 10) continue;
                    var response = Encoding.ASCII.GetBytes($"XRMPE-SERVER/1 {nonce} {gamePort} {queryPort} {(steam ? "Steam" : "IP")}");
                    await _udp.SendAsync(response, packet.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _udp.Dispose(); await _loop.ConfigureAwait(false); _stop.Dispose(); }
}

public sealed class LanDiscovery : IServerDiscoveryProvider
{
    public async Task<IReadOnlyList<BrowserServer>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var servers = new Dictionary<string, BrowserServer>();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var request = Encoding.ASCII.GetBytes(("XRMPE-DISCOVER/1 " + nonce).PadRight(128));
        var qrNonce = RandomNumberGenerator.GetBytes(4);
        var qr = Qr2Query.Request(qrNonce);
        var targets = LanNetwork.Targets();
        var receive = Task.Run(async () =>
        {
            try
            {
                while (servers.Count < 128)
                {
                    UdpReceiveResult packet;
                    try { packet = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { continue; }
                    if (!LanNetwork.IsLocal(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 8192) continue;
                    var host = packet.RemoteEndPoint.Address.ToString();
                    var parsed = Qr2Query.Parse(packet.Buffer, qrNonce);
                    if (parsed is { } reply)
                    {
                        var endpoint = new BrowserEndpoint(host, reply.Port, packet.RemoteEndPoint.Port);
                        servers.TryGetValue(endpoint.Key, out var existingNative);
                        servers[endpoint.Key] = new(endpoint with { Steam = existingNative?.Endpoint.Steam == true }, reply.Snapshot,
                            Kind: existingNative?.Kind == "Steam" ? "Steam" : "LAN", LauncherResponded: existingNative?.LauncherResponded == true);
                        continue;
                    }
                    if (packet.RemoteEndPoint.Port != LanPublisher.DiscoveryPort || packet.Buffer.Length > 160) continue;
                    var parts = Encoding.ASCII.GetString(packet.Buffer).Split(' ');
                    if (parts.Length != 5 || parts[0] != "XRMPE-SERVER/1" || parts[1] != nonce ||
                        !int.TryParse(parts[2], out var port) || !int.TryParse(parts[3], out var query) ||
                        port is < 1 or > 65535 || query is < 1 or > 65535 || parts[4] is not ("IP" or "Steam")) continue;
                    var item = new BrowserEndpoint(host, port, query, parts[4] == "Steam");
                    servers.TryGetValue(item.Key, out var existing);
                    servers[item.Key] = new(item, existing?.Snapshot, Kind: item.Steam ? "Steam" : "LAN", LauncherResponded: true);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
        try
        {
            foreach (var ip in targets)
                await udp.SendAsync(request, new IPEndPoint(ip, LanPublisher.DiscoveryPort), timeout.Token).ConfigureAwait(false);
            // OpenXRay's documented LAN QR2 range, bounded and paced. No address-space sweep.
            for (var port = 5445; port <= 5695; port++)
            {
                foreach (var ip in targets) await udp.SendAsync(qr, new IPEndPoint(ip, port), timeout.Token).ConfigureAwait(false);
                await Task.Delay(3, timeout.Token).ConfigureAwait(false);
            }
            await receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { timeout.Cancel(); await receive.ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
        // Broadcast RTT includes send pacing. Re-probe individually for a real request-specific RTT.
        return servers.Values.Select(x => x with { Ping = null }).ToArray();
    }
}
