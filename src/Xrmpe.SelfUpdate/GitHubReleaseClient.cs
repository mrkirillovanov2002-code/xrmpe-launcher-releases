using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Xrmpe.SelfUpdate;

public sealed class GitHubReleaseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly RSA? _testKey;
    public GitHubReleaseClient() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }, null) { }
    internal GitHubReleaseClient(HttpClient http, RSA? verificationKey) { _http = http; _testKey = verificationKey; }
    public static Uri Asset(string repository, string tag, string name) => new($"https://github.com/{ReleaseTrust.Repository(repository)}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
    private static void Https(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Разрешены только HTTPS GitHub и его серверы release assets.");
    }
    private async Task<HttpResponseMessage> Get(Uri uri, long? offset, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            Https(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("xrMPE-Launcher/0.5.0");
            request.Headers.Accept.ParseAdd(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");
            if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            if (offset is > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Пустой redirect GitHub.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { response.Dispose(); throw new HttpRequestException("GitHub временно ограничил запросы. Повторите проверку позже; аккаунт и токен не требуются."); }
            return response;
        }
        throw new HttpRequestException("Слишком много перенаправлений GitHub.");
    }
    private async Task<byte[]> Small(Uri uri, int limit, CancellationToken ct)
    {
        using var response = await Get(uri, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("Репозиторий, опубликованный Release или обязательный файл обновления не найден.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Ответ GitHub слишком большой.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream(); var buffer = new byte[16384]; int n;
        while ((n = await source.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct)) > 0)
        { if (data.Length + n > limit) throw new InvalidDataException("Ответ GitHub слишком большой."); data.Write(buffer, 0, n); }
        return data.ToArray();
    }
    public async Task<GitHubOffer> Check(string repository, CancellationToken ct)
    {
        repository = ReleaseTrust.Repository(repository);
        using var json = JsonDocument.Parse(await Small(new Uri($"https://api.github.com/repos/{repository}/releases/latest"), 1024 * 1024, ct));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Обновление должно быть опубликованным стабильным Release.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out _)) throw new InvalidDataException("Тег Release должен иметь вид v0.5.0.");
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        foreach (var name in new[] { ReleaseTrust.ManifestAsset, ReleaseTrust.SignatureAsset, ReleaseTrust.ArchiveAsset })
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("state").GetString() != "uploaded" || !string.Equals(matches[0].GetProperty("browser_download_url").GetString(), Asset(repository, tag, name).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Нет однозначного готового release asset: " + name);
        }
        var manifestBytes = await Small(Asset(repository, tag, ReleaseTrust.ManifestAsset), 2 * 1024 * 1024, ct);
        var signature = await Small(Asset(repository, tag, ReleaseTrust.SignatureAsset), 1024, ct);
        var manifest = ReleaseTrust.Verify(manifestBytes, signature, _testKey);
        if (manifest.Repository != repository || manifest.Tag != tag || assets.Single(a => a.GetProperty("name").GetString() == ReleaseTrust.ArchiveAsset).GetProperty("size").GetInt64() != manifest.Archive.Size)
            throw new InvalidDataException("Подписанное обновление относится к другому репозиторию/тегу или имеет другой размер.");
        return new(manifest, manifestBytes, signature);
    }
    public async Task Download(GitHubOffer offer, string cache, string stage, Action<long, long, double> progress, CancellationToken ct)
    {
        var manifest = ReleaseTrust.Verify(offer.ManifestBytes, offer.Signature, _testKey);
        ReleasePolicy.NoLinks(cache); ReleasePolicy.NoLinks(stage);
        Directory.CreateDirectory(cache); Directory.CreateDirectory(stage);
        var archive = manifest.Archive;
        var path = Path.Combine(cache, archive.Sha256.ToUpperInvariant() + ".zip.part");
        ReleasePolicy.NoLinks(path);
        var received = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (received > archive.Size) { File.Delete(path); received = 0; }
        var initial = received; var watch = Stopwatch.StartNew();
        if (received < archive.Size)
        {
            using var response = await Get(Asset(manifest.Repository, manifest.Tag, archive.Name), received, ct);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != received || range.Length != archive.Size || range.To != archive.Size - 1) throw new InvalidDataException("Неверный Content-Range при докачке.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) { received = 0; initial = 0; }
            else throw new InvalidDataException("Неподдерживаемый ответ загрузки GitHub.");
            if (response.Content.Headers.ContentLength is long length && length != archive.Size - received) throw new InvalidDataException("Неверный размер архива в ответе.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(path, received == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072]; int n;
            while ((n = await source.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct)) > 0)
            {
                if (received + n > archive.Size) throw new InvalidDataException("Архив больше заявленного.");
                await file.WriteAsync(buffer.AsMemory(0, n), ct); received += n;
                progress(received, archive.Size, (received - initial) / Math.Max(.001, watch.Elapsed.TotalSeconds));
            }
        }
        if (new FileInfo(path).Length != archive.Size) throw new IOException("Загрузка прервана. Частичный архив сохранён для докачки.");
        if (!string.Equals(await ReleasePolicy.Hash(path, ct), archive.Sha256, StringComparison.OrdinalIgnoreCase))
        { File.Delete(path); throw new InvalidDataException("SHA-256 архива не совпадает. Повреждённая загрузка удалена, установка запрещена."); }
        progress(archive.Size, archive.Size, 0);
        var expected = manifest.Release.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count != expected.Count) throw new InvalidDataException("Состав архива отличается от манифеста.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!expected.TryGetValue(entry.FullName, out var item) || !seen.Add(entry.FullName) || entry.Length != item.Size ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("Неподдерживаемый файл/ссылка в архиве: " + entry.FullName);
                var target = ReleasePolicy.SafePath(stage, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                var buffer = new byte[131072]; long written = 0; int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                { if (written + n > item.Size) throw new InvalidDataException("Распакованный файл больше заявленного."); await output.WriteAsync(buffer.AsMemory(0, n), ct); written += n; }
                if (written != item.Size) throw new InvalidDataException("Распакованный файл неполон.");
            }
        }
        await ReleasePolicy.Verify(stage, manifest.Release, ct);
        await File.WriteAllBytesAsync(Path.Combine(stage, ReleaseTrust.ManifestAsset), offer.ManifestBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(stage, ReleaseTrust.SignatureAsset), offer.Signature, ct);
        await File.WriteAllTextAsync(Path.Combine(stage, ReleasePolicy.ManifestName), JsonSerializer.Serialize(manifest.Release, UpdateJson.Default.LauncherRelease), ct);
        // Keep the fully verified archive: retry after a write/installation error costs no download.
    }
    public void Dispose() => _http.Dispose();
}
