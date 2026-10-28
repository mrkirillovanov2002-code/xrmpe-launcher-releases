using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
namespace Xrmpe.Launcher.Content;

public sealed class CosmeticHashMismatchException(string stage, string expected, string actual, long size)
    : IOException($"SHA-256 косметического пакета ({stage}) не совпадает: ожидалось {expected}, получено {actual}, размер {size} байт.")
{
    public string Stage { get; } = stage;
    public string ExpectedHash { get; } = expected;
    public string ActualHash { get; } = actual;
    public long Size { get; } = size;
}

public static class LocalArmbandsProfiles
{
    public const string PackageHash = "9FE1F8AC4895ABEB0C15E65889521C033AF7C27FA2A09921A1D2547613641C14";
    public const string EmissiveHash = "16C3D2F3D33AB7BCCC5C0EE855948FCCA45700AFAFE48A5FEE447ED41838B4AA";
    public static string HashFor(string variant) => variant switch
    {
        "dds" => PackageHash,
        "emissive" => EmissiveHash,
        _ => throw new InvalidDataException("Неизвестный вариант повязок.")
    };
    public static string ActualHash(string path)
    {
        ContentPaths.NoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 50 * 1024 * 1024 || info.Length == 0)
            throw new IOException("Не найден локальный пакет повязок допустимого размера.");
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public static void Verify(string path, string variant = "dds", string? approvedCosmeticHash = null, string stage = "локальный пакет")
    {
        var expected = HashFor(variant);
        var actual = ActualHash(path);
        if (actual == expected) return;
        if (variant == "emissive" && approvedCosmeticHash == actual)
        {
            CosmeticPackageSafety.Verify(path);
            return;
        }
        throw new CosmeticHashMismatchException(stage, expected, actual, new FileInfo(path).Length);
    }
    public static void Apply(ProcessStartInfo start, string package, string variant = "dds", string? approvedCosmeticHash = null, Action<string>? diagnostic = null)
    {
        if (!start.ArgumentList.Contains("-addon=xrmpe_pvp.addon")) return;
        if (string.Equals(Path.GetFileName(Path.GetDirectoryName(start.FileName)), "dedicated", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Косметика запрещена для dedicated.");
        if (start.ArgumentList.Contains("-fsltx")) throw new InvalidOperationException("Неизвестный профиль запуска: косметика не добавлена.");
        var expected = HashFor(variant);
        if (approvedCosmeticHash is not null &&
            (variant != "emissive" || !EmissiveWorkshop.IsTrustedLocalBuild(package, approvedCosmeticHash)))
            throw new InvalidDataException("Принудительное включение разрешено только для результата штатного локального сборщика.");
        Verify(package, variant, approvedCosmeticHash, "локальный кеш");
        var game = Path.GetFullPath(start.WorkingDirectory); ContentPaths.NoLinks(game);
        var source = Path.Combine(game, "fsgame.ltx"); ContentPaths.NoLinks(source);
        if (new FileInfo(source).Length > 65536) throw new InvalidDataException("Слишком большой fsgame.ltx.");
        var text = File.ReadAllText(source);
        if (text.Contains("$zonelink", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Исходный fsgame уже содержит исследовательские изменения. Выберите чистую установку.");
        var relative = ".zl-bands/" + expected[..16];
        var directory = Path.Combine(game, relative); ContentPaths.NoLinks(directory); Directory.CreateDirectory(directory);
        var mounted = Path.Combine(directory, "armbands.db"); ContentPaths.NoLinks(mounted);
        var sourceHash = ActualHash(package);
        string? mountedHash = null;
        if (File.Exists(mounted))
        {
            var size = new FileInfo(mounted).Length;
            mountedHash = size is > 0 and <= 50 * 1024 * 1024 ? ActualHash(mounted) : "invalid-size:" + size;
        }
        // A stale .zl-bands copy must not permanently break a valid local build.
        // Replace only ZoneLink's own sidecar, never an original game archive.
        if (mountedHash != sourceHash)
        {
            if (mountedHash is not null) diagnostic?.Invoke($"Косметика: копия для запуска отличается от локального кеша. Ожидалось {sourceHash}, получено {mountedHash}; восстанавливаем только .zl-bands.");
            var temporary = mounted + "." + Guid.NewGuid().ToString("N") + ".tmp";
            ContentPaths.NoLinks(temporary);
            try
            {
                File.Copy(package, temporary, false);
                Verify(temporary, variant, approvedCosmeticHash, "временная копия для запуска");
                File.Move(temporary, mounted, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Verify(mounted, variant, approvedCosmeticHash, "копия для запуска");
        var contents = text + "\n$zonelink_armbands$ = false| false| $fs_root$| " + relative.Replace('/', '\\') + "\\\n";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)))[..16];
        var name = "_zl_bands_" + id + ".ltx"; var profile = Path.Combine(game, name); ContentPaths.NoLinks(profile);
        if (File.Exists(profile)) { if (File.ReadAllText(profile) != contents) throw new IOException("Конфликт косметического профиля."); }
        else File.WriteAllText(profile, contents, new UTF8Encoding(false));
        // Keep all original aliases, especially appdata, unchanged. No user.ltx copying or rewriting.
        start.ArgumentList.Add("-fsltx"); start.ArgumentList.Add(name);
    }
}
