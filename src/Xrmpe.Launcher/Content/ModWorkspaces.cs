using System.Text.Json;

namespace Xrmpe.Launcher.Content;

// Per-addon notes and local Detroit preference. Installation/mounting lives in the workshop services.
public sealed record ModWorkspace(string Notes, string[] PlannedModIds, bool ArmbandsEnabled = false, string ArmbandsPreviewId = "", string ArmbandsVariant = "dds", string ArmbandsApprovedHash = "", string ArmbandsApprovalBuilderHash = "")
{
    public static ModWorkspace Empty => new("", []);
}
public sealed record ModWorkspaceDocument(int Schema, Dictionary<string, ModWorkspace> Profiles);
public sealed class ModWorkspaceStore(string file)
{
    public string? Warning { get; private set; }
    static string Key(string game, string addon)
    {
        ServerProfileStore.Validate(new ServerProfile(Addon: addon));
        return Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + "|" + addon.ToUpperInvariant();
    }
    static void Validate(ModWorkspace profile)
    {
        if (profile is null || profile.Notes is null || profile.Notes.Length > 2000 || profile.PlannedModIds is null || profile.PlannedModIds.Length > 128 ||
            profile.PlannedModIds.Any(id => id is null || !AddonCatalog.ValidId(id)) ||
            profile.ArmbandsPreviewId is null || profile.ArmbandsPreviewId.Length > 80 ||
            profile.ArmbandsPreviewId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            profile.ArmbandsVariant is not ("dds" or "emissive") || profile.ArmbandsApprovedHash is null ||
            (profile.ArmbandsApprovedHash.Length != 0 && (profile.ArmbandsApprovedHash.Length != 64 || !profile.ArmbandsApprovedHash.All(Uri.IsHexDigit))) ||
            profile.ArmbandsApprovalBuilderHash is null ||
            (profile.ArmbandsApprovalBuilderHash.Length != 0 && (profile.ArmbandsApprovalBuilderHash.Length != 64 || !profile.ArmbandsApprovalBuilderHash.All(Uri.IsHexDigit))))
            throw new InvalidDataException("Повреждён план модификаций.");
    }
    ModWorkspaceDocument Read(string path)
    {
        ContentPaths.NoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл профилей модов слишком большой.");
        var data = JsonSerializer.Deserialize<ModWorkspaceDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("Нет профилей.");
        if (data.Schema != 1 || data.Profiles is null || data.Profiles.Count > 512) throw new InvalidDataException("Неверная схема профилей.");
        foreach (var pair in data.Profiles) { if (pair.Key.Length > 4400) throw new InvalidDataException("Неверный ключ."); Validate(pair.Value); }
        return data;
    }
    ModWorkspaceDocument LoadDocument()
    {
        Warning = null;
        foreach (var path in new[] { file, file + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try { var result = Read(path); if (path.EndsWith(".bak")) Warning = "План восстановлен из резервной копии; исходный файл сохранён."; return result; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
            { Warning = "Профили модов не прочитаны: " + ex.Message; }
        }
        return new(1, new());
    }
    public ModWorkspace Load(string game, string addon) => LoadDocument().Profiles.GetValueOrDefault(Key(game, addon)) ?? ModWorkspace.Empty;
    public void Save(string game, string addon, ModWorkspace profile)
    {
        Validate(profile); var key = Key(game, addon); var data = LoadDocument();
        if (data.Profiles.Count >= 512 && !data.Profiles.ContainsKey(key)) throw new IOException("Достигнут лимит профилей.");
        data.Profiles[key] = profile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        foreach (var path in new[] { file, file + ".bak", file + ".tmp" }) ContentPaths.NoLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 1024 * 1024) throw new IOException("Профили превышают лимит размера; прежний файл сохранён.");
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        if (!File.Exists(file)) { File.Move(file + ".tmp", file); return; }
        bool valid = false; try { Read(file); valid = true; } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { }
        if (valid) File.Replace(file + ".tmp", file, file + ".bak");
        else { File.Copy(file, file + ".corrupt-" + Guid.NewGuid().ToString("N")); File.Move(file + ".tmp", file, true); }
    }
}
