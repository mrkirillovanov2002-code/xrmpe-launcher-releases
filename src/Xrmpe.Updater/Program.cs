using System.Diagnostics;
using System.Text.Json;
using Xrmpe.SelfUpdate;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher", "self-update-result.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
void Log(string message) { Console.WriteLine(message); try { File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
void SaveResult(InstallResult result) { try { File.WriteAllText(Path.ChangeExtension(logPath, ".json"), JsonSerializer.Serialize(result, UpdateJson.Default.InstallResult)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
Process? started = null;
Installer? installer = null;
InstallJob? job = null;
try
{
    if (args.Length != 1) throw new ArgumentException("Нужен локальный файл задания updater.");
    ReleasePolicy.NoLinks(args[0]);
    job = JsonSerializer.Deserialize(await File.ReadAllTextAsync(args[0]), UpdateJson.Default.InstallJob) ?? throw new InvalidDataException("Пустое задание.");
    job = job with { Target = Path.GetFullPath(job.Target), Stage = Path.GetFullPath(job.Stage) };
    if (!File.Exists(ReleasePolicy.SafePath(job.Target, ReleasePolicy.Executable))) throw new IOException("В выбранном каталоге нет старого Xrmpe.Launcher.exe.");
    var release = ReleasePolicy.Read(job.Stage);
    if (job.RequireSignature)
    {
        await ReleaseTrust.VerifyStage(job.Stage);
        var installedVersion = System.Reflection.AssemblyName.GetAssemblyName(ReleasePolicy.SafePath(job.Target, "Xrmpe.Launcher.dll")).Version!;
        if (Version.Parse(release.Version) <= installedVersion) throw new InvalidDataException("Подписанное обновление должно быть новее установленной версии.");
    }
    await ReleasePolicy.Verify(job.Stage, release);
    ReleasePolicy.EnsureWritable(job.Target);
    Process? parent = null;
    if (job.ParentPid > 0)
    {
        try { parent = Process.GetProcessById(job.ParentPid); }
        catch (ArgumentException) { }
        if (parent is not null && parent.StartTime.ToUniversalTime().Ticks != job.ParentStartUtcTicks) { parent.Dispose(); parent = null; }
    }
    await File.WriteAllTextAsync(Path.Combine(job.Stage, "updater.ready"), "ready");
    if (parent is not null)
    {
        using (parent)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90))) await parent.WaitForExitAsync(timeout.Token);
    }
    // No replacement occurs until the exact old process has exited.
    foreach (var process in Process.GetProcessesByName("Xrmpe.Launcher"))
    {
        using (process)
        {
            if (string.Equals(process.MainModule?.FileName, Path.Combine(job.Target, ReleasePolicy.Executable), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ещё одна копия лаунчера работает из этого каталога. Закройте её и повторите обновление.");
        }
    }
    if (job.RequireSignature) await ReleaseTrust.VerifyStage(job.Stage);
    installer = new Installer(job.Target);
    await installer.Apply(job.Stage, release);
    if (job.Restart)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var health = Path.Combine(job.Stage, "health-" + nonce);
        var start = new ProcessStartInfo(Path.Combine(job.Target, ReleasePolicy.Executable)) { UseShellExecute = true, WorkingDirectory = job.Target };
        start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(health);
        started = Process.Start(start) ?? throw new IOException("Не удалось запустить новую версию.");
        var timer = Stopwatch.StartNew();
        while (!File.Exists(health))
        {
            if (started.HasExited || timer.Elapsed > TimeSpan.FromSeconds(40)) throw new IOException("Новая версия не подтвердила открытие окна.");
            await Task.Delay(100);
        }
    }
    installer.Commit();
    SaveResult(new InstallResult(true, job.Target, "Установлена версия " + release.Version));
    Log("УСПЕХ: установлен лаунчер " + release.Version + "; новая версия запущена: " + job.Restart);
    return 0;
}
catch (Exception ex)
{
    if (started is not null && !started.HasExited) { started.Kill(); await started.WaitForExitAsync(); }
    bool restored = true;
    try { installer?.Rollback(); }
    catch (Exception restore) { restored = false; Log("ОТКАТ НЕ ЗАВЕРШЁН: " + restore.Message + ". Резервная копия .xrmpe-launcher-rollback сохранена."); }
    Log("ОШИБКА: " + ex.Message + ". Старые файлы сохранены/восстановлены, если транзакция начиналась.");
    if (job is not null) SaveResult(new InstallResult(false, job.Target, ex.Message));
    try { if (job is not null) await File.WriteAllTextAsync(Path.Combine(job.Stage, "updater.error"), ex.Message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    if (restored && installer is not null && job is { Restart: true })
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(job.Target, ReleasePolicy.Executable)) { WorkingDirectory = job.Target, UseShellExecute = true }); }
        catch (Exception restart) { Log("Запустите старую версию вручную: " + restart.Message); }
    }
    return 1;
}
finally { installer?.Dispose(); started?.Dispose(); }
