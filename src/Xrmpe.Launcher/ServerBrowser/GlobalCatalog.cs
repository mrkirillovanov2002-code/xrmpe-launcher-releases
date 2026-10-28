using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Xrmpe.Launcher;

public sealed record CatalogPreferences(string Address = "https://xrmpe-server-browser.mr-kirillovanov2002.workers.dev", bool Publish = false, string BackupAddress = "");
public sealed record CatalogEntry(string Host, int Port, int QueryPort, bool Steam, long Expires);
public sealed record CatalogList(int Schema, List<CatalogEntry> Servers);
public sealed record CatalogPublicationTiming(TimeSpan Startup, TimeSpan Heartbeat, TimeSpan Poll)
{
    public static readonly CatalogPublicationTiming Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
}

public sealed class GlobalCatalog : IServerDiscoveryProvider, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly CatalogPublicationTiming _timing;
    public static Uri ValidateAddress(string address)
    {
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Каталог: HTTPS-адрес без пароля, пути и параметров. HTTP разрешён только для localhost-тестов.");
        return uri;
    }
    public GlobalCatalog(string address, HttpMessageHandler? testHandler = null, CatalogPublicationTiming? testTiming = null)
    {
        _root = ValidateAddress(address);
        _timing = testTiming ?? CatalogPublicationTiming.Default;
        _http = new(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectCallback = async (context, ct) =>
            {
                // xrMPE direct connections use IPv4; registration must observe that same family.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var ip = addresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? throw new HttpRequestException("У каталога нет IPv4-адреса.");
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try { await socket.ConnectAsync(ip, context.DnsEndPoint.Port, ct).ConfigureAwait(false); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    public static bool PublicIPv4(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.ToString() != host) return false;
        var n = ip.GetAddressBytes();
        return n[0] is not (0 or 10 or 127) && n[0] < 224 &&
            !(n[0] == 100 && n[1] is >= 64 and <= 127) && !(n[0] == 169 && n[1] == 254) &&
            !(n[0] == 172 && n[1] is >= 16 and <= 31) && !(n[0] == 192 && n[1] is 0 or 2 or 168) &&
            !(n[0] == 198 && n[1] is 18 or 19 or 51) && !(n[0] == 203 && n[1] == 0 && n[2] == 113);
    }
    public async Task<IReadOnlyList<BrowserServer>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(8));
        var ct = bounded.Token;
        using var response = await _http.GetAsync(new Uri(_root, "v1/servers"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        for (int size; (size = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0;)
        { if (memory.Length + size > 131072) throw new InvalidDataException("Ответ каталога слишком большой."); await memory.WriteAsync(buffer.AsMemory(0, size), ct); }
        var list = JsonSerializer.Deserialize<CatalogList>(memory.ToArray(), Json);
        if (list is null || list.Schema != 1 || list.Servers is null || list.Servers.Count > 128) throw new InvalidDataException("Некорректный каталог серверов.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return list.Servers.Where(s => s.Expires > now && s.Expires <= now + 300 &&
            (PublicIPv4(s.Host) || _root.IsLoopback && s.Host == "127.0.0.1") && s.Port is >= 1 and <= 65535 && s.QueryPort is >= 1 and <= 65535)
            .Select(s => new BrowserServer(new(s.Host, s.Port, s.QueryPort, s.Steam), Kind: s.Steam ? "Steam" : "IP", Internet: true))
            .DistinctBy(s => s.Endpoint.Key).ToArray();
    }
    private async Task Publish(int port, int query, bool steam, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, "v1/servers"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { port, queryPort = query, steam });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    public async Task PublishWhileAlive(int port, int query, bool steam, Func<bool> alive, Action<string> status, CancellationToken ct)
    {
        // Per-session capability lives only in memory; no publisher secret embedded or persisted.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var attempted = false;
        try
        {
            await Task.Delay(_timing.Startup, ct).ConfigureAwait(false);
            var due = DateTimeOffset.MinValue;
            while (alive() && !ct.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= due)
                {
                    attempted = true;
                    try { await Publish(port, query, steam, token, ct).ConfigureAwait(false); status("Каталог: heartbeat принят; доступность игры проверяют игроки через QR2."); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                    { status("Каталог недоступен или отказал в регистрации: " + ex.Message + ". Повтор через 60 с; dedicated продолжает работать."); }
                    due = DateTimeOffset.UtcNow.Add(_timing.Heartbeat);
                }
                await Task.Delay(_timing.Poll, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (attempted)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_root, "v1/servers/" + port));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { status("Каталог: снятие регистрации не подтверждено; запись истечёт за 180 с."); }
            }
        }
    }
    public void Dispose() => _http.Dispose();
}
