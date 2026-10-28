using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xrmpe.Launcher.Content;

public sealed record EmissiveBuildProgress(int Percent, string Message);
public sealed record EmissiveBuildResult(bool FromCache, string DbPath, string ActualHash, bool ReferenceHashMatches);
internal sealed record EmissiveBuildReceipt(int Schema, string BuilderHash, string PackageHash, int Models, int Entries);

public static class EmissiveWorkshop
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    public const string BuilderZipHash = "12DC915FDD4DF5A0C61A97C5C1540FC0DCEA0FA2F6B37021BF42A05F10E55203";
    public static string InstalledPath(string data) =>
        Path.Combine(Path.GetFullPath(data), "LocalMods", "Emissive", LocalArmbandsProfiles.EmissiveHash[..16], "armbands.db");
    public static string PreviewPath(string data, string id, string angle) =>
        Path.Combine(Path.GetDirectoryName(InstalledPath(data))!, "Previews", id + "-" + angle + ".png");
    private static string ReceiptPath(string package) => Path.Combine(Path.GetDirectoryName(package)!, ".local-build.json");

    public static bool IsTrustedLocalBuild(string package, string actualHash)
    {
        try
        {
            if (Path.GetFileName(package) != "armbands.db" ||
                Path.GetFileName(Path.GetDirectoryName(package)) != LocalArmbandsProfiles.EmissiveHash[..16] ||
                Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(package))) != "Emissive") return false;
            var receiptPath = ReceiptPath(package);
            ContentPaths.NoLinks(receiptPath);
            if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 1024) return false;
            var receipt = JsonSerializer.Deserialize<EmissiveBuildReceipt>(File.ReadAllText(receiptPath));
            if (receipt is not { Schema: 1, Models: 33, Entries: 34 } ||
                receipt.BuilderHash != BuilderZipHash || receipt.PackageHash != actualHash ||
                LocalArmbandsProfiles.ActualHash(package) != actualHash) return false;
            CosmeticPackageSafety.Verify(package);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private static void WriteReceipt(string package, string actualHash)
    {
        var path = ReceiptPath(package); var temporary = path + ".tmp";
        ContentPaths.NoLinks(path); ContentPaths.NoLinks(temporary);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new EmissiveBuildReceipt(1, BuilderZipHash, actualHash, 33, 34)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool PreviewsComplete(string data)
    {
        var directory = Path.Combine(Path.GetDirectoryName(InstalledPath(data))!, "Previews");
        if (!Directory.Exists(directory) || Directory.EnumerateFiles(directory, "*.png").Count() != 120) return false;
        Span<byte> header = stackalloc byte[8];
        foreach (var costume in WorkshopCatalog.Items)
            foreach (var angle in new[] { "front", "right", "back", "left" })
            {
                var path = Path.Combine(directory, costume.Id + "-" + angle + ".png");
                ContentPaths.NoLinks(path);
                if (!File.Exists(path) || new FileInfo(path).Length < 100) return false;
                using var file = File.OpenRead(path);
                if (file.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
            }
        return true;
    }

    // Generates a local-only archive from the user's installed xrMPE resources.
    // The application package contains builder code and redistributable runtimes,
    // never modified game OGF/DDS/DB bytes.
    public static async Task EnsureBuiltAsync(string game, string data) =>
        await EnsureBuiltAsync(game, data, null, CancellationToken.None);

    public static async Task<EmissiveBuildResult> EnsureBuiltAsync(string game, string data,
        IProgress<EmissiveBuildProgress>? progress, CancellationToken cancellationToken, bool forceRebuild = false)
    {
        await BuildGate.WaitAsync(cancellationToken);
        try { return await EnsureBuiltCoreAsync(game, data, progress, cancellationToken, forceRebuild); }
        finally { BuildGate.Release(); }
    }

    private static async Task<EmissiveBuildResult> EnsureBuiltCoreAsync(string game, string data,
        IProgress<EmissiveBuildProgress>? progress, CancellationToken cancellationToken, bool forceRebuild)
    {
        var target = InstalledPath(data);
        ContentPaths.NoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var lockPath = Path.Combine(Path.GetDirectoryName(target)!, ".build.lock");
        ContentPaths.NoLinks(lockPath);
        FileStream? buildLock = null;
        while (buildLock is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { buildLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                progress?.Report(new(0, "Другая копия ZoneLink уже готовит повязки; ждём завершения…"));
                await Task.Delay(1000, cancellationToken);
            }
        }
        using (buildLock)
        {
            if (forceRebuild) progress?.Report(new(0, "Повторно собираем локальный пакет; оригинальные архивы не изменяются…"));
            if (!forceRebuild && File.Exists(target))
            {
                try
                {
                    var hash = LocalArmbandsProfiles.ActualHash(target);
                    if (hash != LocalArmbandsProfiles.EmissiveHash && !IsTrustedLocalBuild(target, hash))
                        throw new CosmeticHashMismatchException("локальный кеш", LocalArmbandsProfiles.EmissiveHash, hash, new FileInfo(target).Length);
                    if (PreviewsComplete(data))
                    {
                        progress?.Report(new(100, hash == LocalArmbandsProfiles.EmissiveHash ? "3D-повязки готовы (проверенный кеш)." : "3D-повязки собраны локально; SHA отличается от эталона."));
                        return new(true, target, hash, hash == LocalArmbandsProfiles.EmissiveHash);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or CosmeticHashMismatchException)
                {
                    progress?.Report(new(0, "Локальный кеш повязок требует пересборки: " + ex.Message));
                }
            }
            return await RunBuilder(game, data, target, progress, cancellationToken);
        }
    }

    private static async Task<EmissiveBuildResult> RunBuilder(string game, string data, string target,
        IProgress<EmissiveBuildProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "Подготавливаем локальный сборщик…"));
        var builder = await Task.Run(() => EnsureBuilderExtracted(data));
        var python = Path.Combine(builder, "runtime", "python.exe");
        var script = Path.Combine(builder, "build_from_install.py");
        ContentPaths.NoLinks(python); ContentPaths.NoLinks(script);
        if (!File.Exists(python) || !File.Exists(script))
            throw new FileNotFoundException("Локальный сборщик повязок отсутствует в установленном ZoneLink.");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = builder };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Path.GetFullPath(game));
        start.ArgumentList.Add(target);
        start.Environment["PYTHONHOME"] = Path.Combine(builder, "runtime");
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить локальный сборщик повязок.");
        var output = new StringBuilder();
        string? buildSummary = null;
        var stdout = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (output.Length < 12000) output.AppendLine(line);
                if (line.Length < 4096 && line.StartsWith('{')) buildSummary = line;
                var fields = line.Split('|', 4);
                if (fields.Length == 3 && fields[0] == "ZL_PROGRESS" && int.TryParse(fields[1], out var percent))
                    progress?.Report(new(percent, fields[2]));
                else if (fields.Length == 4 && fields[0] == "ZL_PREVIEW" &&
                    int.TryParse(fields[1], out var done) && int.TryParse(fields[2], out var total) && total > 0)
                    progress?.Report(new(55 + 40 * done / total, $"Предпросмотры: {done}/{total} костюмов"));
            }
        });
        var stderr = process.StandardError.ReadToEndAsync();
        // The reference machine needs about six minutes. A bounded 20-minute
        // window accommodates slower disks while streaming visible progress.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Локальная сборка не завершилась за 20 минут. Подробности — в журнале; можно повторить операцию.");
        }
        await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
        {
            var diagnostic = CompatibilityMessage(errors, data);
            throw new IOException(diagnostic ??
                "Локальная сборка повязок не завершена. Откройте журнал ZoneLink и повторите операцию. " +
                "Исходные файлы игры не изменены; неполный пакет не подключается.");
        }
        // The pinned builder already checked source archive hashes and verified
        // every generated OGF and DB entry. Compare its own output digest to the
        // bytes on disk before recording the provenance of a non-reference build.
        var actual = LocalArmbandsProfiles.ActualHash(target);
        using (var summary = JsonDocument.Parse(buildSummary ?? throw new InvalidDataException("Сборщик не сообщил SHA-256 готового DB.")))
        {
            if (summary.RootElement.GetProperty("db_sha256").GetString()?.ToUpperInvariant() != actual ||
                summary.RootElement.GetProperty("models").GetInt32() != 33 ||
                summary.RootElement.GetProperty("entries").GetInt32() != 34)
                throw new InvalidDataException("Итог локального сборщика не совпадает с созданным DB.");
        }
        CosmeticPackageSafety.Verify(target);
        if (!PreviewsComplete(data)) throw new InvalidDataException("Сборщик не создал все 120 корректных PNG-предпросмотров.");
        WriteReceipt(target, actual);
        var matched = actual == LocalArmbandsProfiles.EmissiveHash;
        progress?.Report(new(100, matched ? "3D-повязки готовы." : "3D-повязки готовы, но SHA отличается от эталона; требуется решение пользователя."));
        return new(false, target, actual, matched);
    }

    private static string? CompatibilityMessage(string stderr, string data)
    {
        const string prefix = "ZL_DIAGNOSTIC|";
        var line = stderr.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null || line.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(line[prefix.Length..]);
            var root = document.RootElement;
            string Read(string name) => root.GetProperty(name).GetString() ?? "";
            var code = Read("code"); var file = Read("file");
            if (code is not ("missing" or "unreadable" or "hash_mismatch") ||
                file is not ("patches/xrmpe_meshes_actors.db0" or "addons/xrmpe_pvp.addon")) return null;
            var expected = Read("expected_sha256"); var actual = Read("actual_sha256");
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) ||
                (actual.Length != 0 && (actual.Length != 64 || !actual.All(Uri.IsHexDigit)))) return null;
            var supported = root.TryGetProperty("supported_sha256", out var hashes)
                ? hashes.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : new[] { expected };
            if (supported.Length is < 1 or > 4 || supported[0] != expected ||
                supported.Any(x => x.Length != 64 || !x.All(Uri.IsHexDigit))) return null;
            var version = Read("addon_version");
            var size = root.GetProperty("size_bytes").GetInt64();
            if (size < 0 || version.Length > 64) return null;
            // The shareable report contains only relative file names, hashes and metadata.
            // No game path, Windows user name, token or raw Python traceback is exported.
            var report = Path.Combine(data, "Logs", "armbands-compatibility.json");
            ContentPaths.NoLinks(report);
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            var temporary = report + ".tmp";
            ContentPaths.NoLinks(temporary);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                schema = 1, code, file, expected_sha256 = expected, actual_sha256 = actual,
                supported_sha256 = supported, size_bytes = size, addon_version = version
            }));
            File.Move(temporary, report, true);
            var found = code switch
            {
                "missing" => "файл отсутствует",
                "unreadable" => "файл не удалось прочитать",
                _ => $"SHA-256 {actual}; размер {size} байт" +
                     (version.Length == 0 ? "" : $"; версия аддона {version}")
            };
            return $"3D-повязки не собраны: установленный {file} не совпадает с проверенной сборкой xrMPE. " +
                   $"Поддерживаемые SHA-256: {string.Join(", ", supported)}; обнаружено: {found}. " +
                   "Проверьте выбранную папку игры и восстановите этот файл из официальной установки xrMPE/PvP; " +
                   "не заменяйте его файлом из другого источника. Для диагностики можно отправить файл " + report + ".";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string EnsureBuilderExtracted(string data)
    {
        using var source = typeof(EmissiveWorkshop).Assembly.GetManifestResourceStream("Xrmpe.Launcher.LocalBuilder.zip")
            ?? throw new FileNotFoundException("В лаунчере отсутствует локальный сборщик повязок.");
        if (source.Length > 150 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(source)) != BuilderZipHash)
            throw new InvalidDataException("Контрольная сумма сборщика повязок не совпадает.");
        source.Position = 0;
        var root = Path.GetFullPath(Path.Combine(data, "LocalBuilder"));
        var target = Path.Combine(root, BuilderZipHash[..16]);
        ContentPaths.NoLinks(target);
        var marker = Path.Combine(target, ".builder-sha256");
        if (File.Exists(marker) && File.ReadAllText(marker) == BuilderZipHash)
            return target;
        Directory.CreateDirectory(root);
        if (Directory.Exists(target)) DeleteScoped(root, target);
        var staged = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 or > 10000) throw new InvalidDataException("Неверное число файлов сборщика.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var parts = entry.FullName.Split('/');
                if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':') || p.Contains('\\')))
                    throw new InvalidDataException("Небезопасное имя в архиве сборщика.");
                total = checked(total + entry.Length);
                if (total > 180L * 1024 * 1024) throw new InvalidDataException("Сборщик превышает лимит размера.");
                var file = Path.Combine(staged, Path.Combine(parts));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                entry.ExtractToFile(file);
            }
            File.WriteAllText(Path.Combine(staged, ".builder-sha256"), BuilderZipHash);
            Directory.Move(staged, target);
        }
        finally
        {
            if (Directory.Exists(staged)) DeleteScoped(root, staged);
        }
        return target;
    }

    private static void DeleteScoped(string root, string path)
    {
        var safeRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Небезопасный путь к локальному сборщику.");
        ContentPaths.NoLinks(full);
        Directory.Delete(full, recursive: true);
    }

    public static void Install(string source, string data)
    {
        LocalArmbandsProfiles.Verify(source, "emissive");
        var target = InstalledPath(data);
        ContentPaths.NoLinks(target);
        if (File.Exists(target)) { LocalArmbandsProfiles.Verify(target, "emissive"); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            LocalArmbandsProfiles.Verify(temporary, "emissive");
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
