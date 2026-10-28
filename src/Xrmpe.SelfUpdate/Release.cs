using System.Security.Cryptography;
using System.Text.Json;

namespace Xrmpe.SelfUpdate;

public sealed record ReleaseFile(string Path, long Size, string Sha256);
public sealed record LauncherRelease(int Schema, string Version, List<ReleaseFile> Files);
public sealed record InstallJob(string Target, string Stage, int ParentPid, long ParentStartUtcTicks, bool Restart = true, bool RequireSignature = false);

public static class ReleasePolicy
{
    public const string ManifestName = "launcher-release.json";
    public const string Executable = "Xrmpe.Launcher.exe";
    public static readonly HashSet<string> Allowed = LoadAllowed();
    private static HashSet<string> LoadAllowed()
    {
        using var stream = typeof(ReleasePolicy).Assembly.GetManifestResourceStream("Xrmpe.SelfUpdate.AllowedFiles.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public static void Validate(LauncherRelease release)
    {
        if (release.Schema != 1 || !System.Version.TryParse(release.Version, out var version) || version.Major < 0 ||
            release.Files is null || release.Files.Count is < 4 or > 600)
            throw new InvalidDataException("Некорректный манифест лаунчера.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in release.Files)
        {
            ValidateName(file.Path);
            if (!names.Add(file.Path) || file.Size < 0 || file.Size > 300L * 1024 * 1024 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Неверный размер, хеш или повтор файла лаунчера.");
            total = checked(total + file.Size);
        }
        if (total > 2L * 1024 * 1024 * 1024 || !new[] { Executable, "Xrmpe.Launcher.dll", "Xrmpe.SelfUpdate.dll", "Xrmpe.Updater.exe", "Xrmpe.Launcher.deps.json", "Xrmpe.Launcher.runtimeconfig.json" }.All(names.Contains))
            throw new InvalidDataException("Неполный или слишком большой пакет лаунчера.");
    }
    public static void ValidateName(string path)
    {
        // Closed embedded catalogue of this launcher's published runtime. No user paths,
        // scripts, saves, settings or game binaries can be introduced by a manifest.
        if (path is null || !Allowed.Contains(path) || path.Contains('\\') || path.Contains(':') || path.Contains("..") || path.StartsWith('/'))
            throw new InvalidDataException("Файл не разрешён для обновления лаунчера: " + path);
    }
    public static void NoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки и junction в путях обновления запрещены: " + current);
        }
    }
    public static string SafePath(string root, string relative)
    {
        ValidateName(relative);
        root = System.IO.Path.GetFullPath(root);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выход за пределы каталога лаунчера.");
        NoLinks(full);
        return full;
    }
    public static async Task<string> Hash(string file, CancellationToken ct = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    public static async Task Verify(string root, LauncherRelease release, CancellationToken ct = default)
    {
        Validate(release);
        foreach (var file in release.Files)
        {
            var path = SafePath(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !string.Equals(await Hash(path, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Размер или SHA-256 не совпадает: " + file.Path);
        }
        var actual = System.Reflection.AssemblyName.GetAssemblyName(SafePath(root, "Xrmpe.Launcher.dll")).Version!;
        var claimed = System.Version.Parse(release.Version);
        if (actual != new Version(claimed.Major, claimed.Minor, Math.Max(0, claimed.Build), Math.Max(0, claimed.Revision)))
            throw new InvalidDataException("Версия Xrmpe.Launcher.dll не совпадает с манифестом.");
    }
    public static LauncherRelease Read(string directory)
    {
        NoLinks(directory);
        var path = System.IO.Path.Combine(directory, ManifestName);
        NoLinks(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Манифест слишком большой.");
        var release = JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJson.Default.LauncherRelease) ?? throw new InvalidDataException("Пустой манифест.");
        Validate(release); return release;
    }
    public static void EnsureWritable(string root)
    {
        NoLinks(root);
        var probe = System.IO.Path.Combine(root, ".xrmpe-write-" + Guid.NewGuid().ToString("N"));
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (UnauthorizedAccessException ex) { throw new UnauthorizedAccessException("Нет прав записи в каталог лаунчера. Для Program Files запустите лаунчер от имени администратора либо используйте переносимую копию в доступной папке. Обновление не установлено.", ex); }
    }
}
