using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Xrmpe.Launcher.Updates;

public sealed record UpdateEntry(string Path, long Size, string Sha256);
public sealed record UpdateManifest(int Version, List<UpdateEntry> Files);
public sealed record UpdateProgress(string Stage, long Done, long Total, double BytesPerSecond)
{
    public string Description => $"{Stage} · {Done / 1048576d:F1} / {Total / 1048576d:F1} МиБ · {BytesPerSecond / 1048576d:F2} МиБ/с · осталось " +
        (BytesPerSecond > 0 ? TimeSpan.FromSeconds(Math.Min(9999999, Math.Max(0, Total - Done) / BytesPerSecond)).ToString(@"hh\:mm\:ss") : "—");
}

public static class UpdateFiles
{
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "addons", "resources", "patches", "mp", "localization", "gamedata", "db", "levels", "meshes", "textures", "sounds", "shaders", "scripts" };
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "saves", "savedgames", "saved_games", "screenshots", "logs", "userdata", "profiles", "config", "settings", "cache", "temp" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".addon", ".ltx", ".xml", ".script", ".lua", ".dds", ".ogf", ".omf", ".ogg", ".wav", ".thm", ".anm", ".ppe", ".seq", ".bump", ".ai", ".spawn", ".geom", ".geomx", ".cform", ".hom", ".wallmarks", ".details", ".ps", ".vs", ".h", ".s", ".xr", ".pack", ".xdb", ".db" };

    public static void EnsureIdle()
    {
        var processes = Process.GetProcessesByName("xrEngine");
        try { if (processes.Length > 0) throw new InvalidOperationException("Закройте игру и все dedicated-серверы xrEngine перед обновлением или раздачей."); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static string ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 220 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Contains('%'))
            throw new InvalidDataException("Небезопасный путь в манифесте.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)) ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("Небезопасное имя файла в манифесте.");
        }
        if (!IsPublishable(path)) throw new InvalidDataException($"Пользовательский или неподдерживаемый файл запрещён: {path}");
        return path;
    }

    public static bool IsPublishable(string path)
    {
        var parts = path.Split('/');
        if (parts.Any(p => p.StartsWith('.') || p.StartsWith('_') || Excluded.Contains(p))) return false;
        var name = parts[^1];
        if (name.Equals("user.ltx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("user_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("server", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ltx", StringComparison.OrdinalIgnoreCase)) return false;
        // Root configuration files (including local fsgame paths) belong to this PC.
        if (parts.Length == 1) return false;
        if (!Roots.Contains(parts[0])) return false;
        var ext = System.IO.Path.GetExtension(name);
        if (parts[0].Equals("bin", StringComparison.OrdinalIgnoreCase)) return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        if (parts[0].Equals("addons", StringComparison.OrdinalIgnoreCase)) return ext.Equals(".addon", StringComparison.OrdinalIgnoreCase);
        return Extensions.Contains(ext) || System.Text.RegularExpressions.Regex.IsMatch(ext, @"\A\.(db|xdb)[0-9]+\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        return SafeInternalPath(root, relative);
    }

    // Also used for updater-owned temporary names, which never come from the server.
    public static string SafeInternalPath(string root, string relative)
    {
        root = System.IO.Path.GetFullPath(root);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выход за пределы папки игры запрещён.");
        EnsureNoLinks(root);
        EnsureNoLinks(full);
        return full;
    }

    public static void EnsureNoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Символические ссылки/junction запрещены: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (File.Exists(path))
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (OperatingSystem.IsWindows() && (!GetFileInformationByHandle(file, out var info) || info.Links > 1))
                throw new InvalidDataException($"Невозможно безопасно открыть файл или обнаружена жёсткая ссылка: {path}");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        EnsureNoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Count > 200000) throw new InvalidDataException("Неподдерживаемый манифест.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Size is < 0 or > 100L * 1024 * 1024 * 1024 ||
                file.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, @"\A[A-Fa-f0-9]{64}\z")) throw new InvalidDataException("Некорректные размер или SHA-256.");
            ValidateRelative(file.Path);
            if (!names.Add(file.Path)) throw new InvalidDataException("Повторяющиеся пути в манифесте.");
        }
        foreach (var name in names)
            for (var parent = System.IO.Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
                if (names.Contains(parent.Replace('\\', '/'))) throw new InvalidDataException("Конфликт файла и каталога.");
    }

    public static async Task<UpdateManifest> BuildManifestAsync(string root, Action<string> log, CancellationToken ct)
    {
        EnsureNoLinks(root);
        var files = new List<UpdateEntry>();
        var dirs = new Stack<string>(); dirs.Push(System.IO.Path.GetFullPath(root));
        while (dirs.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            EnsureNoLinks(dir);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var relative = System.IO.Path.GetRelativePath(root, entry).Replace('\\', '/');
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) { log($"Пропущена ссылка: {relative}"); continue; }
                if (Directory.Exists(entry))
                {
                    var parts = relative.Split('/');
                    if (Roots.Contains(parts[0]) && !parts.Any(p => p.StartsWith('_') || p.StartsWith('.') || Excluded.Contains(p))) dirs.Push(entry);
                    continue;
                }
                if (!IsPublishable(relative)) continue;
                ValidateRelative(relative);
                var hash = await HashAsync(entry, ct).ConfigureAwait(false);
                files.Add(new UpdateEntry(relative, new FileInfo(entry).Length, hash));
                if (files.Count > 200000) throw new InvalidDataException("Слишком много файлов.");
            }
        }
        var result = new UpdateManifest(1, files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList());
        ValidateManifest(result);
        return result;
    }
}
