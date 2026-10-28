using System.Diagnostics;
using System.Text.Json;
using Xrmpe.Launcher;

internal static class SteamReleaseCheck
{
    public static async Task Run(string[] args)
    {
        var game = Path.GetFullPath("backups/modes-ui-before-20261017/isolated-game");
        var output = Path.GetFullPath(args[Array.IndexOf(args, "--steam-rc") + 1]);
        if (!output.StartsWith(Path.GetFullPath("artifacts/release-candidate-0.6.0-20261017") + Path.DirectorySeparatorChar)) throw new Exception("Only RC test output allowed");
        Directory.CreateDirectory(output);
        if (Process.GetProcessesByName("xrEngine").Length != 0) throw new Exception("An existing game is running; no changes made");
        foreach (var port in new[] { 5445, 5450, 55446 }) { using var check = new System.Net.Sockets.UdpClient(port); }
        var logPaths = new[] { Path.Combine(game, "_appdata_server_custom", "logs"), Path.Combine(game, "_appdata_", "logs") };
        for (int i = 0; i < logPaths.Length; i++) if (Directory.Exists(logPaths[i]))
        {
            var before = Path.Combine(output, "before-" + i); Directory.CreateDirectory(before);
            foreach (var file in Directory.GetFiles(logPaths[i], "*.log")) File.Copy(file, Path.Combine(before, Path.GetFileName(file)), false);
        }
        var serverInfo = LauncherService.EnableSteam(LauncherService.CreateServerStartInfo(game, "mp_kpp_2", "ZoneLink_Steam_RC", 32, 55446, 60));
        var clientInfo = LauncherService.EnableSteam(LauncherService.CreateClientStartInfo(game, 55446));
        await File.WriteAllTextAsync(Path.Combine(output, "commands.json"), JsonSerializer.Serialize(new[] { new { Role="server", Exe=serverInfo.FileName, Cwd=serverInfo.WorkingDirectory, Args=serverInfo.ArgumentList.ToArray() }, new { Role="client", Exe=clientInfo.FileName, Cwd=clientInfo.WorkingDirectory, Args=clientInfo.ArgumentList.ToArray() } }, new JsonSerializerOptions { WriteIndented=true }));
        var runner = new LauncherService(); var lines = new List<string>();
        void Log(string text) { lock (lines) { if (lines.Count < 20000) lines.Add(text); } }
        Task<int>? server = null, client = null;
        var identities = new Dictionary<int, (string Path, DateTime Start)>();
        void Track()
        {
            foreach (var p in Process.GetProcessesByName("xrEngine")) using (p)
            {
                var path = p.MainModule?.FileName;
                if (path == serverInfo.FileName || path == clientInfo.FileName) identities.TryAdd(p.Id, (path!, p.StartTime.ToUniversalTime()));
            }
        }
        try
        {
            server = runner.RunSteamServerAsync(game, "mp_kpp_2", "ZoneLink_Steam_RC", 32, 55446, 60, Log);
            await Task.Delay(2000); Track();
            if (server.IsCompleted) throw new Exception("Steam dedicated ended before client start; see runtime-output.log");
            client = runner.RunSteamClientAsync(game, 55446, Log);
            var until = DateTime.UtcNow.AddMinutes(10);
            while (!server.IsCompleted && !client.IsCompleted && DateTime.UtcNow < until && !File.Exists(Path.Combine(output, "stop.flag")))
            {
                Track();
                await File.WriteAllTextAsync(Path.Combine(output, "processes.json"), JsonSerializer.Serialize(identities.Select(x => new {Pid=x.Key, Exe=x.Value.Path, StartUtc=x.Value.Start})));
                await Task.Delay(1000);
            }
            Console.WriteLine($"Steam server completed={server.IsCompleted}; client completed={client.IsCompleted}");
        }
        finally
        {
            // Only processes started after the initial empty-process preflight, with matching path and creation time.
            foreach (var entry in identities.Reverse())
            {
                try { using var p = Process.GetProcessById(entry.Key); if (p.MainModule?.FileName != entry.Value.Path || p.StartTime.ToUniversalTime() != entry.Value.Start) continue;
                    if (!p.CloseMainWindow() || !p.WaitForExit(2000)) { p.Kill(); await p.WaitForExitAsync(); }
                } catch (ArgumentException) { }
            }
            await runner.StopServerAsync();
            foreach (var task in new[] { server, client }) if (task is not null) try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) { Log(ex.Message); }
            lock (lines) File.WriteAllLines(Path.Combine(output, "runtime-output.log"), lines);
            for (int i=0;i<logPaths.Length;i++) if (Directory.Exists(logPaths[i])) foreach(var file in Directory.GetFiles(logPaths[i], "*.log")) File.Copy(file, Path.Combine(output, (i==0?"server-":"client-")+Path.GetFileName(file)), true);
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ServerExit=server?.IsCompletedSuccessfully==true?server.Result:(int?)null, ClientExit=client?.IsCompletedSuccessfully==true?client.Result:(int?)null, SteamInvitationTested=false, RemoteFriendTested=false }));
        }
    }
}
