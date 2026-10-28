using System.Text.RegularExpressions;

namespace Xrmpe.Launcher.Presentation;

public sealed record DiagnosticFacts(string Version, bool Developer, bool PrimaryConfigured, bool BackupConfigured,
    bool Publish, bool ClientRunning, bool ServerRunning, bool UpdatesBusy, bool SearchRunning);
public static class DiagnosticPrivacy
{
    public static string RemoveSecrets(string text, string? knownToken = null)
    {
        if (!string.IsNullOrEmpty(knownToken) && knownToken.Length >= 16) text = text.Replace(knownToken, "[секрет скрыт]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [скрыто]");
        text = Regex.Replace(text, "(?i)((?:token|password|passwd|psw|secret|authorization|токен|пароль)\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s/&()]+)", "$1[скрыто]");
        return Regex.Replace(text, @"(?i)(https?://)[^\s/@]+:[^\s/@]+@", "$1[учётные данные скрыты]@");
    }
    public static string ForPlayer(string text)
    {
        var lines = new List<string>();
        foreach (var line in RemoveSecrets(text).Split('\n'))
        {
            bool technical = Regex.IsMatch(line, @"(?i)Аргументы:|Рабочая папка:|heartbeat|\bTTL\b");
            if (technical && !Regex.IsMatch(line, @"(?i)ошиб|отказ|не удалось|exception|fail")) continue;
            var safe = Regex.Replace(line, @"(?i)https?://[^\s]+", "[адрес сервиса]");
            safe = Regex.Replace(safe, @"(?i)[a-z]:[\\/][^\r\n]*", "[локальный путь скрыт]");
            lines.Add(safe);
        }
        return string.Join("\n", lines);
    }
    // Allowlisted structured export, never raw logs or user-provided free text.
    public static string Export(DiagnosticFacts facts)
    {
        var version = Regex.IsMatch(facts.Version, @"\A\d+\.\d+\.\d+(?:\.\d+)?\z") ? facts.Version : "не определена";
        string Yes(bool value) => value ? "да" : "нет";
        return $"xrMPE Launcher {version}\nРежим разработчика: {Yes(facts.Developer)}\nОсновной каталог настроен: {Yes(facts.PrimaryConfigured)}\nРезервный каталог настроен: {Yes(facts.BackupConfigured)}\nПубликация разрешена: {Yes(facts.Publish)}\nКлиент работает: {Yes(facts.ClientRunning)}\nDedicated работает: {Yes(facts.ServerRunning)}\nОбновление/раздача активны: {Yes(facts.UpdatesBusy)}\nПоиск серверов активен: {Yes(facts.SearchRunning)}\n\nАдреса, локальные пути, токены, пароли, идентификаторы игроков и содержимое журналов не включены.\n";
    }
}
