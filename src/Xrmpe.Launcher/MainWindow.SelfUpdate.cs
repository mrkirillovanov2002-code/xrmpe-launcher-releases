using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Xrmpe.SelfUpdate;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private void ShowPreviousSelfUpdateError()
    {
        try
        {
            var path = Path.Combine(_dataFolder, "self-update-result.json");
            if (!File.Exists(path)) return;
            var result = JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJson.Default.InstallResult);
            if (result is not null && string.Equals(Path.GetFullPath(result.Target).TrimEnd(Path.DirectorySeparatorChar), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                LauncherUpdateStatus.Text = (result.Success ? "Обновление выполнено: " : "Обновление лаунчера не установлено: ") + result.Message + ". Подробности в self-update-result.log.";
                AppendLog(LauncherUpdateStatus.Text);
            }
        }
        catch (Exception ex) { AppendLog("Не удалось прочитать результат updater: " + ex.Message); }
    }
    private LauncherRelease? _launcherRelease;
    private string? _launcherAddress, _launcherToken;
    private static Version CurrentLauncherVersion => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private async void BrowseLauncherPackage(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка новой публикации лаунчера (с launcher-release.json)", AllowMultiple = false });
            if (folders.Count > 0) LauncherPackagePath.Text = folders[0].TryGetLocalPath();
        }
        catch (Exception ex) { LauncherUpdateStatus.Text = ex.Message; }
    }

    private async void CheckLauncherUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null) return;
        if (GitHubSelected) { SaveInternetPreferences(); await CheckGitHubUpdate(false); return; }
        _launcherRelease = null;
        try
        {
            var server = UpdateServerAddress(); var token = UpdateToken.Text ?? "";
            LauncherUpdateStatus.Text = "Проверка версии лаунчера…";
            bool checkedVersion = false;
            await UpdateOperation(async ct =>
            {
                using var client = new LauncherUpdateClient(server, token);
                var release = await client.Check(ct);
                checkedVersion = true;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Version.Parse(release.Version) <= CurrentLauncherVersion)
                        LauncherUpdateStatus.Text = $"Лаунчер актуален ({CurrentLauncherVersion.ToString(3)}). На хосте: {release.Version}.";
                    else
                    {
                        _launcherRelease = release; _launcherAddress = server.AbsoluteUri; _launcherToken = token;
                        LauncherUpdateStatus.Text = $"Доступна новая версия лаунчера: {release.Version} · {release.Files.Sum(f => f.Size) / 1048576d:F1} МиБ. Нажмите «Обновить лаунчер» для загрузки и перезапуска.";
                    }
                });
            });
            if (!checkedVersion) LauncherUpdateStatus.Text = UpdateStatus.Text;
        }
        catch (Exception ex) { LauncherUpdateStatus.Text = ex.Message; AppendLog(ex.Message); }
    }

    private async void ApplyLauncherUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null || _running || _serverRunning || (GitHubSelected ? _githubOffer is null : _launcherRelease is null)) return;
        bool ready = false;
        UpdateStatus.Text = "Подготовка загрузки лаунчера…";
        LauncherTransfer.IsVisible = true;
        try
        {
            var internet = GitHubSelected;
            var offer = _githubOffer;
            var server = internet ? null : UpdateServerAddress(); var token = UpdateToken.Text ?? "";
            if (!internet && (server!.AbsoluteUri != _launcherAddress || token != _launcherToken)) throw new InvalidOperationException("Адрес или токен изменились: повторите проверку версии лаунчера.");
            if (internet && (offer is null || ReleaseTrust.Repository(GitHubRepository.Text ?? "") != offer.Manifest.Repository)) throw new InvalidOperationException("Репозиторий изменился: повторите проверку версии.");
            var release = internet ? offer!.Manifest.Release : _launcherRelease!;
            await UpdateOperation(async ct =>
            {
                Updates.UpdateFiles.EnsureIdle();
                var target = AppContext.BaseDirectory;
                ReleasePolicy.EnsureWritable(target);
                var helperSource = Path.Combine(target, "Xrmpe.Updater.exe");
                if (!File.Exists(helperSource)) throw new FileNotFoundException("Нет Xrmpe.Updater.exe. Выполните однократное обновление из комплекта Bootstrap.");
                var stage = Path.Combine(_dataFolder, "SelfUpdate", Guid.NewGuid().ToString("N"));
                if (internet)
                {
                    using var client = new GitHubReleaseClient();
                    await client.Download(offer!, Path.Combine(_dataFolder, "GitHubDownloads"), stage,
                        (done, total, speed) => ReportUpdate(new Updates.UpdateProgress("Лаунчер GitHub: загрузка / подпись / SHA-256", done, total, speed)), ct);
                    await ReleaseTrust.VerifyStage(stage, ct);
                }
                else
                {
                    using var client = new LauncherUpdateClient(server!, token);
                    await client.Download(release, stage, (done, total, speed) => ReportUpdate(new Updates.UpdateProgress("Лаунчер LAN: загрузка и SHA-256", done, total, speed)), ct);
                }
                Updates.UpdateFiles.EnsureIdle();
                var helper = Path.Combine(stage, "updater-helper.exe");
                File.Copy(helperSource, helper);
                using var current = Process.GetCurrentProcess();
                var jobPath = Path.Combine(stage, "job.json");
                await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(new InstallJob(target, stage, current.Id, current.StartTime.ToUniversalTime().Ticks, RequireSignature: internet)), ct);
                var info = new ProcessStartInfo(helper) { WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(jobPath);
                using var updater = Process.Start(info) ?? throw new IOException("Updater не запущен.");
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(stage, "updater.ready")))
                {
                    if (updater.HasExited || clock.Elapsed > TimeSpan.FromSeconds(60)) throw new IOException("Updater не готов. Старая версия продолжает работу. Журнал: " + Path.Combine(_dataFolder, "self-update-result.log"));
                    await Task.Delay(100, ct);
                }
                ready = true;
                await Dispatcher.UIThread.InvokeAsync(() => LauncherUpdateStatus.Text = "SHA-256 проверены. Закрытие лаунчера для безопасной замены…");
            });
            if (ready) Close();
            else LauncherUpdateStatus.Text = UpdateStatus.Text;
        }
        catch (Exception ex) { LauncherUpdateStatus.Text = ex.Message; AppendLog(ex.Message); }
        finally { LauncherTransfer.IsVisible = false; }
    }

    // Called after the new version's real window opens, allowing updater to commit.
    internal static void ConfirmUpdatedWindow(string[]? args)
    {
        if (args is null) return;
        var index = Array.IndexOf(args, "--update-health");
        if (index < 0 || index + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[index + 1]);
        var owned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher", "SelfUpdate") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(owned, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("health-", StringComparison.Ordinal)) return;
        ReleasePolicy.NoLinks(path);
        File.WriteAllText(path, "window-opened " + CurrentLauncherVersion);
    }
}
