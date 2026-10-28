using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xrmpe.SelfUpdate;

var repoRoot = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var preparedRelease = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
var assets = preparedRelease is null ? Path.Combine(repoRoot, "artifacts/github-updates-preview/TestReleaseAssets") : Path.Combine(preparedRelease, "ReleaseAssets");
var publication = preparedRelease is null ? Path.Combine(repoRoot, "artifacts/github-updates-preview/app") : Path.Combine(preparedRelease, "app");
var work = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(repoRoot, "artifacts/github-updates-preview/test-evidence", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
void Assert(bool condition, string label) { if (!condition) throw new Exception(label); }
async Task Rejected(Func<Task> action, string label)
{ try { await action(); } catch { Console.WriteLine("PASS: rejected " + label); return; } throw new Exception("Accepted: " + label); }
void CopyTree(string from, string to)
{
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    { var output = Path.Combine(to, Path.GetRelativePath(from, file)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output); }
}
var signedBytes = await File.ReadAllBytesAsync(Path.Combine(assets, ReleaseTrust.ManifestAsset));
var signature = await File.ReadAllBytesAsync(Path.Combine(assets, ReleaseTrust.SignatureAsset));
var manifest = ReleaseTrust.Verify(signedBytes, signature);
var archiveFile = Path.Combine(assets, ReleaseTrust.ArchiveAsset);
await using var server = await ReleaseFixture.Start(manifest, assets);
GitHubReleaseClient Client() => new(new HttpClient(new FixtureTransport(server.Address, server.Certificate)) { Timeout = TimeSpan.FromSeconds(10) }, null);
using var client = Client();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var offer = await client.Check(manifest.Repository, timeout.Token);
Assert(offer.Manifest.Release.Version == manifest.Release.Version, "Signed version matches supplied release");
Assert(!server.AuthorizationSeen, "No token on HTTPS requests");
Console.WriteLine("PASS: real TLS localhost GitHub-compatible /releases/latest + assets, no GitHub authentication, embedded publisher RSA-PSS signature.");
await Rejected(() => { var damaged = signedBytes.ToArray(); damaged[^2] ^= 1; ReleaseTrust.Verify(damaged, signature); return Task.CompletedTask; }, "modified signed manifest");
await Rejected(() => { using var attacker = RSA.Create(3072); ReleaseTrust.Verify(signedBytes, attacker.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); return Task.CompletedTask; }, "untrusted signing key");
server.BadSignature = true;
await Rejected(async () => await client.Check(manifest.Repository, timeout.Token), "tampered downloaded signature"); server.BadSignature = false;
server.RedirectOutside = true;
await Rejected(async () => await client.Check(manifest.Repository, timeout.Token), "redirect outside GitHub HTTPS hosts"); server.RedirectOutside = false;
server.Offline = true;
await Rejected(async () => await client.Check(manifest.Repository, timeout.Token), "no usable internet/service"); server.Offline = false;
await Rejected(async () => await client.Check("another/repository", timeout.Token), "signed repository mismatch");
server.BadAssetSize = true;
await Rejected(async () => await client.Check(manifest.Repository, timeout.Token), "API/archive size mismatch"); server.BadAssetSize = false;

var cache = Path.Combine(work, "cache"); var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher/SelfUpdate", Guid.NewGuid().ToString("N"));
server.AbortOnce = true;
await Rejected(() => client.Download(offer, cache, stage, (_, _, _) => { }, timeout.Token), "actual interrupted TLS archive body");
var partial = Path.Combine(cache, manifest.Archive.Sha256 + ".zip.part");
Assert(File.Exists(partial) && new FileInfo(partial).Length > 0 && new FileInfo(partial).Length < manifest.Archive.Size, "Partial persisted");
var offset = new FileInfo(partial).Length;
await client.Download(offer, cache, stage, (_, _, _) => { }, timeout.Token);
Assert(server.RangeFrom == offset, "Exact persisted byte offset resumed");

var badRangeCache = Path.Combine(work, "bad-range-cache"); Directory.CreateDirectory(badRangeCache);
var rangePrefix = new byte[65536];
using (var input = File.OpenRead(archiveFile)) await input.ReadExactlyAsync(rangePrefix);
await File.WriteAllBytesAsync(Path.Combine(badRangeCache, manifest.Archive.Sha256 + ".zip.part"), rangePrefix);
server.BadRange = true;
await Rejected(() => client.Download(offer, badRangeCache, Path.Combine(work, "bad-range-stage"), (_, _, _) => { }, timeout.Token), "incorrect Content-Range");
server.BadRange = false;

var rollbackSource = args.Length > 4 ? Path.GetFullPath(args[4]) : Path.Combine(repoRoot, "artifacts/installer-release-0.3.1/app");
var rollbackTarget = Path.Combine(work, "Откат после частичной замены"); CopyTree(rollbackSource, rollbackTarget);
await ReleaseTrust.VerifyStage(stage);
if (preparedRelease is not null)
{
    foreach (var name in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-COMPONENTS.json" })
        Assert(manifest.Release.Files.Any(f => f.Path == name) && File.Exists(Path.Combine(stage, name)), "Signed notices downloaded: " + name);
    var licensePath = Path.Combine(stage, "LICENSE.txt");
    var originalLicense = await File.ReadAllBytesAsync(licensePath);
    await File.AppendAllTextAsync(licensePath, "tampered notice");
    await Rejected(() => ReleaseTrust.VerifyStage(stage), "modified license document in signed stage");
    await File.WriteAllBytesAsync(licensePath, originalLicense);
    await ReleaseTrust.VerifyStage(stage);
    Console.WriteLine("PASS: all license documents are signed, hashed and verified by the actual update client.");
}
using (var installer = new Installer(rollbackTarget))
    await Rejected(() => installer.Apply(stage, manifest.Release, index => { if (index == manifest.Release.Files.Count) throw new IOException("Injected write failure"); }), "actual installation write error after all payload files including notices");
await ReleasePolicy.Verify(rollbackTarget, ReleasePolicy.Read(rollbackSource));
if (preparedRelease is not null)
    Assert(File.Exists(Path.Combine(rollbackTarget, "THIRD-PARTY-COMPONENTS.json")) == File.Exists(Path.Combine(rollbackSource, "THIRD-PARTY-COMPONENTS.json")), "Rollback restores original license inventory state");
Console.WriteLine("PASS: automatic transaction rollback to the supplied previous release after a signed, verified download and partial replacement failure.");
var stageSignature = Path.Combine(stage, ReleaseTrust.SignatureAsset);
await File.WriteAllBytesAsync(stageSignature, new byte[signature.Length]);
await HelperRejects(rollbackTarget, "updater independently rejects a tampered stage signature");
await ReleasePolicy.Verify(rollbackTarget, ReleasePolicy.Read(rollbackSource));
await File.WriteAllBytesAsync(stageSignature, signature);
await ReleaseTrust.VerifyStage(stage);
Assert(!Directory.GetFiles(stage, "xrEngine.exe", SearchOption.AllDirectories).Any(), "No game binaries");
Console.WriteLine($"PASS: real connection abort, Range resume at {offset}, archive SHA-256, signed allowlist extraction, per-file hashes; verified stage ready.");

// A corrupted complete cached archive is never installed, and a later retry can download afresh.
using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write)) file.WriteByte(0x13);
await Rejected(() => client.Download(offer, cache, Path.Combine(work, "bad-stage"), (_, _, _) => { }, timeout.Token), "corrupted archive");
Assert(!File.Exists(partial), "Corrupt cache discarded");
await client.Download(offer, cache, stage, (_, _, _) => { }, timeout.Token);

