using System.Text.Json;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher.Presentation;

public sealed record InterfacePreferences(int Schema = 1, bool DeveloperMode = false, bool DeveloperWarningAccepted = false,
    int Animations = 1, int? NetworkChoice = null, bool AdvancedIp = false);
public sealed class InterfacePreferencesStore(string path)
{
    public string? Warning { get; private set; }
    public static InterfacePreferences Validate(InterfacePreferences value)
    {
        if (value.Schema != 1) throw new InvalidDataException("Неизвестная схема настроек интерфейса.");
        if (value.Animations is < 0 or > 2 || value.NetworkChoice is < 0 or > 1) throw new InvalidDataException("Недопустимые настройки интерфейса.");
        return value.DeveloperWarningAccepted ? value : value with { DeveloperMode = false };
    }
    InterfacePreferences Read(string file)
    {
        ContentPaths.NoLinks(file);
        if (new FileInfo(file).Length > 4096) throw new InvalidDataException("Слишком большой файл настроек интерфейса.");
        return Validate(JsonSerializer.Deserialize<InterfacePreferences>(File.ReadAllText(file)) ?? throw new InvalidDataException("Пустой файл настроек интерфейса."));
    }
    public InterfacePreferences Load()
    {
        Warning = null;
        foreach (var file in new[] { path, path + ".bak" })
        {
            if (!File.Exists(file)) continue;
            try { var result = Read(file); if (file.EndsWith(".bak")) Warning = "Настройки интерфейса восстановлены из резервной копии."; return result; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { Warning = "Настройки интерфейса не прочитаны; исходный файл сохранён."; }
        }
        return new();
    }
    public void Save(InterfacePreferences value)
    {
        value = Validate(value);
        foreach (var file in new[] { path, path + ".tmp", path + ".bak" }) ContentPaths.NoLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        if (!File.Exists(path)) { File.Move(path + ".tmp", path); return; }
        bool valid = false; try { Read(path); valid = true; } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { }
        if (valid) File.Replace(path + ".tmp", path, path + ".bak");
        else { File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); File.Move(path + ".tmp", path, true); }
    }
}
