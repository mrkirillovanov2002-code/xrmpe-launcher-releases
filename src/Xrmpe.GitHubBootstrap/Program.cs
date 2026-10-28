using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xrmpe.SelfUpdate;

Console.InputEncoding = Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    Console.WriteLine("xrMPE — однократное подключение GitHub-обновлений. Игра и лаунчер должны быть закрыты.");
    var target = args.Length > 0 ? args[0] : Ask("Полный путь к СУЩЕСТВУЮЩЕЙ папке с Xrmpe.Launcher.exe (без кавычек): ");
    target = Path.GetFullPath(target.Trim().Trim('"'));
    ReleasePolicy.EnsureWritable(target);
    ReleasePolicy.SafePath(target, ReleasePolicy.Executable);
    if (!File.Exists(Path.Combine(target, ReleasePolicy.Executable))) throw new FileNotFoundException("В выбранной папке нет лаунчера.");
    var installed = AssemblyName.GetAssemblyName(ReleasePolicy.SafePath(target, "Xrmpe.Launcher.dll")).Version!;
    foreach (var process in Process.GetProcessesByName("Xrmpe.Launcher"))
        using (process) if (string.Equals(process.MainModule?.FileName, Path.Combine(target, ReleasePolicy.Executable), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Закройте лаунчер из этой папки и повторите запуск.");
    var engines = Process.GetProcessesByName("xrEngine");
    try { if (engines.Length != 0) throw new IOException("Закройте игру и dedicated перед обновлением."); }
    finally { foreach (var engine in engines) engine.Dispose(); }
    var repository = ReleaseTrust.Repository(args.Length > 1 ? args[1] : Ask("Публичный GitHub-репозиторий (владелец/репозиторий): "));
    if (!ReleaseTrust.Configured(repository)) throw new ArgumentException("Вместо OWNER/REPOSITORY нужен настоящий публичный репозиторий. Публикация ещё не настроена.");
    using var client = new GitHubReleaseClient();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var offer = await client.Check(repository, stop.Token);
    if (Version.Parse(offer.Manifest.Release.Version) <= installed) { Console.WriteLine("Новая версия отсутствует."); return 0; }
    Console.WriteLine($"Версия {installed} → {offer.Manifest.Release.Version}, {offer.Manifest.Archive.Size / 1048576d:F1} МиБ. Подпись проверена.\n{offer.Manifest.Notes}");
    if (!Ask("Обновить в этой же папке? Введите ДА: ").Equals("ДА", StringComparison.OrdinalIgnoreCase)) return 0;
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher");
    var stage = Path.Combine(data, "SelfUpdate", Guid.NewGuid().ToString("N"));
    var last = Stopwatch.StartNew();
    await client.Download(offer, Path.Combine(data, "GitHubDownloads"), stage, (done, total, speed) =>
    { if (last.ElapsedMilliseconds >= 500) { Console.WriteLine($"{100d * done / total:F1}% · {speed / 1048576d:F1} МиБ/с"); last.Restart(); } }, stop.Token);
    await ReleaseTrust.VerifyStage(stage, stop.Token);
    // This executable was downloaded only after verifying the publisher's embedded trust key and every file hash.
    var helper = Path.Combine(stage, "updater-helper.exe");
    File.Copy(ReleasePolicy.SafePath(stage, "Xrmpe.Updater.exe"), helper);
    var job = Path.Combine(stage, "job.json");
    await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new InstallJob(target, stage, 0, 0, RequireSignature: true), UpdateJson.Default.InstallJob));
    var preferencesPath = Path.Combine(data, "launcher-internet.json");
    ReleasePolicy.NoLinks(preferencesPath); ReleasePolicy.NoLinks(preferencesPath + ".tmp");
    if (File.Exists(preferencesPath) && new FileInfo(preferencesPath).Length > 8192) throw new InvalidDataException("Настройки GitHub слишком велики; исходный файл сохранён.");
    var previousPreferences = File.Exists(preferencesPath) ? await File.ReadAllBytesAsync(preferencesPath) : null;
    await File.WriteAllTextAsync(preferencesPath + ".tmp", JsonSerializer.Serialize(new GitHubChannelPreferences(repository, 0), UpdateJson.Default.GitHubChannelPreferences));
    File.Move(preferencesPath + ".tmp", preferencesPath, true);
    var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = stage };
    start.ArgumentList.Add(job);
    var exitCode = 1;
    try
    {
        using var updater = Process.Start(start) ?? throw new IOException("Не удалось запустить проверенный Updater.");
        await updater.WaitForExitAsync();
        exitCode = updater.ExitCode;
    }
    finally
    {
        if (exitCode != 0)
        {
            if (previousPreferences is null) File.Delete(preferencesPath);
            else { await File.WriteAllBytesAsync(preferencesPath + ".tmp", previousPreferences); File.Move(preferencesPath + ".tmp", preferencesPath, true); }
        }
    }
    Console.WriteLine(exitCode == 0 ? "Обновление завершено. Ярлыки и папка установки сохранены." : "Обновление не установлено. См. журнал self-update-result.log; старая версия сохранена или восстановлена.");
    return exitCode;
}
catch (Exception ex) { Console.Error.WriteLine("Ошибка: " + ex.Message); return 1; }
static string Ask(string prompt) { Console.Write(prompt); return Console.ReadLine() ?? ""; }