// Original-user profile is observed but never replaced by test data.
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher");
var before = Directory.GetFiles(dataRoot, "*.json").Where(x => Path.GetFileName(x) != "self-update-result.json").ToDictionary(x => x, File.ReadAllBytes);
foreach (var original in args.Length > 4
    ? new[] { rollbackSource }
    : new[] { "artifacts/installer-release-0.3.1/app", "artifacts/server-browser-preview/app", "artifacts/github-release-0.5.0-licenses-20261016/app", "artifacts/release-candidate-0.6.0-20261017/app" }.Select(source => Path.Combine(repoRoot, source)).Where(source => Environment.GetEnvironmentVariable("ZONELINK_CURRENT_ONLY") != "1" || source.Contains("0.6.0")))
{
    var target = Path.Combine(work, "Переносимая " + ReleasePolicy.Read(original).Version, "app");
    CopyTree(original, target);
    await File.WriteAllTextAsync(Path.Combine(target, "user-settings.json"), "keep user settings");
    Directory.CreateDirectory(Path.Combine(target, "saves")); await File.WriteAllTextAsync(Path.Combine(target, "saves/keep.sav"), "keep save");
    var marker = Path.Combine(target, "user-settings.json");
    // Real .lnk creation/launch occurs in the isolated work directory, never on the desktop.
    var shortcut = Path.Combine(Path.GetDirectoryName(target)!, "Существующий xrMPE.lnk");
    var ps = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
    ps.ArgumentList.Add("-NoProfile"); ps.ArgumentList.Add("-NonInteractive"); ps.ArgumentList.Add("-Command");
    var q = (string x) => "'" + x.Replace("'", "''") + "'";
    ps.ArgumentList.Add("$shell=New-Object -ComObject WScript.Shell; $link=$shell.CreateShortcut(" + q(shortcut) + "); $link.TargetPath=" + q(Path.Combine(target, ReleasePolicy.Executable)) + "; $link.WorkingDirectory=" + q(target) + "; $link.Save()");
    using (var create = Process.Start(ps)!) { await create.WaitForExitAsync(); Assert(create.ExitCode == 0, "Create test shortcut"); }
    var shortcutHash = await ReleasePolicy.Hash(shortcut);
    await UpdateTarget(target, stage, publication, timeout.Token);
    await HelperRejects(target, "same-version/downgrade signed package");
    Assert(await ReleasePolicy.Hash(shortcut) == shortcutHash, "Existing shortcut bytes unchanged");
    Assert(await File.ReadAllTextAsync(marker) == "keep user settings", "Local user settings preserved");
    Assert(await File.ReadAllTextAsync(Path.Combine(target, "saves/keep.sav")) == "keep save", "Local saves preserved");
    using var startLink = Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true })!;
    var opened = await FindWindow(Path.Combine(target, ReleasePolicy.Executable));
    Assert(opened is not null, "Existing .lnk opens new version");
    opened!.CloseMainWindow(); await opened.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); opened.Dispose();
    Console.WriteLine("PASS: signed HTTPS package -> existing " + ReleasePolicy.Read(original).Version + " portable path -> actual " + manifest.Release.Version + " window restart; existing Cyrillic .lnk unchanged and launches; user files preserved.");
}
// Optionally exercise an independently installed Inno fixture, with its own AppId and shortcut.
if (args.Length > 1 && args[1] != "-")
{
    var target = Path.GetFullPath(args[1]);
    var uninstall = Directory.GetFiles(target, "unins*").ToDictionary(x => x, File.ReadAllBytes);
    Assert(uninstall.Count >= 2, "Real installed fixture");
    var shortcut = Path.Combine(target, "Existing Launcher.lnk");
    var hash = await ReleasePolicy.Hash(shortcut);
    await UpdateTarget(target, stage, publication, timeout.Token);
    foreach (var file in uninstall) Assert(File.ReadAllBytes(file.Key).SequenceEqual(file.Value), "Uninstaller unchanged after signed update");
    Assert(await ReleasePolicy.Hash(shortcut) == hash, "Installed shortcut unchanged");
    Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true });
    var opened = await FindWindow(Path.Combine(target, ReleasePolicy.Executable));
    Assert(opened is not null, "Installed shortcut launches updated window"); opened!.CloseMainWindow(); await opened.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); opened.Dispose();
    Console.WriteLine("PASS: real isolated Inno installation upgraded from signed HTTPS package; installed .lnk and uninstaller preserved.");
}
foreach (var file in before) Assert(File.ReadAllBytes(file.Key).SequenceEqual(file.Value), "Actual user profile preserved: " + file.Key);
Console.WriteLine("PASS: actual user's game path, history and favorites/settings bytes preserved.");
Console.WriteLine("Evidence: " + work);

