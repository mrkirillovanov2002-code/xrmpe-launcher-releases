namespace Xrmpe.Launcher;

public static class GameInstallation
{
    public static readonly string[] RequiredFiles =
    [
        Path.Combine("bin", "xrEngine.exe"),
        Path.Combine("bin", "dedicated", "xrEngine.exe"),
        "fsgame_sc.ltx"
    ];

    public static string Resolve(string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) throw new ArgumentException("Папка игры не выбрана. Нажмите «Выбрать папку игры».");
        var folder = Path.GetFullPath(selected.Trim().Trim('"'));
        var candidates = new List<string> { folder, Path.Combine(folder, "game") };
        // Also accept a manually selected bin or bin/dedicated directory.
        var parent = Directory.GetParent(folder);
        if (Path.GetFileName(folder).Equals("bin", StringComparison.OrdinalIgnoreCase) && parent is not null) candidates.Add(parent.FullName);
        if (Path.GetFileName(folder).Equals("dedicated", StringComparison.OrdinalIgnoreCase) && parent?.Parent is not null) candidates.Add(parent.Parent.FullName);
        var best = candidates.OrderByDescending(path => RequiredFiles.Count(file => File.Exists(Path.Combine(path, file)))).First();
        var missing = RequiredFiles.Where(file => !File.Exists(Path.Combine(best, file))).ToArray();
        if (missing.Length == 0) return best;
        throw new FileNotFoundException($"Игра не найдена или скопирована не полностью. Проверена папка: {best}. Не найдены: {string.Join(", ", missing)}. Нажмите «Выбрать папку игры» и укажите game или папку установки.");
    }

    public static string? FindNearby(string launcherDirectory)
    {
        // No dependency on the first PC, current working directory, or drive letter.
        // Inspect the EXE folder and its ancestors, never recursively scan disks.
        var folder = new DirectoryInfo(Path.GetFullPath(launcherDirectory));
        for (var depth = 0; folder is not null && depth < 12; depth++, folder = folder.Parent)
        {
            try { return Resolve(folder.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }
}

public sealed class GamePathSettings
{
    private readonly string _file;
    private readonly string _legacyFile;
    public GamePathSettings(string dataFolder, string machineName)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machineName)))[..16];
        _file = Path.Combine(dataFolder, "Machines", key, "game-path.txt");
        _legacyFile = Path.Combine(dataFolder, "game-path.txt");
    }
    public string? Load() => File.Exists(_file) ? File.ReadAllText(_file) : File.Exists(_legacyFile) ? File.ReadAllText(_legacyFile) : null;
    public void Save(string game)
    {
        game = GameInstallation.Resolve(game);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, game);
    }
}
