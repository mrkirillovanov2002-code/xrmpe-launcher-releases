using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher;

public sealed class LauncherService
{
    public bool ArmbandsEnabled { get; set; }
    public string ArmbandsPackage { get; set; } = "";
    public string ArmbandsVariant { get; set; } = "emissive";
    public string? ArmbandsApprovedHash { get; set; }
    private readonly ProcessRunner _server = new();
    public bool IsServerProcessRunning => _server.IsRunning;
    public Task StopServerAsync() => _server.StopAsync();
    public const string BatName = "Start_PvP_Addon.bat";
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    public const string DefaultAddon = "xrmpe_pvp.addon";
    public static string ValidateAddon(string game, string addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        if (!System.Text.RegularExpressions.Regex.IsMatch(addon, @"\A[a-z0-9][a-z0-9_-]{0,79}\.addon\z"))
            throw new ArgumentException("Имя аддона должно быть в нижнем регистре: латинские буквы, цифры, _ или -, затем .addon. Переименуйте собственный пакет вручную; лаунчер файлы не меняет.");
        var directory = Path.Combine(game, "addons");
        if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.addon").Any(x => Path.GetFileName(x).Equals(addon, StringComparison.Ordinal)))
            throw new FileNotFoundException($"Не найден аддон с точным именем {addon}. У всех участников должен быть одинаковый пакет в game/addons.");
        for (var path = Path.Combine(directory, addon); path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Аддон и его каталог не должны быть ссылками/junction.");
        return addon;
    }
    public static ProcessStartInfo CreateClientStartInfo(string folder, int port = 5446, string addon = DefaultAddon)
    {
        var game = GameInstallation.Resolve(folder);
        addon = ValidateAddon(game, addon);
        var engine = Path.Combine(game, "bin", "xrEngine.exe");
        if (!File.Exists(engine)) throw new FileNotFoundException("Не найден game\\bin\\xrEngine.exe. Выберите папку установки или её папку game.", engine);
        var start = new ProcessStartInfo
        {
            FileName = engine,
            WorkingDirectory = game,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-addon=" + addon);
        AddCampaignFlags(start, game, addon);
        start.ArgumentList.Add("-start");
        if (port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
        start.ArgumentList.Add($"client(localhost/port={port})");
        return start;
    }

    public Task<int> RunClientAsync(string folder, Action<string> log, int port = 5446, string addon = DefaultAddon) =>
        RunEngineAsync(CreateClientStartInfo(folder, port, addon), "PvP-клиент", log);

    private static void AddCampaignFlags(ProcessStartInfo start, string game, string addon) {
        // Preserve byte-for-byte existing PvP/control launch arguments. Other addon headers are bounded and read-only.
        if (addon is "xrmpe_pvp.addon" or "zonestrike.addon") return;
        var a = AddonCatalog.ReadLaunchCandidate(Path.Combine(game, "addons", addon));
        if (AddonScenarios.ClientReason(a) is { } reason) throw new InvalidOperationException(reason);
        foreach (var flag in a.StartupArgs ?? []) start.ArgumentList.Add(flag);
    }
    public static ProcessStartInfo CreateAddonServerStartInfo(string folder, AddonDescriptor addon, ServerProfile p, bool steam)
    {
        if(p.Mode=="dm") {var legacy=CreateServerStartInfo(folder,p.Map,p.Name,p.Players,p.Port,p.Kills,addon.FileName,p.Mode);return steam?EnableSteam(legacy):legacy;}
        var game=GameInstallation.Resolve(folder);ValidateAddon(game,addon.FileName);ServerProfileStore.Validate(p);
        if(AddonScenarios.ServerReason(addon,p,game,steam) is {} reason)throw new InvalidOperationException(reason);
        var exe=Path.Combine(game,"bin","dedicated","xrEngine.exe");if(!File.Exists(exe))throw new FileNotFoundException("Нет dedicated",exe);
        var start=new ProcessStartInfo(exe){WorkingDirectory=Path.Combine(game,"bin"),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var flag in new[]{"-i","-fsltx",@"..\fsgame_s.ltx"})start.ArgumentList.Add(flag);
        if(steam)start.ArgumentList.Add("-steam");start.ArgumentList.Add("-addon="+addon.FileName);
        foreach(var flag in addon.StartupArgs??[])start.ArgumentList.Add(flag);
        var level=p.Mode=="coop" && p.CoopSave.Length>0?p.CoopSave:p.Map;
        var code=p.Mode=="defence"?"df":"coop";
        var command=$"server({level}/{code}/hname={p.Name}/maxplayers={p.Players}/public={(steam?0:1)}/minplayers={p.MinPlayers}";
        if(p.Difficulty!="none")command+="/difficulty="+p.Difficulty;
        command+=$"/portsv={p.Port})";
        foreach(var arg in new[]{"-start",command,"client(localhost)"})start.ArgumentList.Add(arg);
        return start;
    }
    public Task<int> RunAddonServerAsync(string folder, AddonDescriptor addon, ServerProfile profile, bool steam, Action<string> log)
    {
        var start=CreateAddonServerStartInfo(folder,addon,profile,steam);
        if(profile.Mode!="dm") {
            var generated=AddonScenarios.PrepareServerData(GameInstallation.Resolve(folder),addon,profile,steam);
            var index=start.ArgumentList.IndexOf("-fsltx");start.ArgumentList[index+1]=@"..\"+Path.GetFileName(generated);
        }
        return steam?RunSteamEngineAsync(start,"Сервер "+profile.Mode,log,true):RunEngineAsync(start,"Сервер "+profile.Mode,log,true);
    }

    public static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0) throw new ArgumentException("Введите IP-адрес или домен сервера.");
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Пока поддерживается IPv4. IPv6 с этой сборкой xrMPE не проверен.");
            return ip.ToString();
        }
        string ascii;
        try { ascii = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("Некорректный домен сервера."); }
        if (ascii.Length > 253 || !ascii.Split('.').All(part => part.Length is > 0 and <= 63 &&
            System.Text.RegularExpressions.Regex.IsMatch(part, @"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z")))
            throw new ArgumentException("Введите только адрес, без http://, порта, пробелов и параметров запуска.");
        if (ascii.All(c => char.IsDigit(c) || c == '.')) throw new ArgumentException("Некорректный IPv4-адрес.");
        return ascii;
    }

