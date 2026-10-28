using System.Text.Json;

namespace Xrmpe.Launcher;

public sealed record BrowserEndpoint(string Host, int Port = 5446, int QueryPort = 5445, bool Steam = false)
{
    public string Key => $"{Host.ToLowerInvariant()}:{Port}";
    public BrowserEndpoint Validate()
    {
        if (Port is < 1 or > 65535 || QueryPort is < 1 or > 65535) throw new ArgumentException("Порты: 1–65535.");
        return this with { Host = LauncherService.NormalizeHost(Host) };
    }
}

public sealed record ServerSnapshot(string Name, string Map, string Mode, int? Players, int? MaxPlayers, string MatchState);

public sealed record BrowserServer(BrowserEndpoint Endpoint, ServerSnapshot? Snapshot = null, int? Ping = null,
    string Kind = "IP", bool LauncherResponded = false, bool Favorite = false, bool Internet = false)
{
    public string Name => Snapshot?.Name ?? Endpoint.Host;
    public string Map => Snapshot?.Map ?? "—";
    public string Mode => Snapshot?.Mode ?? "—";
    public string PlayerText => Snapshot is { Players: int players, MaxPlayers: int max } ? $"{players}/{max}" : "—";
    public string PingText => Ping is int ms ? $"{ms} мс" : "—";
    public string CompatibilityText => "Аддон не проверен";
    public string Availability => Snapshot is not null ? "Ответ QR2" : LauncherResponded ? "Лаунчер виден; игра не подтверждена" : Internet ? "Зарегистрирован; нет ответа QR2" : "Нет ответа QR2";
    public string Address => $"{Endpoint.Host}:{Endpoint.Port}";
    public string Star => Favorite ? "★ " : "";
    public string DisplayName => Star + Name;
    public string Details => $"{Address} · query UDP {Endpoint.QueryPort} · {Availability} · gamemode: {Snapshot?.MatchState ?? "неизвестно"} (фаза матча не подтверждена)";
}

public sealed record BrowserPreferences(List<BrowserEndpoint> Saved, List<string> Favorites, bool Advertise = false);

public sealed class ServerBrowserStore(string path)
{
    public BrowserPreferences Load()
    {
        if (!File.Exists(path)) return new([], []);
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Список серверов слишком большой.");
        var data = JsonSerializer.Deserialize<BrowserPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("Некорректный список серверов.");
        return new((data.Saved ?? []).Take(128).Select(x => x.Validate()).DistinctBy(x => x.Key).ToList(),
            (data.Favorites ?? []).Take(128).Where(x => x is { Length: <= 300 }).Distinct().ToList(), data.Advertise);
    }
    public void Save(BrowserPreferences data)
    {
        if (data.Saved.Count > 128 || data.Favorites.Count > 128) throw new InvalidOperationException("Можно сохранить до 128 серверов.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

// A future master-server provider can implement this contract; no public catalogue is configured.
public interface IServerDiscoveryProvider
{
    Task<IReadOnlyList<BrowserServer>> DiscoverAsync(CancellationToken cancellationToken);
}
