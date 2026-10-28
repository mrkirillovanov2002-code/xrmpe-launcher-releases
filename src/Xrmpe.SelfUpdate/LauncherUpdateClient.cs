using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Xrmpe.SelfUpdate;

public sealed class LauncherUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    public LauncherUpdateClient(Uri server, string token)
    {
        if (server.Scheme != "http" || server.AbsolutePath != "/" || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("Нужен HTTP-адрес LAN/VPN сервера без пути.");
        if (token.Length is < 16 or > 128 || !token.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Укажите токен хоста: 16–128 латинских букв/цифр.");
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<LauncherRelease> Check(CancellationToken ct)
    {
        using var response = await _http.GetAsync("launcher/manifest", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("Хост не раздаёт обновление лаунчера. На хосте включите этот канал и выберите папку новой публикации.");
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[32768]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + n > 2 * 1024 * 1024) throw new InvalidDataException("Манифест лаунчера слишком большой.");
            data.Write(buffer, 0, n);
        }
        var release = JsonSerializer.Deserialize(data.ToArray(), UpdateJson.Default.LauncherRelease) ?? throw new InvalidDataException("Пустой манифест.");
        ReleasePolicy.Validate(release); return release;
    }
    public async Task Download(LauncherRelease release, string stage, Action<long,long,double> progress, CancellationToken ct)
    {
        ReleasePolicy.Validate(release); ReleasePolicy.NoLinks(stage);
        Directory.CreateDirectory(stage);
        var total = release.Files.Sum(f => f.Size); long done = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in release.Files)
        {
            var path = ReleasePolicy.SafePath(stage, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var response = await _http.GetAsync("launcher/files/" + file.Sha256, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != file.Size) throw new InvalidDataException("Неверный размер ответа: " + file.Path);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long received = 0; int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (received + n > file.Size) throw new InvalidDataException("Файл больше заявленного: " + file.Path);
                    await target.WriteAsync(buffer.AsMemory(0,n), ct); received += n; done += n;
                    progress(done, total, done / Math.Max(.001, clock.Elapsed.TotalSeconds));
                }
            }
            if (new FileInfo(path).Length != file.Size || !string.Equals(await ReleasePolicy.Hash(path, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка SHA-256 не пройдена: " + file.Path);
        }
        await File.WriteAllTextAsync(Path.Combine(stage, ReleasePolicy.ManifestName), JsonSerializer.Serialize(release, UpdateJson.Default.LauncherRelease), ct);
        await ReleasePolicy.Verify(stage, release, ct);
    }
    public void Dispose() => _http.Dispose();
}
