using System.Text.Json;

namespace Xrmpe.Launcher.Content;

public sealed record MapChoice(string Id, string Name, string Mode, string Source, bool LaunchVerified, string Status)
{
    public override string ToString() => $"{Name} · {(LaunchVerified ? "проверенный сценарий" : Status.StartsWith("Dedicated") ? "dedicated проверен, клиент не подтверждён" : Source.StartsWith("metadata.json") ? "заявлена автором" : "только ресурс")}";
}
public sealed record ModeChoice(string Code)
{
    public override string ToString() => Code switch { "dm" => "Смертельный бой · PvP", "coop" => "Кооператив · сюжетная кампания", "defence" => "Оборона · волны противников", _ => "Неизвестный режим: " + Code };
}
public static class ContentPolicy
{
    // Existing 39-map set accepted by the project owner after in-game checks. Not archive discovery.
    public static readonly HashSet<string> AcceptedPvpMaps = new("mp_agroprom mp_arena mp_atp mp_autostation mp_bath mp_chapel mp_close_combat mp_darkfabric mp_darkvalley mp_derevnya mp_dolina_ferma mp_factory mp_firestation mp_forest_arena mp_garbage mp_hospital mp_kordon_ferma mp_kpp_2 mp_lesnichestvo mp_lost_village mp_military1 mp_military_2 mp_military_full mp_neptun mp_new_bridge mp_pool mp_pripyat mp_radar mp_railroad mp_rostok mp_sport_center mp_svalka mp_training_camp mp_village mp_volhov mp_volna_cop mp_vostok mp_workshop mp_zaton".Split(' '), StringComparer.Ordinal);
    public static bool VerifiedAddon(AddonDescriptor? addon) => addon is { Healthy: true, FileName: "xrmpe_pvp.addon", Version: "1.4.7" } && addon.Modes.Contains("dm");
    public const string LocalControlSha256 = "4F9E0BD5BCE10B438282ABEF3E2295ABF3073AAA355EA62A0E38D39DA3A79020";
    public static bool LocalControlAddon(AddonDescriptor? addon) => addon is { Healthy: true, FileName: "zonestrike.addon", Version: "local-control-1", PackageSha256: LocalControlSha256 } && addon.Modes.SequenceEqual(new[] { "dm" });
    public static bool CanLaunch(AddonDescriptor? addon, bool steam) => addon?.FileName == "zonestrike.addon" ? !steam && LocalControlAddon(addon) : AddonScenarios.ClientReason(addon) is null;
    public static IReadOnlyList<MapChoice> Maps(AddonDescriptor? addon, string mode, string game)
    {
        if (addon is null || !addon.Healthy) return [];
        var rows = new List<MapChoice>();
        if (LocalControlAddon(addon) && mode == "dm")
        {
            bool exists = false;
            try { var path = Path.Combine(game, "mp", "mp_agroprom.db"); ContentPaths.NoLinks(path); exists = File.Exists(path) && new FileInfo(path).Length > 0; } catch (IOException) { }
            rows.Add(new("mp_agroprom", "mp_agroprom", "dm", "Локальная проверка двух клиентов с контрольным пакетом ZoneStrike", exists,
                exists ? "Локальный DM подтверждён; Steam и произвольные изменения пакета не проверены" : "Не найден ресурс карты"));
            return rows;
        }
        if (addon.FileName.Equals("xrmpe_pvp.addon", StringComparison.OrdinalIgnoreCase) && mode == "dm")
        {
            var resource = Path.Combine(game, "mp", "mp_kpp_2.db"); bool present = false;
            try { ContentPaths.NoLinks(resource); present = File.Exists(resource) && new FileInfo(resource).Length > 0; } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            rows.Add(new("mp_kpp_2", "mp_kpp_2", "dm", "Ранее проверенный PvP-сценарий + ресурс mp/mp_kpp_2.db",
                VerifiedAddon(addon) && present, !present ? "Не найден ресурс карты" : VerifiedAddon(addon) ? "Проверенный сценарий; целостность DB не проверена" : "Версия PvP ещё не проверена"));
        }
        rows.AddRange(addon.Maps.Where(m => m.Modes.Contains(mode == "defence" ? "df" : mode))
            .Select(m => new MapChoice(m.Id, m.Name, mode, "metadata.json → availableMaps", false, "Заявлена автором; запуск не проверен")));
        if (mode == "coop" && addon.StartMap is { } start) rows.Add(new(start, start, mode, "metadata.json → coopStartMap", false, "Стартовая карта кампании; запуск не проверен"));
        // Resource discovery is separate evidence, never permission to launch a map.
        if ((mode == "dm" && addon.FileName == "xrmpe_pvp.addon") || (mode == "defence" && addon.Modes.Contains("defence")))
        {
            var folder = Path.Combine(game, "mp");
            try
            {
                ContentPaths.NoLinks(folder);
                if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, mode == "dm" ? "mp_*.db" : "df_*.db").Take(128).Order())
                {
                    var id = Path.GetFileNameWithoutExtension(file); if (!AddonCatalog.ValidId(id)) continue;
                    ContentPaths.NoLinks(file);
                    if (new FileInfo(file).Length == 0) continue;
                    if (rows.Any(x => x.Id == id)) continue;
                    // Measured on the installed PvP 1.4.7 physical test copy. Never promote from QR2 alone.
                    var serverOnly = VerifiedAddon(addon) && mode == "dm" && id is "mp_agroprom" or "mp_arena";
                    rows.Add(new(id, id, mode, "Найден ресурс mp/" + Path.GetFileName(file) + "; внутренняя конфигурация режима не декодирована", false,
                        serverOnly ? "Dedicated ответил по QR2 в изолированной проверке; загрузка клиентского уровня не подтверждена, запуск заблокирован" : "Только ресурс; конфигурация и запуск не проверены"));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return rows.DistinctBy(x => x.Id).Select(x => VerifiedAddon(addon) && mode == "dm" && AcceptedPvpMaps.Contains(x.Id)
            && File.Exists(Path.Combine(game, "mp", x.Id + ".db")) && new FileInfo(Path.Combine(game, "mp", x.Id + ".db")).Length > 0
            ? x with { LaunchVerified = true, Source = "Согласованный список PvP-карт 1.4.7", Status = "Игровые проверки подтверждены владельцем проекта; файл карты найден" } : x)
            .OrderByDescending(x => x.LaunchVerified).ThenBy(x => x.Id).ToArray();
    }
}
public sealed record ServerProfile(string Addon = "xrmpe_pvp.addon", string Mode = "dm", string Map = "mp_kpp_2",
    string Name = "xrmpe_pvp_addon", int Players = 32, int Port = 5446, int Kills = 60, int QueryPort = 5445, string CoopSave = "", bool OfficialSave = false, string Difficulty = "none", int MinPlayers = 1, string[]? Rotation = null);
public sealed record ProfileDocument(int Schema, Dictionary<string, ServerProfile> Installations);
public sealed class ServerProfileStore(string file)
{
    public string? RecoveryMessage { get; private set; }
    static string Key(string game) => Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    public static ServerProfile Validate(ServerProfile p)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.Addon) || p.Mode is null || p.Map is null || p.Addon.Length > 180 || !p.Addon.EndsWith(".addon", StringComparison.OrdinalIgnoreCase) || p.Addon.IndexOfAny(['/', '\\', ':', '"']) >= 0 || p.Addon.Contains("..") || p.Addon.Any(char.IsControl) ||
            !AddonCatalog.ValidId(p.Mode) || !AddonCatalog.ValidId(p.Map) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64 || p.Name.Any(c => char.IsControl(c) || "/\\()\"".Contains(c)) ||
            p.Players is < 2 or > 64 || p.Port is < 1 or > 65535 or 5450 || p.QueryPort is < 1 or > 65535 || p.Kills is < 0 or > 10000)
            throw new InvalidDataException("Неверные параметры серверного профиля.");
        if (!AddonScenarios.ValidSaveName(p.CoopSave, true) || p.Difficulty is not ("none" or "gd_novice" or "gd_stalker" or "gd_veteran" or "gd_master") || p.MinPlayers < 1 || p.MinPlayers > p.Players || p.Rotation is { Length: > 32 } || p.Rotation?.Any(x => !AddonCatalog.ValidId(x)) == true) throw new InvalidDataException("Неверные параметры кампании/обороны.");
        return p;
    }
    ProfileDocument Read(string path)
    {
        ContentPaths.NoLinks(path);
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Файл профилей слишком большой.");
        var data = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустые профили.");
        if (data.Schema != 1 || data.Installations is null || data.Installations.Count > 64) throw new InvalidDataException("Неверная схема профилей.");
        foreach (var item in data.Installations) { if (item.Key.Length > 4096 || item.Value is null) throw new InvalidDataException("Неверный профиль."); Validate(item.Value); }
        return data;
    }
    ProfileDocument LoadDocument()
    {
        RecoveryMessage = null;
        try { if (File.Exists(file)) return Read(file); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NullReferenceException)
        { RecoveryMessage = "Основной файл профилей повреждён: " + ex.Message; }
        if (File.Exists(file + ".bak"))
        {
            try { var data = Read(file + ".bak"); RecoveryMessage = "Профили восстановлены из резервной копии; повреждённый файл не удалён."; return data; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NullReferenceException) { RecoveryMessage = "Не удалось прочитать профили и резервную копию. Исходные файлы сохранены."; }
        }
        return new(1, new());
    }
    public ServerProfile Load(string game) => LoadDocument().Installations.GetValueOrDefault(Key(game)) ?? new();
    public ServerProfile? LoadForAddon(string game, string addon) => LoadDocument().Installations.GetValueOrDefault(Key(game) + "|" + addon.ToUpperInvariant()) ?? (Load(game).Addon == addon ? Load(game) : null);
    public void Save(string game, ServerProfile profile)
    {
        Validate(profile); ContentPaths.NoLinks(file); ContentPaths.NoLinks(file + ".bak"); ContentPaths.NoLinks(file + ".tmp");
        var data = LoadDocument(); var addonKey = Key(game) + "|" + profile.Addon.ToUpperInvariant();
        if (data.Installations.Count + new[]{Key(game), addonKey}.Count(k => !data.Installations.ContainsKey(k)) > 64) throw new IOException("Достигнут лимит профилей; прежние файлы сохранены.");
        data.Installations[Key(game)] = profile; data.Installations[addonKey] = profile; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (!File.Exists(file)) { File.Move(file + ".tmp", file); return; }
        bool valid = false; try { Read(file); valid = true; } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or NullReferenceException) { }
        if (valid) File.Replace(file + ".tmp", file, file + ".bak");
        else { File.Copy(file, file + ".corrupt-" + Guid.NewGuid().ToString("N"), false); File.Move(file + ".tmp", file, true); }
    }
}
