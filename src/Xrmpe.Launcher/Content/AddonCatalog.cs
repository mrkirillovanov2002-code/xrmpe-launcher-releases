using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Xrmpe.Launcher.Content;

public sealed record DeclaredMap(string Id, string Name, string[] Modes);
public sealed record AddonDescriptor(string FileName, string Name, string Author, string Description, string Version,
    string[] Modes, string? StartMap, DeclaredMap[] Maps, byte[]? Icon, int IconPixels, string? Error, string? Warning, string? PackageSha256 = null, string[]? StartupArgs = null)
{
    public bool Healthy => Error is null;
    public string Summary => Healthy ? $"Установлен · {Version} · {string.Join(", ", Modes)}" : "Повреждён / не поддерживается: " + Error;
}
public static class ContentPaths
{
    public static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки и junction не поддерживаются: " + p);
    }
    public static string Root(string selected)
    {
        var root = Path.GetFullPath(selected.Trim().Trim('"'));
        if (!File.Exists(Path.Combine(root, "bin", "xrEngine.exe")) && Directory.Exists(Path.Combine(root, "game"))) root = Path.Combine(root, "game");
        if (!File.Exists(Path.Combine(root, "bin", "xrEngine.exe"))) throw new IOException("Выберите папку game с bin/xrEngine.exe.");
        NoLinks(root); return root;
    }
}
public static class AddonCatalog
{
    public static Task<IReadOnlyList<AddonDescriptor>> ScanAsync(string game, CancellationToken ct) => Task.Run<IReadOnlyList<AddonDescriptor>>(() =>
    {
        ContentPaths.NoLinks(game);
        var folder = Path.Combine(game, "addons"); ContentPaths.NoLinks(folder);
        if (!Directory.Exists(folder)) return Array.Empty<AddonDescriptor>();
        var result = new List<AddonDescriptor>(); long iconBudget = 16 * 1024 * 1024;
        foreach (var file in Directory.EnumerateFiles(folder, "*.addon").Order(StringComparer.OrdinalIgnoreCase).Take(128))
        {
            ct.ThrowIfCancellationRequested(); var item = ReadLaunchCandidate(file, ct);
            if (item.Icon is not null) { iconBudget -= item.Icon.Length; if (iconBudget < 0) item = item with { Icon = null, Warning = "Превышен общий лимит иконок; показана заглушка." }; }
            result.Add(item);
        }
        return result;
    }, ct);
    public static AddonDescriptor ReadLaunchCandidate(string path, CancellationToken ct = default)
    {
        var item = Read(path, ct);
        if (item.Healthy && item.FileName == "zonestrike.addon")
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024]; int read;
            while ((read = stream.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            item = item with { PackageSha256 = Convert.ToHexString(hash.GetHashAndReset()) };
        }
        return item;
    }
    public static AddonDescriptor Read(string path, CancellationToken ct = default)
    {
        var file = Path.GetFileName(path);
        try
        {
            ContentPaths.NoLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[]? metadata = null, icon = null; int databases = 0, entries = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var header = new byte[512];
            while (stream.Position < stream.Length)
            {
                ct.ThrowIfCancellationRequested(); if (++entries > 4096) throw new InvalidDataException("Слишком много записей контейнера.");
                stream.ReadExactly(header);
                if (header.All(x => x == 0)) break;
                long Octal(int at, int length)
                {
                    var text = Encoding.ASCII.GetString(header, at, length).Trim('\0', ' ');
                    if (text.Length == 0 || text.Any(c => c is < '0' or > '7')) throw new InvalidDataException("Неверное числовое поле TAR/V7.");
                    return Convert.ToInt64(text, 8);
                }
                var checksum = Octal(148, 8); long sum = 0;
                for (var i = 0; i < 512; i++) sum += i is >= 148 and < 156 ? 32 : header[i];
                if (sum != checksum) throw new InvalidDataException("Не совпадает контрольная сумма TAR-заголовка.");
                string Text(int at, int length) => Encoding.UTF8.GetString(header, at, length).Split('\0')[0];
                var name = Text(0, 100);
                if (Text(257, 5) == "ustar" && Text(345, 155) is { Length: > 0 } prefix) name = prefix + "/" + name;
                if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(x => x == ".." || x == ".")) throw new InvalidDataException("Небезопасное имя записи.");
                if (!seen.Add(name)) throw new InvalidDataException("Повторяющиеся записи контейнера.");
                if (header[156] is not (0 or (byte)'0' or (byte)'5')) throw new InvalidDataException("Ссылки и расширения TAR не поддерживаются.");
                var size = Octal(124, 12); var padded = checked((size + 511) / 512 * 512);
                if (padded > stream.Length - stream.Position) throw new InvalidDataException("Обрезанные данные контейнера.");
                var next = checked(stream.Position + padded);
                if (header[156] != (byte)'5')
                {
                    if (name == "metadata.json")
                    {
                        if (size is < 2 or > 65536) throw new InvalidDataException("Метаданные: допустимо до 64 КиБ.");
                        metadata = new byte[(int)size]; stream.ReadExactly(metadata);
                    }
                    if (name == "icon.webp" && size is > 0 and <= 2097152) { icon = new byte[(int)size]; stream.ReadExactly(icon); }
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\.db[0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && size > 0) databases++;
                }
                stream.Position = next;
            }
            if (metadata is null) throw new InvalidDataException("Нет metadata.json.");
            if (databases == 0) throw new InvalidDataException("Не найдены ресурсные DB аддона.");
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
            string Value(JsonElement element, string key, int max = 512) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()!, max) : "";
            string Local(JsonElement element, string key, int max)
            {
                if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return "";
                return new[] { "ru", "en" }.Select(k => Value(value, k, max)).FirstOrDefault(x => x.Length != 0) ?? "";
            }
            string[] Modes(JsonElement element)
            {
                if (!element.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array || modes.GetArrayLength() > 16) throw new InvalidDataException("Нет допустимого списка modes.");
                return modes.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Clean(x.GetString()!, 32) : throw new InvalidDataException("Неверный mode.")).Distinct().ToArray();
            }
            var maps = new List<DeclaredMap>();
            if (root.TryGetProperty("availableMaps", out var available))
            {
                if (available.ValueKind != JsonValueKind.Array || available.GetArrayLength() > 256) throw new InvalidDataException("Неверный список карт.");
                foreach (var map in available.EnumerateArray())
                {
                    var id = Value(map, "devName", 80); if (!ValidId(id)) throw new InvalidDataException("Небезопасный devName карты.");
                    maps.Add(new(id, Local(map, "name", 160) is { Length: > 0 } title ? title : id, Modes(map)));
                }
            }
            var start = Value(root, "coopStartMap", 80); if (start.Length > 0 && !ValidId(start)) throw new InvalidDataException("Небезопасная стартовая карта.");
            string[] flags = [];
            if (root.TryGetProperty("coopStartupArgs", out var startup)) {
                if (startup.ValueKind != JsonValueKind.Array || startup.GetArrayLength() > 16) throw new InvalidDataException("Неверный coopStartupArgs.");
                flags = startup.EnumerateArray().SelectMany(v => v.ValueKind == JsonValueKind.String ? v.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : throw new InvalidDataException("Неверный флаг запуска")).Distinct().ToArray();
            }
            int pixels = IconPixels(icon); string? warning = null;
            if (pixels == 0) { icon = null; warning = "Иконка отсутствует или имеет неподдерживаемый/слишком большой размер."; }
            return new(file, Local(root, "name", 160) is { Length: > 0 } titleName ? titleName : file,
                Value(root, "author", 160), Local(root, "description", 4000), Value(root, "addonVersion", 64), Modes(root),
                start.Length == 0 ? null : start, maps.DistinctBy(x => x.Id).ToArray(), icon, pixels, null, warning, StartupArgs: flags);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException or InvalidOperationException or FormatException)
        { return new(file, file, "", "", "", [], null, [], null, 0, ex.Message, null); }
    }
    public static bool ValidId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-zA-Z0-9_-]{1,80}\z");
    static string Clean(string text, int limit) => new(text.Take(limit).Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
    static int IconPixels(byte[]? data)
    {
        // Installed PvP 1.4.7 calls its icon "icon.webp", but the actual bytes are PNG.
        if (data is { Length: >= 24 } && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && Encoding.ASCII.GetString(data, 12, 4) == "IHDR")
        {
            var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
            var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
            return w is > 0 and <= 2048 && h is > 0 and <= 2048 ? (int)(w * h) : 0;
        }
        if (data is null || data.Length < 30 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WEBP") return 0;
        int width = 0, height = 0; var type = Encoding.ASCII.GetString(data, 12, 4);
        if (type == "VP8X") { width = 1 + data[24] + (data[25] << 8) + (data[26] << 16); height = 1 + data[27] + (data[28] << 8) + (data[29] << 16); }
        if (type == "VP8 " && data[23] == 0x9d && data[24] == 1 && data[25] == 0x2a) { width = BitConverter.ToUInt16(data, 26) & 16383; height = BitConverter.ToUInt16(data, 28) & 16383; }
        if (type == "VP8L" && data[20] == 0x2f) { var bits = BitConverter.ToUInt32(data, 21); width = 1 + (int)(bits & 16383); height = 1 + (int)((bits >> 14) & 16383); }
        return width is > 0 and <= 2048 && height is > 0 and <= 2048 ? width * height : 0;
    }
}
