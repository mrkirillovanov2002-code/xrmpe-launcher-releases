using System.Text.Json;

namespace Xrmpe.Launcher;

public sealed record ServerAddress(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

public sealed class ConnectionHistory(string path)
{
    public IReadOnlyList<ServerAddress> Load()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Файл истории адресов слишком большой.");
        var entries = JsonSerializer.Deserialize<List<ServerAddress>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Некорректная история адресов.");
        return entries.Where(entry => entry is not null && entry.Host is not null && entry.Port is >= 1 and <= 65535)
            .Select(entry => new ServerAddress(LauncherService.NormalizeHost(entry.Host), entry.Port)).Distinct().Take(10).ToArray();
    }

    public IReadOnlyList<ServerAddress> Remember(string host, int port, IEnumerable<ServerAddress> previous)
    {
        host = LauncherService.NormalizeHost(host);
        if (port is < 1 or > 65535) throw new ArgumentException("Некорректный порт.");
        var items = new[] { new ServerAddress(host, port) }.Concat(previous).Distinct().Take(10).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        return items;
    }
}
