using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Xrmpe.Launcher.Updates;

public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly Action _idle;
    public UpdateClient(string root, Uri server, string token, Action? ensureIdle = null)
    {
        if (server.Scheme is not ("http" or "https") || server.AbsolutePath != "/" || server.Query.Length != 0 || server.UserInfo.Length != 0)
            throw new ArgumentException("Укажите HTTP(S)-адрес сервера без пути и пароля в URL.");
        _root = Path.GetFullPath(root);
        UpdateFiles.EnsureNoLinks(_root);
        _idle = ensureIdle ?? UpdateFiles.EnsureIdle;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<UpdateEntry>> CheckAsync(Action<UpdateProgress> report, CancellationToken ct)
    {
        _idle();
        using var response = await _http.GetAsync("manifest", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("Манифест слишком большой.");
            data.Write(buffer, 0, count);
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(data.ToArray()) ?? throw new InvalidDataException("Пустой манифест.");
        UpdateFiles.ValidateManifest(manifest);
        var needed = new List<UpdateEntry>();
        long done = 0, total = manifest.Files.Sum(f => f.Size);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = UpdateFiles.SafePath(_root, file.Path);
            if (!File.Exists(target) || new FileInfo(target).Length != file.Size ||
                !string.Equals(await UpdateFiles.HashAsync(target, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) needed.Add(file);
            done += file.Size;
            report(new UpdateProgress("Проверка " + file.Path, done, total, 0));
        }
        return needed;
    }

    public async Task ApplyAsync(IReadOnlyList<UpdateEntry> files, Action<UpdateProgress> report, Action<string> log, CancellationToken ct)
    {
        _idle();
        UpdateFiles.ValidateManifest(new UpdateManifest(1, files.ToList()));
        var staging = UpdateFiles.SafeInternalPath(_root, ".xrmpe-updates");
        Directory.CreateDirectory(staging);
        // FileShare.None is a cross-process lock for updater instances on this game folder.
        var lockFile = UpdateFiles.SafeInternalPath(_root, Path.Combine(".xrmpe-updates", "update.lock"));
        await using var folderLock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        long completed = 0, total = files.Sum(f => f.Size), transferred = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            _idle();
            var target = UpdateFiles.SafePath(_root, file.Path);
            // A previous attempt may already have committed earlier files successfully.
            if (File.Exists(target) && new FileInfo(target).Length == file.Size &&
                string.Equals(await UpdateFiles.HashAsync(target, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                completed += file.Size;
                report(new UpdateProgress("Уже актуален " + file.Path, completed, total, 0));
                continue;
            }
            var part = UpdateFiles.SafeInternalPath(_root, Path.Combine(".xrmpe-updates", file.Sha256.ToUpperInvariant() + ".part"));
            long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (offset > file.Size) throw new InvalidDataException("Временный файл больше ожидаемого. Удалите только файл .part из .xrmpe-updates и повторите.");
            if (offset < file.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "files/" + file.Sha256);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{file.Sha256}\""));
                    log($"Докачка {file.Path}: с байта {offset}.");
                }
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset ||
                    response.Content.Headers.ContentRange?.To != file.Size - 1 || response.Content.Headers.ContentRange?.Length != file.Size))
                    throw new InvalidDataException("Сервер не подтвердил диапазон докачки. Оригинал не изменён.");
                if (offset == 0 && response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength != file.Size - offset ||
                    response.Headers.ETag?.Tag != $"\"{file.Sha256}\"") throw new InvalidDataException("Размер или ETag ответа не совпадает с манифестом.");
                await using var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 131072, true);
                output.Position = offset;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[65536];
                while (true)
                {
                    // Timeout also applies to stalled response bodies, not only HTTP headers.
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (offset + read > file.Size) throw new InvalidDataException("Сервер прислал лишние данные.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    offset += read; transferred += read;
                    report(new UpdateProgress("Загрузка " + file.Path, completed + offset, total, transferred / Math.Max(.001, clock.Elapsed.TotalSeconds)));
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (offset != file.Size) throw new IOException("Соединение оборвано. Повторите обновление для докачки.");
            }
            else if (file.Size == 0 && !File.Exists(part)) await File.WriteAllBytesAsync(part, [], ct);
            if (new FileInfo(part).Length != file.Size || !string.Equals(await UpdateFiles.HashAsync(part, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Only this updater-owned partial file is discarded; originals are untouched.
                File.Delete(part);
                throw new InvalidDataException($"SHA-256 не совпал: {file.Path}. Оригинал не изменён; повторите загрузку.");
            }
            ct.ThrowIfCancellationRequested();
            _idle();
            target = UpdateFiles.SafePath(_root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            UpdateFiles.SafePath(_root, file.Path);
            UpdateFiles.EnsureNoLinks(part);
            // Staging is on the same volume. Atomic replacement; no delete-before-copy gap.
            if (File.Exists(target)) File.Replace(part, target, null);
            else File.Move(part, target);
            completed += file.Size;
            report(new UpdateProgress("Проверено и установлено " + file.Path, completed, total, transferred / Math.Max(.001, clock.Elapsed.TotalSeconds)));
            log($"Обновлён: {file.Path}");
        }
    }

    public void Dispose() => _http.Dispose();
}