    public static ProcessStartInfo CreateRemoteClientStartInfo(string folder, string host, int port, string addon = DefaultAddon)
    {
        host = NormalizeHost(host);
        var start = CreateClientStartInfo(folder, port, addon);
        start.ArgumentList[start.ArgumentList.IndexOf("-start") + 1] = $"client({host}/port={port})";
        return start;
    }

    public async Task<int> RunRemoteClientAsync(string folder, string host, int port, Action<string> log, string addon = DefaultAddon)
    {
        host = NormalizeHost(host);
        // The native library exposes ParseString for numeric addresses. Resolve DNS here
        // rather than assuming the engine accepts every DNS/IDN hostname itself.
        var start = CreateRemoteClientStartInfo(folder, host, port, addon);
        if (host != "localhost" && !IPAddress.TryParse(host, out _))
        {
            log($"DNS: {host}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException("У домена не найден IPv4-адрес (A-запись).");
            log($"DNS: {host} → {ip}");
            start = CreateRemoteClientStartInfo(folder, ip.ToString(), port, addon);
        }
        return await RunEngineAsync(start, "PvP-клиент IP", log);
    }

    public static ProcessStartInfo EnableSteam(ProcessStartInfo start)
    {
        start.ArgumentList.Add("-steam");
        return start;
    }

    public static ProcessStartInfo CreateMenuStartInfo(string folder,string addon=DefaultAddon) {
        var start=CreateClientStartInfo(folder,5446,addon);start.ArgumentList.Clear();start.ArgumentList.Add("-addon="+addon);AddCampaignFlags(start,folder,addon);return start;
    }
    public Task<int> RunMenuClientAsync(string folder,Action<string> log,string addon=DefaultAddon)=>RunEngineAsync(CreateMenuStartInfo(folder,addon),"Клиент",log);
    public static ProcessStartInfo CreateSteamMenuStartInfo(string folder, string addon = DefaultAddon)
    {
        var start = CreateClientStartInfo(folder, addon: addon);
        start.ArgumentList.Clear();
        start.ArgumentList.Add("-addon=" + addon);
        AddCampaignFlags(start, start.WorkingDirectory, addon);
        return EnableSteam(start);
    }

    public Task<int> RunSteamClientAsync(string folder, int? localPort, Action<string> log, string addon = DefaultAddon) =>
        RunSteamEngineAsync(localPort is int port ? EnableSteam(CreateClientStartInfo(folder, port, addon)) : CreateSteamMenuStartInfo(folder, addon), "PvP-клиент Steam", log);

    public Task<int> RunSteamServerAsync(string folder, string map, string name, int players, int port, int kills, Action<string> log, string addon = DefaultAddon, string mode = "dm") =>
        RunSteamEngineAsync(EnableSteam(CreateServerStartInfo(folder, map, name, players, port, kills, addon, mode)), "PvP-сервер Steam", log, server: true);

    private Task<int> RunSteamEngineAsync(ProcessStartInfo start, string label, Action<string> log, bool server = false)
    {
        var bin = Path.GetDirectoryName(start.FileName)!;
        if (Path.GetFileName(bin).Equals("dedicated", StringComparison.OrdinalIgnoreCase)) bin = Path.GetDirectoryName(bin)!;
        if (!File.Exists(Path.Combine(bin, "steam_api.dll"))) throw new FileNotFoundException("Не найден bin\\steam_api.dll.");
        var appId = Path.Combine(bin, "steam_appid.txt");
        if (!File.Exists(appId) || File.ReadAllText(appId).Trim() != "480")
            throw new InvalidOperationException("Экспериментальный режим проверяет существующий bin\\steam_appid.txt = 480. Файл не изменяется.");
        var processes = Process.GetProcessesByName("steam");
        try { if (processes.Length == 0) throw new InvalidOperationException("Сначала запустите Steam и войдите в свой аккаунт."); }
        finally { foreach (var process in processes) process.Dispose(); }
        log("Steam: экспериментальный запуск -steam. Приглашения обрабатывает движок; работа PvP с другом ещё не подтверждена.");
        return RunEngineAsync(start, label, log, server);
    }

    public static ProcessStartInfo CreateServerStartInfo(string folder, string map, string name, int players, int port, int kills, string addon = DefaultAddon, string mode = "dm")
    {
        map = map.Trim();
        name = name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(map, @"\A[a-zA-Z0-9_-]+\z"))
            throw new ArgumentException("Укажите идентификатор карты, например mp_kpp_2 (буквы, цифры, _ и -).");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(c => char.IsControl(c) || "/\\()\"".Contains(c)))
            throw new ArgumentException("Название сервера: 1–64 символа без /, \\, скобок, кавычек и переводов строк.");
        if (players is < 2 or > 64 || port is < 1 or > 65535 || kills is < 0 or > 10000)
            throw new ArgumentException("Проверьте количество игроков, порт и лимит убийств.");
        if (mode != "dm") throw new ArgumentException("Этот сценарий dedicated поддерживает только проверенный DM. TDM, кооператив и оборона требуют отдельных проверенных сценариев.");
        var start = CreateClientStartInfo(folder, addon: addon);
        var bin = Path.Combine(start.WorkingDirectory, "bin");
        start.FileName = Path.Combine(bin, "dedicated", "xrEngine.exe");
        start.WorkingDirectory = bin;
        if (!File.Exists(start.FileName)) throw new FileNotFoundException("Не найден game\\bin\\dedicated\\xrEngine.exe.", start.FileName);
        if (!File.Exists(Path.Combine(bin, "..", "fsgame_sc.ltx"))) throw new FileNotFoundException("Не найден game\\fsgame_sc.ltx.");
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-addon=" + addon, "-i", "-silent_error_mode", "-fsltx", @"..\fsgame_sc.ltx", "-start",
            $"server({map}/{mode}/timelimit=61/fraglimit={kills}/hname={name}/maxplayers={players}/public=1/portsv={port})", "client(localhost/portcl=5450)" })
            start.ArgumentList.Add(arg);
        if (port == 5450) throw new ArgumentException("Порт 5450 занят встроенным клиентом dedicated. Выберите другой порт сервера.");
        return start;
    }

    public Task<int> RunServerAsync(string folder, string map, string name, int players, int port, int kills, Action<string> log, string addon = DefaultAddon, string mode = "dm") =>
        RunEngineAsync(CreateServerStartInfo(folder, map, name, players, port, kills, addon, mode), "PvP-сервер", log, server: true);

    private Task<int> RunEngineAsync(ProcessStartInfo start, string label, Action<string> log, bool server = false)
    {
        if (!server && ArmbandsEnabled && start.ArgumentList.Contains("-addon=xrmpe_pvp.addon")) LocalArmbandsProfiles.Apply(start, ArmbandsPackage, ArmbandsVariant, ArmbandsApprovedHash, log);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Запуск клиента поддерживается только в Windows.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.GetEncoding((int)GetOEMCP());
        log($"{label}: {start.FileName}");
        log("Аргументы: " + string.Join(" ", start.ArgumentList));
        log($"Рабочая папка: {start.WorkingDirectory}");
        return (server ? _server : new ProcessRunner()).RunAsync(start, label, log);
    }

    public async Task<int> RunAsync(string folder, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Запуск BAT поддерживается только в Windows.");
        folder = Path.GetFullPath(folder.Trim());
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Папка игры не найдена.");
        var bat = Path.Combine(folder, BatName);
        if (!File.Exists(bat)) throw new FileNotFoundException($"В выбранной папке отсутствует {BatName}.", bat);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding((int)GetOEMCP());
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = "/d /s /c \"\"%XRMPE_LAUNCH_BAT%\"\"",
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        start.Environment["XRMPE_LAUNCH_BAT"] = bat;
        log($"BAT: {bat}");
        log($"Рабочая папка: {folder}");
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить cmd.exe.");
        // Do not leave an invisible BAT waiting for input (for example, PAUSE).
        process.StandardInput.Close();
        async Task ReadAsync(StreamReader reader, string prefix)
        {
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0) log(prefix + new string(buffer, 0, read));
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput, ""), ReadAsync(process.StandardError, "[stderr] "), process.WaitForExitAsync());
        return process.ExitCode;
    }
}