async Task HelperRejects(string target, string label)
{
    var helper = Path.Combine(stage, "updater-helper.exe"); File.Copy(Path.Combine(publication, "Xrmpe.Updater.exe"), helper, true);
    var job = Path.Combine(stage, "rejected-job.json");
    await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new InstallJob(target, stage, 0, 0, Restart: false, RequireSignature: true), UpdateJson.Default.InstallJob));
    var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add(job);
    using var updater = Process.Start(start)!;
    var stdout = updater.StandardOutput.ReadToEndAsync(); var stderr = updater.StandardError.ReadToEndAsync();
    await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Assert(updater.ExitCode == 1, label); await stdout; await stderr;
    Console.WriteLine("PASS: " + label + "; target not replaced.");
}

async Task UpdateTarget(string target, string verifiedStage, string latest, CancellationToken token)
{
    using var old = Process.Start(new ProcessStartInfo(Path.Combine(target, ReleasePolicy.Executable)) { UseShellExecute = false, WorkingDirectory = target })!;
    var readyWindow = await FindWindow(Path.Combine(target, ReleasePolicy.Executable)); Assert(readyWindow is not null, "Old window"); readyWindow!.Dispose();
    var helper = Path.Combine(verifiedStage, "updater-helper.exe"); File.Copy(Path.Combine(latest, "Xrmpe.Updater.exe"), helper, true);
    var jobPath = Path.Combine(verifiedStage, "job.json");
    await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(new InstallJob(target, verifiedStage, old.Id, old.StartTime.ToUniversalTime().Ticks, RequireSignature: true), UpdateJson.Default.InstallJob));
    var start = new ProcessStartInfo(helper) { WorkingDirectory = verifiedStage, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add(jobPath);
    var readyPath = Path.Combine(verifiedStage, "updater.ready"); if (File.Exists(readyPath)) File.Delete(readyPath);
    using var updater = Process.Start(start)!;
    var stdout = updater.StandardOutput.ReadToEndAsync(); var stderr = updater.StandardError.ReadToEndAsync();
    for (var n = 0; n < 300 && !File.Exists(readyPath) && !updater.HasExited; n++) await Task.Delay(100, token);
    Assert(File.Exists(readyPath), "Signed updater reverified manifest and ready");
    Assert(!updater.HasExited, "Updater waits old process");
    old.CloseMainWindow(); await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20), token);
    await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90), token);
    Console.WriteLine(await stdout); Console.WriteLine(await stderr);
    Assert(updater.ExitCode == 0, "Updater installation and healthy restart");
    await ReleasePolicy.Verify(target, manifest.Release, token);
    var restarted = await FindWindow(Path.Combine(target, ReleasePolicy.Executable)); Assert(restarted is not null, "Restarted actual new window");
    restarted!.CloseMainWindow(); await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15), token); restarted.Dispose();
}
static async Task<Process?> FindWindow(string exe)
{
    exe = Path.GetFullPath(exe);
    for (var attempt = 0; attempt < 150; attempt++)
    {
        foreach (var process in Process.GetProcessesByName("Xrmpe.Launcher"))
        {
            try { if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) && process.MainWindowHandle != 0) return process; }
            catch { }
            process.Dispose();
        }
        await Task.Delay(100);
    }
    return null;
}

