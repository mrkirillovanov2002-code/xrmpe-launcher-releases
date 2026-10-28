using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xrmpe.Launcher;

void Assert(bool condition, string label) { if (!condition) throw new Exception(label); }
var root = Path.Combine(Path.GetTempPath(), "xrMPE browser тест " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var endpoint = new BrowserEndpoint("LOCALHOST", 5546, 5545).Validate();
    var store = new ServerBrowserStore(Path.Combine(root, "Первый ПК", "server-browser.json"));
    store.Save(new([endpoint, new("127.0.0.1", 5547, 5545, true)], [endpoint.Key], true));
    var restored = store.Load();
    Assert(restored.Saved.Count == 2 && restored.Favorites.Single() == endpoint.Key && restored.Advertise && restored.Saved[1].Steam, "Persistence incl. Steam transport");
    var second = new ServerBrowserStore(Path.Combine(root, "Другой ПК", "server-browser.json"));
    Assert(second.Load().Saved.Count == 0, "Separate computer store");
    foreach (var host in new[] { "127.0.0.1/port=1", "../engine", "a\nb", "http://host", "host:5446" })
    {
        var rejected = false;
        try { new BrowserEndpoint(host).Validate(); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Host injection rejected");
    }
    Console.WriteLine("PASS: saved servers/favorites/Steam transport survive restart; separate directories; address injection rejected.");

    await using var fixture = new Qr2Fixture();
    var server = new BrowserServer(new("127.0.0.1", 5546, fixture.Port));
    var live = await Qr2Query.ProbeAsync(server, CancellationToken.None);
    Assert(live.Snapshot is { Name: "Тестовый QR2", Map: "mp_fixture", Players: 2, MaxPlayers: 32, Mode: "deathmatch" } && live.Ping is >= 0, "Live QR2 fields and RTT");
    fixture.Challenge = true;
    live = await Qr2Query.ProbeAsync(server, CancellationToken.None);
    Assert(live.Snapshot?.Players == 2 && fixture.ChallengeAccepted, "Challenge handshake");
    fixture.Challenge = false;
    fixture.BadNonce = true;
    Assert((await Qr2Query.ProbeAsync(server, CancellationToken.None)).Snapshot is null, "Mismatched nonce rejected");
    fixture.BadNonce = false;
    Assert((await Qr2Query.ProbeAsync(server with { Endpoint = server.Endpoint with { Port = 5547 } }, CancellationToken.None)).Snapshot is null, "Other game port rejected");
    using (var cancel = new CancellationTokenSource(40))
    {
        var canceled = false;
        try { await Qr2Query.ProbeAsync(server with { Endpoint = server.Endpoint with { QueryPort = 1 } }, cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        // An ICMP refusal can correctly finish sooner than cancellation.
        Assert(canceled || cancel.IsCancellationRequested == false, "Bounded unavailable probe");
    }
    var nonce = new byte[] { 1, 2, 3, 4 };
    Assert(Qr2Query.Parse([0, .. nonce, .. Encoding.UTF8.GetBytes("hostname\0Fake\0hostport\05546\0\0")], nonce) is null, "Non-XRay QR2 rejected");
    Assert(Qr2Query.Parse(new byte[9000], nonce) is null, "Oversized response rejected");
    Assert(Qr2Query.Parse([0, .. nonce, .. Encoding.UTF8.GetBytes("hostname\0Fake\nname\0\0")], nonce) is null, "Control characters rejected");
    Console.WriteLine("PASS: real UDP fixture, map/mode/2/32, request RTT, QR2 challenge, wrong nonce/port, bounds and malformed packets.");

    var alive = false;
    await using (var publisher = new LanPublisher(5546, fixture.Port, true, () => alive))
    {
        var hidden = await new LanDiscovery().DiscoverAsync(CancellationToken.None);
        Assert(!hidden.Any(x => x.Endpoint.Port == 5546 && x.Endpoint.QueryPort == fixture.Port), "Not advertised before process start");
        alive = true;
        var found = await new LanDiscovery().DiscoverAsync(CancellationToken.None);
        var lan = found.FirstOrDefault(x => x.Endpoint.Host == "127.0.0.1" && x.Endpoint.Port == 5546 && x.Endpoint.QueryPort == fixture.Port);
        Assert(lan is { Kind: "Steam", LauncherResponded: true, Snapshot: null, Ping: null }, "Endpoint advertised without fabricated engine data");
        var verified = await Qr2Query.ProbeAsync(lan!, CancellationToken.None);
        Assert(verified.Snapshot?.Players == 2, "Discovered endpoint queried independently");
        alive = false;
        found = await new LanDiscovery().DiscoverAsync(CancellationToken.None);
        Assert(!found.Any(x => x.Endpoint.Port == 5546 && x.Endpoint.QueryPort == fixture.Port), "Publication stops on process exit");
    }
    var afterDispose = await new LanDiscovery().DiscoverAsync(CancellationToken.None);
    Assert(!afterDispose.Any(x => x.Endpoint.Port == 5546 && x.Endpoint.QueryPort == fixture.Port), "Discovery socket closed");
    Console.WriteLine("PASS: LAN discovery, process readiness gating, Steam label, no fake data, native probe, stop/dispose.");

    // Actual native protocol on the default LAN QR2 port (only a local test responder).
    await using (var native = new Qr2Fixture(5445))
    {
        var discovered = await new LanDiscovery().DiscoverAsync(CancellationToken.None);
        Assert(discovered.Any(x => x.Endpoint.Port == 5546 && x.Snapshot?.Name == "Тестовый QR2"), "Native LAN QR2 discovery");
    }
    Console.WriteLine("PASS: native QR2 LAN query discovery without a publishing launcher.");
}
finally { Directory.Delete(root, true); }

sealed class Qr2Fixture : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public bool Challenge { get; set; }
    public bool BadNonce { get; set; }
    public bool ChallengeAccepted { get; private set; }
    public Qr2Fixture(int port = 0)
    {
        _udp = new(new IPEndPoint(IPAddress.Loopback, port));
        _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    var request = packet.Buffer;
                    if (request.Length < 7 || request[0] != 0xfe || request[1] != 0xfd) continue;
                    var nonce = request[3..7];
                    if (request[2] == 9)
                    {
                        byte[] challenge = [9, .. nonce, .. Encoding.ASCII.GetBytes((Challenge ? "123456" : "0") + "\0")];
                        await _udp.SendAsync(challenge, packet.RemoteEndPoint, _stop.Token); continue;
                    }
                    if (Challenge)
                    {
                        if (request.Length < 14 || BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(7, 4)) != 123456) continue;
                        ChallengeAccepted = true;
                    }
                    if (BadNonce) nonce[0] ^= 0xff;
                    var fields = new[] { "hostname", "Тестовый QR2", "gamename", "stalkercoppc", "hostport", "5546", "mapname", "mp_fixture", "gametype", "deathmatch", "numplayers", "2", "maxplayers", "32", "gamemode", "openplaying" };
                    byte[] response = [0, .. nonce, .. Encoding.UTF8.GetBytes(string.Join('\0', fields) + "\0\0")];
                    await _udp.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _udp.Dispose(); await _loop; _stop.Dispose(); }
}
