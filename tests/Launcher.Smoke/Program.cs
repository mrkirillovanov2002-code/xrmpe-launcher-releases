using Xrmpe.Launcher;

if (args.Contains("--log-flood"))
{
    await Task.WhenAll(Task.Run(() => { while (true) Console.Out.WriteLine("stdout flood " + new string('x', 200)); }),
        Task.Run(() => { while (true) Console.Error.WriteLine("stderr flood " + new string('y', 200)); }));
    return;
}
if (args.Contains("--idle")) { await Task.Delay(Timeout.Infinite); return; }
if (args.Contains("--exit-now")) return;
if (args.Contains("--child-parent")) {
    var c = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--idle" }, UseShellExecute = false, CreateNoWindow = true })!;
    Console.WriteLine("CHILD="+c.Id); await Task.Delay(Timeout.Infinite); return;
}
await FriendsChecks.Run();
await LoggingChecks.RunAsync();
PortabilityChecks.Run();

var root = Path.Combine(Path.GetTempPath(), "xrMPE smoke пробел & " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var service = new LauncherService();
    try { LauncherService.CreateClientStartInfo(root); throw new Exception("Missing engine was accepted"); }
    catch (FileNotFoundException) { }
    var game = Path.Combine(root, "game");
    Directory.CreateDirectory(Path.Combine(game, "bin"));
    var engine = Path.Combine(game, "bin", "xrEngine.exe");
    await File.WriteAllTextAsync(engine, "fixture only; never execute");
    Directory.CreateDirectory(Path.Combine(game, "bin", "dedicated"));
    Directory.CreateDirectory(Path.Combine(game, "addons"));
    await File.WriteAllTextAsync(Path.Combine(game, "bin", "dedicated", "xrEngine.exe"), "fixture only");
    await File.WriteAllTextAsync(Path.Combine(game, "addons", "xrmpe_pvp.addon"), "fixture only");
    await File.WriteAllTextAsync(Path.Combine(game, "fsgame_sc.ltx"), "fixture only");
    foreach (var selected in new[] { root, game })
    {
        var client = LauncherService.CreateClientStartInfo(selected);
        if (client.FileName != engine || client.WorkingDirectory != game || client.UseShellExecute ||
            !client.ArgumentList.SequenceEqual(new[] { "-addon=xrmpe_pvp.addon", "-start", "client(localhost/port=5446)" }))
            throw new Exception("Client launch differs from Connect_PvP.bat");
    }
    Console.WriteLine("PASS: client executable, game working directory, exact client arguments, root/game selection, missing engine.");
    var alternative = Path.Combine(game, "addons", "zonestrike.addon");
    await File.WriteAllTextAsync(alternative, "fixture only; never execute");
    foreach (var info in new[] {
        LauncherService.CreateClientStartInfo(game, 5501, "zonestrike.addon"),
        LauncherService.CreateRemoteClientStartInfo(game, "127.0.0.1", 5501, "zonestrike.addon"),
        LauncherService.CreateSteamMenuStartInfo(game, "zonestrike.addon"),
        LauncherService.EnableSteam(LauncherService.CreateClientStartInfo(game, 5501, "zonestrike.addon")),
        LauncherService.EnableSteam(LauncherService.CreateServerStartInfo(game, "mp_agroprom", "name", 32, 5501, 60, "zonestrike.addon")) })
        if (info.ArgumentList.Count(x => x.StartsWith("-addon=")) != 1 || !info.ArgumentList.Contains("-addon=zonestrike.addon") || info.ArgumentList.Contains("-addon=xrmpe_pvp.addon"))
            throw new Exception("Selected addon lost or mixed with default in a launch path");
    foreach (var invalid in new[] { "ZoneStrike.addon", "../x.addon", "x.addon -steam", "x\".addon", "missing.addon" })
    {
        bool rejected = false;
        try { LauncherService.CreateSteamMenuStartInfo(game, invalid); } catch (Exception ex) when (ex is ArgumentException or IOException) { rejected = true; }
        if (!rejected) throw new Exception("Unsafe/missing addon accepted: " + invalid);
    }
    try { LauncherService.CreateServerStartInfo(game, "mp_agroprom", "name", 32, 5501, 60, "zonestrike.addon", "tdm"); throw new Exception("Unverified TDM accepted"); }
    catch (ArgumentException) { }
    File.Delete(Path.Combine(game, "addons", "xrmpe_pvp.addon"));
    LauncherService.CreateClientStartInfo(game, addon: "zonestrike.addon");
    await File.WriteAllTextAsync(Path.Combine(game, "addons", "xrmpe_pvp.addon"), "fixture only");
    File.Delete(alternative);
    Console.WriteLine("PASS: selected addon across client/IP/Steam/dedicated, exactly one addon, lowercase/path validation, TDM rejection, no dependency on default addon.");
    Directory.CreateDirectory(Path.Combine(game, "bin", "dedicated"));
    await File.WriteAllTextAsync(Path.Combine(game, "bin", "dedicated", "xrEngine.exe"), "fixture only");
    await File.WriteAllTextAsync(Path.Combine(game, "fsgame_sc.ltx"), "fixture only");
    foreach (var selected in new[] { root, game })
    {
        var server = LauncherService.CreateServerStartInfo(selected, "mp_kpp_2", "Мой PvP сервер", 24, 5501, 80);
        if (server.FileName != Path.Combine(game, "bin", "dedicated", "xrEngine.exe") ||
            server.WorkingDirectory != Path.Combine(game, "bin") ||
            !server.ArgumentList.SequenceEqual(new[] { "-addon=xrmpe_pvp.addon", "-i", "-silent_error_mode", "-fsltx", @"..\fsgame_sc.ltx", "-start",
                "server(mp_kpp_2/dm/timelimit=61/fraglimit=80/hname=Мой PvP сервер/maxplayers=24/public=1/portsv=5501)", "client(localhost/portcl=5450)" }))
            throw new Exception("Dedicated launch contract failed");
        if (LauncherService.CreateClientStartInfo(selected, 5501).ArgumentList.Last() != "client(localhost/port=5501)")
            throw new Exception("Client did not use server port");
    }
    foreach (var bad in new[] { ("mp_kpp_2/dm", "name", 32, 5446, 60), ("mp_kpp_2", "name/portsv=1", 32, 5446, 60), ("mp_kpp_2", "name", 32, 0, 60), ("mp_kpp_2", "name", 32, 5450, 60), ("mp_kpp_2", "name", 0, 5446, 60), ("mp_kpp_2", "name", 32, 5446, -1) })
    {
        try { LauncherService.CreateServerStartInfo(root, bad.Item1, bad.Item2, bad.Item3, bad.Item4, bad.Item5); throw new Exception("Invalid server options accepted"); }
        catch (ArgumentException) { }
    }
    Console.WriteLine("PASS: dedicated path/CWD, all five server settings, shared client port, invalid parameters.");
    foreach (var host in new[] { "localhost", "127.0.0.1", "192.168.1.5", "203.0.113.10", "play.example.org", "сервер.рф" })
    {
        var remote = LauncherService.CreateRemoteClientStartInfo(root, host, 5512);
        if (remote.WorkingDirectory != game || remote.FileName != engine ||
            !remote.ArgumentList.SequenceEqual(new[] { "-addon=xrmpe_pvp.addon", "-start", $"client({LauncherService.NormalizeHost(host)}/port=5512)" }))
            throw new Exception("Remote client command contract failed");
    }
    foreach (var host in new[] { "", "https://example.org", "host:5446", "host/port=1", "host) server(mp)", "a b", "a\nb", "::1", "999.999.999.999", "a\"b", "-start", "example..org" })
    {
        try { LauncherService.NormalizeHost(host); throw new Exception("Invalid host accepted: " + host); }
        catch (ArgumentException) { }
    }
    var steamLocal = LauncherService.EnableSteam(LauncherService.CreateClientStartInfo(root));
    if (!steamLocal.ArgumentList.SequenceEqual(new[] { "-addon=xrmpe_pvp.addon", "-start", "client(localhost/port=5446)", "-steam" }))
        throw new Exception("Steam altered local launch parameters");
    var steamMenu = LauncherService.CreateSteamMenuStartInfo(root);
    if (!steamMenu.ArgumentList.SequenceEqual(new[] { "-addon=xrmpe_pvp.addon", "-steam" })) throw new Exception("Steam menu contract failed");
    var defaultServer = LauncherService.CreateServerStartInfo(root, "mp_kpp_2", "name", 32, 5446, 60);
    var oldArgs = defaultServer.ArgumentList.ToArray();
    LauncherService.EnableSteam(defaultServer);
    if (!defaultServer.ArgumentList.SequenceEqual(oldArgs.Append("-steam"))) throw new Exception("Steam changed dedicated parameters");
    var historyPath = Path.Combine(root, "history.json");
    var history = new ConnectionHistory(historyPath);
    IReadOnlyList<ServerAddress> addresses = history.Load();
    if (addresses.Count != 0) throw new Exception("New history not empty");
    for (var i = 0; i < 12; i++) addresses = history.Remember($"server{i}.example.org", 5446 + i, addresses);
    addresses = history.Remember("SERVER11.EXAMPLE.ORG", 5457, addresses);
    if (addresses.Count != 10 || addresses[0].Host != "server11.example.org" || history.Load().Count != 10)
        throw new Exception("History persistence/limit/deduplication failed");
    addresses = history.Remember("localhost", 5446, addresses);
    if (new ConnectionHistory(historyPath).Load()[0] != new ServerAddress("localhost", 5446)) throw new Exception("History did not restore last input");
    await File.WriteAllTextAsync(historyPath, "broken JSON");
    try { history.Load(); throw new Exception("Corrupt history accepted"); }
    catch (System.Text.Json.JsonException) { }
    Console.WriteLine("PASS: remote IPv4/domain/IDN commands, invalid host rejection, Steam flags/menu, persistent bounded MRU history.");
    try { await service.RunAsync(root, _ => { }); throw new Exception("Missing BAT was accepted"); }
    catch (FileNotFoundException) { }
    var bat = Path.Combine(root, LauncherService.BatName);
    await File.WriteAllTextAsync(bat, "@echo off\r\nif not exist cwd-marker.txt exit /b 90\r\nif not \"%~1\"==\"\" exit /b 91\r\necho OUTPUT_OK\r\necho ERROR_OK 1>&2\r\nexit /b 7\r\n");
    await File.WriteAllTextAsync(Path.Combine(root, "cwd-marker.txt"), "test");
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var code = await service.RunAsync(root, logs.Enqueue).WaitAsync(TimeSpan.FromSeconds(15));
    if (code != 7 || !logs.Any(s => s.Contains("OUTPUT_OK")) || !logs.Any(s => s.Contains("[stderr]") && s.Contains("ERROR_OK")))
        throw new Exception($"BAT contract failed: {code}\n{string.Join('\n', logs)}");
    Console.WriteLine("PASS: missing BAT, working directory, spaces/Cyrillic/&, no arguments, stdout, stderr, exit code.");
}
finally
{
    // Only remove the three known fixture files, never recursively delete a computed path.
    File.Delete(Path.Combine(root, LauncherService.BatName));
    File.Delete(Path.Combine(root, "cwd-marker.txt"));
    File.Delete(Path.Combine(root, "history.json"));
    File.Delete(Path.Combine(root, "history.json.tmp"));
    File.Delete(Path.Combine(root, "game", "bin", "xrEngine.exe"));
    File.Delete(Path.Combine(root, "game", "bin", "dedicated", "xrEngine.exe"));
    File.Delete(Path.Combine(root, "game", "fsgame_sc.ltx"));
    File.Delete(Path.Combine(root, "game", "addons", "xrmpe_pvp.addon"));
    if (Directory.Exists(Path.Combine(root, "game", "addons"))) Directory.Delete(Path.Combine(root, "game", "addons"));
    if (Directory.Exists(Path.Combine(root, "game", "bin", "dedicated"))) Directory.Delete(Path.Combine(root, "game", "bin", "dedicated"));
    if (Directory.Exists(Path.Combine(root, "game", "bin"))) Directory.Delete(Path.Combine(root, "game", "bin"));
    if (Directory.Exists(Path.Combine(root, "game"))) Directory.Delete(Path.Combine(root, "game"));
    Directory.Delete(root);
}
