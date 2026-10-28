using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Globalization;

namespace Xrmpe.Launcher;

// Server-key-only QR2 queries: no player identities, commands, DLL loading or master-server traffic.
public static class Qr2Query
{
    public static byte[] Request(byte[] nonce) => [0xfe, 0xfd, 0, .. nonce, 0xff, 0, 0];

    public static (ServerSnapshot Snapshot, int Port)? Parse(byte[] data, byte[] nonce)
    {
        if (data.Length is < 8 or > 8192 || nonce.Length != 4 || data[0] != 0 || !data.AsSpan(1, 4).SequenceEqual(nonce)) return null;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data, 5, data.Length - 5); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(1251).GetString(data, 5, data.Length - 5); }
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = text.Split('\0');
        var terminated = false;
        for (var i = 0; i < parts.Length && i < 256; i += 2)
        {
            if (parts[i].Length == 0) { terminated = true; break; }
            if (i + 1 >= parts.Length || parts[i].Length > 64 || parts[i + 1].Length > 256) return null;
            if (parts[i + 1].Any(char.IsControl)) return null;
            fields[parts[i]] = parts[i + 1];
        }
        if (!terminated || !fields.TryGetValue("hostname", out var name) || string.IsNullOrWhiteSpace(name) ||
            !fields.TryGetValue("hostport", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535) return null;
        // QR2 is shared by many games. Require X-Ray's game identifier or its custom game-type key.
        if (!(fields.GetValueOrDefault("gamename", "").StartsWith("stalker", StringComparison.OrdinalIgnoreCase) || fields.ContainsKey("gametypename"))) return null;
        int? Count(string key) => int.TryParse(fields.GetValueOrDefault(key), out var n) && n is >= 0 and <= 1024 ? n : null;
        var players = Count("numplayers"); var max = Count("maxplayers");
        if (players > max) { players = null; max = null; }
        return (new(name, fields.GetValueOrDefault("mapname", "—"), fields.GetValueOrDefault("gametype", "—"),
            players, max, fields.GetValueOrDefault("gamemode", "неизвестно")), port);
    }

    public static Task<BrowserServer> ProbeAsync(BrowserServer server, CancellationToken token) =>
        Task.Run(() => ProbeCoreAsync(server, token), token);

    private static async Task<BrowserServer> ProbeCoreAsync(BrowserServer server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var ips = await Dns.GetHostAddressesAsync(server.Endpoint.Host, timeout.Token).ConfigureAwait(false);
            var ip = ips.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? throw new SocketException();
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(new IPEndPoint(ip, server.Endpoint.QueryPort));
            var nonce = RandomNumberGenerator.GetBytes(4);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(Request(nonce), timeout.Token).ConfigureAwait(false);
            // A query challenge is optional in QR2. Request it in parallel; a plain response still wins.
            await udp.SendAsync(new byte[] { 0xfe, 0xfd, 9 }.Concat(nonce).ToArray(), timeout.Token).ConfigureAwait(false);
            var challenged = false;
            for (var received = 0; received < 64; received++)
            {
                var response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var bytes = response.Buffer;
                if (!challenged && bytes.Length is >= 7 and <= 32 && bytes[0] == 9 && bytes.AsSpan(1, 4).SequenceEqual(nonce))
                {
                    var text = Encoding.ASCII.GetString(bytes, 5, bytes.Length - 5).TrimEnd('\0');
                    if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var challenge))
                    {
                        challenged = true;
                        byte[] request = challenge == 0 ? Request(nonce) : [0xfe, 0xfd, 0, .. nonce, 0, 0, 0, 0, 0xff, 0, 0];
                        if (challenge != 0) BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(7, 4), challenge);
                        watch.Restart();
                        await udp.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    }
                    continue;
                }
                var parsed = Parse(response.Buffer, nonce);
                if (parsed is { } result && result.Port == server.Endpoint.Port)
                    return server with { Snapshot = result.Snapshot, Ping = (int)watch.ElapsedMilliseconds };
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { token.ThrowIfCancellationRequested(); }
        return server with { Snapshot = null, Ping = null };
    }
}
