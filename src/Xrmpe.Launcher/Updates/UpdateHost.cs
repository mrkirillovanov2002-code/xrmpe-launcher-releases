using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Xrmpe.SelfUpdate;

namespace Xrmpe.Launcher.Updates;

public sealed class UpdateHost : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly SemaphoreSlim _slots = new(4);
    private readonly SemaphoreSlim _bandwidth = new(1);
    public Uri? Address { get; private set; }
    public long ResumedRequests;
    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public async Task StartAsync(string root, IPAddress bind, int port, string token, int kibPerSecond, bool allowRemote,
        Action<string> log, CancellationToken ct, Action? ensureIdle = null, string? launcherRoot = null)
    {
        if (_app is not null) throw new InvalidOperationException("Раздача уже запущена.");
        if (!IPAddress.IsLoopback(bind) && !allowRemote) throw new InvalidOperationException("Для внешнего адреса явно включите доступ с других компьютеров.");
        if (token.Length < 16 || token.Length > 128 || !token.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Токен: 16–128 латинских букв/цифр.");
        if (port is < 0 or > 65535 || kibPerSecond is < 64 or > 1048576) throw new ArgumentException("Проверьте порт и ограничение скорости.");
        var idle = ensureIdle ?? UpdateFiles.EnsureIdle;
        idle();
        root = Path.GetFullPath(root);
        var manifest = await UpdateFiles.BuildManifestAsync(root, log, ct).ConfigureAwait(false);
        idle();
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Манифест превышает 32 МиБ.");
        var byHash = manifest.Files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        byte[]? launcherJson = null;
        var launcherFiles = new Dictionary<string, ReleaseFile>(StringComparer.OrdinalIgnoreCase);
        if (launcherRoot is not null)
        {
            launcherRoot = Path.GetFullPath(launcherRoot);
            var release = ReleasePolicy.Read(launcherRoot);
            await ReleasePolicy.Verify(launcherRoot, release, ct);
            launcherJson = JsonSerializer.SerializeToUtf8Bytes(release);
            launcherFiles = release.Files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // Never log request Authorization headers.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(bind, port, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var app = builder.Build();
        app.Run(async context =>
        {
            var auth = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(auth, secret)) { context.Response.StatusCode = 401; return; }
            if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            if (!await _slots.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 503; return; }
            try
            {
                if (context.Request.Path == "/launcher/manifest")
                {
                    if (launcherJson is null) { context.Response.StatusCode = 404; return; }
                    context.Response.ContentType = "application/json"; context.Response.ContentLength = launcherJson.Length;
                    await context.Response.Body.WriteAsync(launcherJson, context.RequestAborted); return;
                }
                if (context.Request.Path == "/manifest")
                {
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength = json.Length;
                    await context.Response.Body.WriteAsync(json, context.RequestAborted);
                    return;
                }
                var path = context.Request.Path.Value ?? "";
                UpdateEntry entry;
                string file;
                if (path.StartsWith("/launcher/files/", StringComparison.Ordinal) && launcherRoot is not null && launcherFiles.TryGetValue(path[16..], out var launcherEntry))
                {
                    entry = new UpdateEntry(launcherEntry.Path, launcherEntry.Size, launcherEntry.Sha256);
                    file = ReleasePolicy.SafePath(launcherRoot, entry.Path);
                }
                else if (path.StartsWith("/files/", StringComparison.Ordinal) && byHash.TryGetValue(path[7..], out var gameEntry))
                { entry = gameEntry; file = UpdateFiles.SafePath(root, entry.Path); }
                else { context.Response.StatusCode = 404; return; }
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                if (stream.Length != entry.Size) { context.Response.StatusCode = 409; return; }
                long offset = 0;
                var range = context.Request.Headers.Range.ToString();
                if (range.Length > 0)
                {
                    if (!range.StartsWith("bytes=", StringComparison.Ordinal) || !range.EndsWith('-') || !long.TryParse(range[6..^1], out offset) || offset < 0 || offset >= entry.Size)
                    { context.Response.StatusCode = 416; return; }
                    if (context.Request.Headers.IfRange.ToString() != $"\"{entry.Sha256}\"") { context.Response.StatusCode = 412; return; }
                    context.Response.StatusCode = 206;
                    context.Response.Headers.ContentRange = $"bytes {offset}-{entry.Size - 1}/{entry.Size}";
                    Interlocked.Increment(ref ResumedRequests);
                }
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.Headers.ETag = $"\"{entry.Sha256}\"";
                context.Response.ContentLength = entry.Size - offset;
                context.Response.ContentType = "application/octet-stream";
                stream.Position = offset;
                var buffer = new byte[32768];
                int count;
                while ((count = await stream.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    // One shared pacing lock: the configured cap applies to all clients together.
                    await _bandwidth.WaitAsync(context.RequestAborted);
                    try
                    {
                        var clock = Stopwatch.StartNew();
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        var wait = TimeSpan.FromSeconds(count / (kibPerSecond * 1024d)) - clock.Elapsed;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, context.RequestAborted);
                    }
                    finally { _bandwidth.Release(); }
                }
            }
            catch (OperationCanceledException) { context.Abort(); }
            catch (Exception ex)
            {
                log($"HTTP-раздача: {ex.GetType().Name}. Обновите манифест перезапуском раздачи, если файлы изменились.");
                if (!context.Response.HasStarted) context.Response.StatusCode = 500; else context.Abort();
            }
            finally { _slots.Release(); }
        });
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            _app = app;
            Address = new Uri(app.Urls.Single());
            log($"Раздача: {Address} · файлов {manifest.Files.Count} · лимит {kibPerSecond} КиБ/с. Токен не записывается в журнал.");
        }
        catch { await app.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            finally { await _app.DisposeAsync(); _app = null; }
        }
    }
}