sealed class FixtureTransport(Uri address, X509Certificate2 certificate) : HttpMessageHandler
{
    private readonly HttpClient _forward = new(new HttpClientHandler { AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && cert.RawData.AsSpan().SequenceEqual(certificate.RawData) });
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var route = request.RequestUri!.Host == "api.github.com" ? "/api" + request.RequestUri.AbsolutePath :
            request.RequestUri.Host == "github.com" ? "/assets/" + request.RequestUri.Segments.Last() : "/cdn/" + request.RequestUri.Segments.Last();
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, new Uri(address, route));
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await _forward.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
}

sealed class ReleaseFixture : IAsyncDisposable
{
    private WebApplication _app = null!;
    public Uri Address { get; private set; } = null!;
    public X509Certificate2 Certificate { get; private set; } = null!;
    public bool BadSignature, RedirectOutside, Offline, AbortOnce, AuthorizationSeen, BadAssetSize, BadRange;
    public long? RangeFrom;
    public static async Task<ReleaseFixture> Start(GitHubManifest manifest, string assets)
    {
        var instance = new ReleaseFixture();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        instance.Certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(instance.Certificate)));
        instance._app = builder.Build();
        instance._app.Run(async context =>
        {
            if (context.Request.Headers.ContainsKey("Authorization")) instance.AuthorizationSeen = true;
            if (instance.Offline) { context.Response.StatusCode = 503; return; }
            var path = context.Request.Path.Value!;
            if (path.StartsWith("/api/"))
            {
                await context.Response.WriteAsJsonAsync(new { draft = false, prerelease = false, tag_name = manifest.Tag,
                    assets = new[] { ReleaseTrust.ManifestAsset, ReleaseTrust.SignatureAsset, ReleaseTrust.ArchiveAsset }.Select(name => new {
                        name, state = "uploaded", browser_download_url = GitHubReleaseClient.Asset(manifest.Repository, manifest.Tag, name).AbsoluteUri,
                        size = new FileInfo(Path.Combine(assets, name)).Length + (instance.BadAssetSize && name == ReleaseTrust.ArchiveAsset ? 1 : 0) }) }); return;
            }
            var fileName = Path.GetFileName(path);
            if (!new[] { ReleaseTrust.ManifestAsset, ReleaseTrust.SignatureAsset, ReleaseTrust.ArchiveAsset }.Contains(fileName)) { context.Response.StatusCode = 404; return; }
            if (path.StartsWith("/assets/"))
            { context.Response.Redirect(instance.RedirectOutside ? "http://evil.invalid/asset" : "https://release-assets.githubusercontent.com/fixture/" + fileName); return; }
            var file = Path.Combine(assets, fileName);
            if (fileName == ReleaseTrust.SignatureAsset && instance.BadSignature) { await context.Response.Body.WriteAsync(new byte[384]); return; }
            var size = new FileInfo(file).Length; long offset = 0;
            if (context.Request.Headers.Range.FirstOrDefault() is { } range && RangeHeaderValue.TryParse(range, out var parsed))
            {
                offset = parsed.Ranges.Single().From!.Value; instance.RangeFrom = offset;
                context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {offset + (instance.BadRange ? 1 : 0)}-{size - 1}/{size}";
            }
            context.Response.ContentLength = size - offset;
            await using var input = File.OpenRead(file); input.Position = offset;
            if (fileName == ReleaseTrust.ArchiveAsset && instance.AbortOnce)
            {
                instance.AbortOnce = false; var buffer = new byte[262144]; var count = await input.ReadAsync(buffer);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count)); await context.Response.Body.FlushAsync();
                await Task.Delay(100); context.Abort(); return;
            }
            await input.CopyToAsync(context.Response.Body);
        });
        await instance._app.StartAsync(); instance.Address = new Uri(instance._app.Urls.Single()); return instance;
    }
    public async ValueTask DisposeAsync() { await _app.DisposeAsync(); Certificate.Dispose(); }
}
