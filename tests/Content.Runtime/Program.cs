using System.Diagnostics;
using System.Text.Json;
using Xrmpe.Launcher;

if (args.Contains("--steam-rc")) { await SteamReleaseCheck.Run(args); return; }
if (args.Contains("--audit-servers") || args.Contains("--audit-client") || args.Contains("--audit-rotation") || args.Contains("--audit-console")) { await MapAudit.Run(args); return; }
if (args.Contains("--local-session")) { await LocalSession.Run(args); return; }

var game = Path.GetFullPath(args[0]);
var expected = Path.GetFullPath("backups/modes-ui-before-20261017/isolated-game");
if (!game.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new Exception("Only the explicit physical isolated copy is permitted");
if (Directory.EnumerateFileSystemEntries(game, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new Exception("No links in isolation");
var mapAt = Array.IndexOf(args, "--maps");
var maps = mapAt < 0 ? new[] { "mp_kpp_2" } : args[(mapAt + 1)..];
string ReadLog(string dir)
{
    var root = Path.Combine(game, dir, "logs");
    if (!Directory.Exists(root)) return "";
    var path = Directory.EnumerateFiles(root, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    if (path is null) return "";
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
foreach (var map in maps)
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(map, @"\Amp_[a-zA-Z0-9_]+\z") || !File.Exists(Path.Combine(game, "mp", map + ".db"))) throw new Exception("Unknown map fixture");
    const int port = 55446;
    var start = LauncherService.CreateServerStartInfo(game, map, "xrMPE_map_test", 32, port, 60); start.WindowStyle = ProcessWindowStyle.Hidden;
    Console.WriteLine("ISOLATED MAP: " + map); Console.WriteLine("ARGS: " + string.Join(" ", start.ArgumentList));
    using var server = Process.Start(start) ?? throw new Exception("No server process");
    var serverOut = server.StandardOutput.ReadToEndAsync(); var serverError = server.StandardError.ReadToEndAsync();
    Process? client = null; Task<string>? clientOut = null, clientError = null;
    bool qr2 = false, clientLoaded = false; string mode = ""; string clientLog = "", serverLog = "";
    try
    {
        var watch = Stopwatch.StartNew();
        while (!server.HasExited && watch.Elapsed < TimeSpan.FromSeconds(40))
        {
            await Task.Delay(800);
            var reply = await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1", port, 5445)), CancellationToken.None);
            if (reply.Snapshot is { } snapshot && snapshot.Map == map) { qr2 = true; mode = snapshot.Mode; Console.WriteLine($"QR2 {map}: {mode}, {snapshot.Players}/{snapshot.MaxPlayers}"); break; }
        }
        if (qr2 && args.Contains("--client"))
        {
            var cs = LauncherService.CreateRemoteClientStartInfo(game, "127.0.0.1", port); cs.WindowStyle = ProcessWindowStyle.Hidden;
            client = Process.Start(cs) ?? throw new Exception("No isolated client"); clientOut = client.StandardOutput.ReadToEndAsync(); clientError = client.StandardError.ReadToEndAsync();
            watch.Restart();
            // A freshly started log can still be briefly locked; never reuse the previous map's completion.
            while (!client.HasExited && !server.HasExited && watch.Elapsed < TimeSpan.FromSeconds(65))
            {
                await Task.Delay(1000); try { clientLog = ReadLog("_appdata_"); } catch (IOException) { continue; }
                clientLoaded = clientLog.Contains("net_start_client6") && (clientLog.Contains("respawned") || clientLog.Contains("LEVEL:SPAWN"));
                if (clientLoaded || clientLog.Contains("FATAL ERROR")) break;
            }
            try { clientLog = ReadLog("_appdata_"); } catch (IOException) { }
        }
        try { serverLog = ReadLog("_appdata_server_custom"); } catch (IOException) { }
        var result = new { Map = map, Addon = "xrmpe_pvp.addon", Mode = mode, Qr2 = qr2, ServerAlive = !server.HasExited,
            ClientLevelLoaded = clientLoaded, ClientExited = client?.HasExited,
            Fatal = serverLog.Contains("FATAL ERROR") || clientLog.Contains("FATAL ERROR"), InternalConfigurationDecoded = false };
        Console.WriteLine(JsonSerializer.Serialize(result));
        var folder = Path.GetFullPath("artifacts/global-browser-preview/Checks"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pvp-" + map + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        // Logs belong only to the isolated copy. Retain diagnostics without copying game resources into the publication.
        File.WriteAllText(Path.Combine(folder, "pvp-" + map + "-server.log"), serverLog);
        File.WriteAllText(Path.Combine(folder, "pvp-" + map + "-client.log"), clientLog);
    }
    finally
    {
        if (client is not null) { if (!client.HasExited) client.Kill(true); await client.WaitForExitAsync(); if (clientOut is not null) await clientOut; if (clientError is not null) await clientError; client.Dispose(); }
        if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); await serverOut; await serverError;
        Console.WriteLine("Only isolated test processes stopped.");
    }
}
