using System.Net;
using System.Text.Json;

namespace Xrmpe.Launcher;

public sealed class CatalogPreferencesStore(string path)
{
    public static CatalogPreferences NewProfileDefaults() => new("https://xrmpe-server-browser.mr-kirillovanov2002.workers.dev", false, "https://xrmpe-reserve-api.onrender.com");
    public static CatalogPreferences Validate(CatalogPreferences value)
    {
        string Normalize(string? address) => string.IsNullOrWhiteSpace(address) ? "" : GlobalCatalog.ValidateAddress(address).GetLeftPart(UriPartial.Authority);
        var primary = Normalize(value.Address); var backup = Normalize(value.BackupAddress);
        if (primary.Length == 0 && backup.Length != 0) throw new ArgumentException("Сначала укажите основной каталог.");
        if (primary.Length != 0 && primary == backup) throw new ArgumentException("Основной и резервный адреса должны различаться.");
        if (value.Publish && primary.Length == 0) throw new ArgumentException("Для публикации укажите основной каталог.");
        return new(primary, value.Publish, backup);
    }
    public CatalogPreferences Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Настройки каталога слишком большие.");
        return Validate(JsonSerializer.Deserialize<CatalogPreferences>(File.ReadAllText(path)) ?? new());
    }
    public void Save(CatalogPreferences value)
    {
        value = Validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
    }
}

// Independent publications, bounded reads, per-source backoff. No secret shared between catalogues.
public sealed class CatalogCluster : IServerDiscoveryProvider, IDisposable
{
    private sealed class Source(string name, string address, GlobalCatalog client)
    {
        public string Label => name + " (" + address + ")";
        public GlobalCatalog Client => client;
        public DateTimeOffset RetryAt;
        public int Failures;
        public Exception? Error;
    }
    private readonly Source[] _sources;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private IReadOnlyList<BrowserServer>? _cached;
    private Exception? _lastError;
    public string Status { get; private set; } = "Каталоги ещё не опрошены.";
    public CatalogCluster(CatalogPreferences preferences, Func<string, GlobalCatalog>? factory = null, Func<DateTimeOffset>? clock = null)
    {
        preferences = CatalogPreferencesStore.Validate(preferences);
        factory ??= address => new GlobalCatalog(address);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sources = new[] { ("Основной", preferences.Address), ("Резервный", preferences.BackupAddress) }
            .Where(x => x.Item2.Length != 0).Select(x => new Source(x.Item1, x.Item2, factory(x.Item2))).ToArray();
    }
    private static bool Transient(Exception error) => error is OperationCanceledException || error is HttpRequestException http &&
        (http.StatusCode is null || http.StatusCode == HttpStatusCode.RequestTimeout || (int)http.StatusCode >= 500);
    private static string Describe(Exception ex) => ex is HttpRequestException { StatusCode: {} status } ? "HTTP " + (int)status :
        ex is OperationCanceledException ? "тайм-аут" : ex is HttpRequestException ? "сетевая ошибка / TLS / DNS" : "некорректный ответ API";
    private static string Failure(Source source, Exception error) => source.Label + ": " + Describe(error) + "; повтор после " + source.RetryAt.ToLocalTime().ToString("HH:mm:ss");
    private async Task<IReadOnlyList<BrowserServer>> Read(Source source, CancellationToken ct)
    {
        if (_clock() < source.RetryAt && source.Error is not null) throw source.Error;
        try
        {
            var rows = await source.Client.DiscoverAsync(ct).ConfigureAwait(false);
            source.Failures = 0; source.Error = null; source.RetryAt = default;
            return rows;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            source.Error = ex; source.Failures = Math.Min(3, source.Failures + 1);
            source.RetryAt = _clock().AddSeconds(30 * (1 << (source.Failures - 1)));
            throw;
        }
    }
    public async Task<IReadOnlyList<BrowserServer>> DiscoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Repeated button clicks never start an unbounded failover loop.
            if (_clock() - _lastRead < TimeSpan.FromSeconds(15))
            { if (_lastError is not null) throw _lastError; return _cached ?? []; }
            if (_sources.Length == 0) { Status = "Каталог не настроен."; return []; }
            var rows = new List<BrowserServer>(); var messages = new List<string>();
            bool primaryOk = false;
            try { rows.AddRange(await Read(_sources[0], ct).ConfigureAwait(false)); primaryOk = true; messages.Add("Источник: " + _sources[0].Label); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                messages.Add(Failure(_sources[0], ex));
                if (!Transient(ex))
                {
                    Status = string.Join(" · ", messages) + ". Переключение запрещено для этой ошибки API; проверьте настройки.";
                    throw;
                }
            }
            if (_sources.Length > 1)
            {
                try
                {
                    rows.AddRange(await Read(_sources[1], ct).ConfigureAwait(false));
                    messages.Add((primaryOk ? "Дополнение из " : "Текущий источник: ") + _sources[1].Label);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { messages.Add(Failure(_sources[1], ex)); }
            }
            Status = string.Join(" · ", messages) + ". Heartbeat не подтверждает доступность игры. Повтор после отказа: 30–120 с.";
            if (!primaryOk && (_sources.Length < 2 || _sources[1].Error is not null)) throw new HttpRequestException("Все настроенные каталоги недоступны. " + Status);
            // Primary wins conflicting endpoint metadata; never merge different addresses into one server.
            _cached = rows.DistinctBy(x => x.Endpoint.Key).Take(128).ToArray(); _lastError = null; _lastRead = _clock();
            return _cached;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _lastError = ex; _cached = null; _lastRead = _clock(); throw; }
        finally { _gate.Release(); }
    }
    public Task PublishWhileAlive(int port, int query, bool steam, Func<bool> alive, Action<string> status, CancellationToken ct)
    {
        var states = new Dictionary<string, string>(); var gate = new object();
        return Task.WhenAll(_sources.Select(source => source.Client.PublishWhileAlive(port, query, steam, alive, message =>
        {
            lock (gate) { states[source.Label] = message; status(string.Join(Environment.NewLine, states.Select(x => x.Key + ": " + x.Value))); }
        }, ct)));
    }
    public void Dispose() { foreach (var source in _sources) source.Client.Dispose(); }
}
