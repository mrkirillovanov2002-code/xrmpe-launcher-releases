using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xrmpe.Launcher.Updates;

internal static class IntegrationChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr reserved);
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xrMPE updates тест " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "host arbitrary game"); var destination = Path.Combine(root, "friend другая папка");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        void Put(string folder, string relative, byte[] data)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, data);
        }
        byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        var token = UpdateHost.GenerateToken();
        await using var host = new UpdateHost();
        WebApplication? proxy = null;
        try
        {
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
            Put(source, "bin/content.dll", payload);
            Put(source, "addons/xrmpe_pvp.addon", Bytes("addon-new"));
            Put(source, "bin/same.dll", Bytes("same")); Put(destination, "bin/same.dll", Bytes("same"));
            Put(source, "bin/empty.dll", []);
            Put(source, "resources/vanilla.db0", Bytes("resource archive"));
            Put(destination, "bin/content.dll", Bytes("old-original"));
            Put(source, "_appdata_/savedgames/private.sav", Bytes("host private"));
            Put(source, "bin/steam_appid.txt", Bytes("480"));
            Put(source, "gamedata/configs/user.ltx", Bytes("host config"));
            Put(source, "fsgame_sc.ltx", Bytes("host-specific config"));
            Put(destination, "fsgame_sc.ltx", Bytes("friend-specific config"));
            Put(destination, "_appdata_/savedgames/private.sav", Bytes("friend save"));
            Put(destination, "bin/steam_appid.txt", Bytes("friend setting"));
            Put(destination, "bin/extra.dll", Bytes("extra do not delete"));
            await host.StartAsync(source, IPAddress.Loopback, 0, token, 1024, false, _ => { }, CancellationToken.None, () => { });
            if (host.Address is null || host.Address.Host != "127.0.0.1") throw new Exception("Not bound to loopback");
            using var raw = new HttpClient { BaseAddress = host.Address };
            using (var denied = await raw.GetAsync("manifest")) if (denied.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Unauthenticated manifest exposed");
            raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using (var denied = await raw.GetAsync("files/../../private.sav")) if (denied.IsSuccessStatusCode) throw new Exception("Unknown route exposed");

            using var client = new UpdateClient(destination, host.Address, token, () => { });
            var plan = await client.CheckAsync(_ => { }, CancellationToken.None);
            if (plan.Count != 4 || !plan.Any(f => f.Path == "resources/vanilla.db0") || plan.Any(f => f.Path.Contains("same") || f.Path.Contains("user") || f.Path.Contains("steam_appid"))) throw new Exception("Delta selection or exclusions failed");
            // A real HTTP response is cut short while advertising full Content-Length.
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            proxy = builder.Build();
            proxy.Run(async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Request.Path.Value);
                using var response = await raw.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentLength = response.Content.Headers.ContentLength;
                if (response.Headers.ETag is not null) context.Response.Headers.ETag = response.Headers.ETag.ToString();
                await using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[32768]; var sent = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read));
                    await context.Response.Body.FlushAsync(); sent += read;
                    if (context.Request.Path.StartsWithSegments("/files") && sent >= 131072)
                    { await Task.Delay(50); context.Abort(); return; }
                }
            });
            await proxy.StartAsync();
            var proxyUri = new Uri(proxy.Urls.Single());
            var large = plan.Single(f => f.Path == "bin/content.dll");
            using (var broken = new UpdateClient(destination, proxyUri, token, () => { }))
            {
                try { await broken.ApplyAsync([large], _ => { }, _ => { }, CancellationToken.None); throw new Exception("Broken connection accepted"); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { }
            }
            if (File.ReadAllText(Path.Combine(destination, "bin/content.dll")) != "old-original") throw new Exception("Original changed on interrupted transfer");
            var partial = Path.Combine(destination, ".xrmpe-updates", large.Sha256 + ".part");
            var partialBytes = new FileInfo(partial).Length;
            if (partialBytes <= 0 || partialBytes >= large.Size) throw new Exception("Partial transfer was not preserved");
            var watch = Stopwatch.StartNew();
            await client.ApplyAsync(plan, _ => { }, _ => { }, CancellationToken.None);
            if (host.ResumedRequests < 1) throw new Exception("Range resume not used");
            if (watch.Elapsed < TimeSpan.FromMilliseconds(400)) throw new Exception("Upload rate limit not applied");
            if (!File.ReadAllBytes(Path.Combine(destination, "bin/content.dll")).SequenceEqual(payload)) throw new Exception("Installed content differs");
            if ((await client.CheckAsync(_ => { }, CancellationToken.None)).Count != 0) throw new Exception("Unchanged recheck downloaded files");
            var resumedBefore = host.ResumedRequests;
            await client.ApplyAsync(plan, _ => { }, _ => { }, CancellationToken.None);
            if (host.ResumedRequests != resumedBefore) throw new Exception("Already committed files re-downloaded");
            if (File.ReadAllText(Path.Combine(destination, "bin/extra.dll")) != "extra do not delete" ||
                File.ReadAllText(Path.Combine(destination, "bin/steam_appid.txt")) != "friend setting" ||
                File.ReadAllText(Path.Combine(destination, "fsgame_sc.ltx")) != "friend-specific config" ||
                File.ReadAllText(Path.Combine(destination, "_appdata_/savedgames/private.sav")) != "friend save") throw new Exception("User or unrelated files changed");
            // Hash mismatch leaves the destination intact, even for a complete staged file.
            var wrong = new UpdateEntry("bin/content.dll", 4, new string('A', 64));
            File.WriteAllText(Path.Combine(destination, ".xrmpe-updates", wrong.Sha256 + ".part"), "oops");
            try { await client.ApplyAsync([wrong], _ => { }, _ => { }, CancellationToken.None); throw new Exception("Hash mismatch accepted"); }
            catch (InvalidDataException) { }
            if (!File.ReadAllBytes(Path.Combine(destination, "bin/content.dll")).SequenceEqual(payload)) throw new Exception("Hash failure modified original");
            foreach (var bad in new[] { "../outside.dll", "/bin/a.dll", "bin/../outside.dll", "bin\\a.dll", "bin/AUX.dll", "bin/a.dll:stream", "bin/a. ", "bin/steam_appid.txt", "gamedata/configs/user.ltx", "_appdata_/savedgames/a.sav", "bin/%2e%2e/a.dll", "fsgame_sc.ltx", "bin/custom.ltx" })
            {
                try { UpdateFiles.ValidateRelative(bad); throw new Exception("Unsafe path accepted: " + bad); }
                catch (InvalidDataException) { }
            }
            try { UpdateFiles.ValidateManifest(new(1, [new("bin/a.dll", 1, new string('A', 64)), new("BIN/A.dll", 1, new string('A', 64))])); throw new Exception("Duplicate paths accepted"); }
            catch (InvalidDataException) { }
            using (var blocked = new UpdateClient(destination, host.Address, token, () => throw new InvalidOperationException("running game")))
            {
                try { await blocked.ApplyAsync(plan, _ => { }, _ => { }, CancellationToken.None); throw new Exception("Running-game guard bypassed"); }
                catch (InvalidOperationException ex) when (ex.Message == "running game") { }
            }
            await using (var remote = new UpdateHost())
            {
                try { await remote.StartAsync(source, IPAddress.Any, 0, token, 1024, false, _ => { }, CancellationToken.None, () => { }); throw new Exception("Remote bind without explicit permission"); }
                catch (InvalidOperationException) { }
            }
            if (OperatingSystem.IsWindows())
            {
                var guardFolder = Path.Combine(root, "guard process");
                Directory.CreateDirectory(guardFolder);
                var runner = Path.Combine(guardFolder, "xrEngine.exe");
                File.Copy(Environment.ProcessPath!, runner);
                foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Updater.Integration" + suffix), Path.Combine(guardFolder, "Updater.Integration" + suffix));
                using (var guard = Process.Start(new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--idle" } })!)
                {
                    try
                    {
                        await Task.Delay(150);
                        if (guard.HasExited) throw new Exception("Engine-name fixture failed to start");
                        try { UpdateFiles.EnsureIdle(); throw new Exception("Actual xrEngine process was not detected"); }
                        catch (InvalidOperationException) { }
                    }
                    finally { if (!guard.HasExited) { guard.Kill(); await guard.WaitForExitAsync(); } }
                }
                var outside = Path.Combine(root, "outside selected games");
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "secret.dll"), "must never be shared or overwritten");
                foreach (var game in new[] { source, destination })
                {
                    var junction = Path.Combine(game, "bin", "junction");
                    var cmd = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in new[] { "/d", "/c", "mklink", "/J", junction, outside }) cmd.ArgumentList.Add(arg);
                    using var process = Process.Start(cmd)!;
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) throw new Exception("Could not create junction fixture: " + await process.StandardError.ReadToEndAsync());
                    try
                    {
                        try { UpdateFiles.SafePath(game, "bin/junction/secret.dll"); throw new Exception("Junction traversal accepted"); }
                        catch (InvalidDataException) { }
                        if (game == source && (await UpdateFiles.BuildManifestAsync(source, _ => { }, CancellationToken.None)).Files.Any(f => f.Path.Contains("junction")))
                            throw new Exception("Host followed junction");
                    }
                    finally { Directory.Delete(junction); }
                }
                var hard = Path.Combine(destination, "bin", "hard.dll");
                if (!CreateHardLink(hard, Path.Combine(outside, "secret.dll"), IntPtr.Zero)) throw new Exception("Could not create hardlink fixture");
                try
                {
                    try { UpdateFiles.SafePath(destination, "bin/hard.dll"); throw new Exception("Hardlink accepted"); }
                    catch (InvalidDataException) { }
                }
                finally { File.Delete(hard); }
                Console.WriteLine("PASS: real Windows junctions rejected on host/client; hardlink rejected; actual xrEngine-name fixture detected.");
            }
            Console.WriteLine($"PASS: localhost HTTP auth, SHA delta, zero-change recheck, real connection abort + Range resume from {partialBytes} bytes, aggregate pacing, verified atomic replacement, empty file, protected saves/settings, no deletes, invalid paths, idle guard, explicit remote bind.");
        }
        finally
        {
            if (proxy is not null) { await proxy.StopAsync(); await proxy.DisposeAsync(); }
            await host.DisposeAsync();
            var absolute = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("xrMPE updates тест ")) throw new Exception("Unsafe test cleanup");
            Directory.Delete(absolute, true);
        }
    }
}
