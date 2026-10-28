using System.Net;
using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xrmpe.Launcher.Updates;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private UpdateHost? _updateHost;
    private CancellationTokenSource? _updateCancel;
    private bool _updateBusy;
    private List<UpdateEntry>? _updatePlan;
    private string? _planRoot, _planAddress, _planToken;
    private bool UpdatesLockGame => _updateBusy || _updateHost is not null;
    private UpdateProgress? _lastUpdateProgress;

    private string UpdateRoot()
    {
        if (string.IsNullOrWhiteSpace(GamePath.Text)) throw new ArgumentException("Выберите папку игры перед обновлением.");
        var path = Path.GetFullPath(GamePath.Text.Trim());
        // Permit repair of a partially copied game; normal launch still requires all files.
        try { return GameInstallation.Resolve(path); }
        catch (FileNotFoundException)
        {
            if (Directory.Exists(Path.Combine(path, "game"))) path = Path.Combine(path, "game");
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Выбранная папка игры не существует.");
            UpdateFiles.EnsureNoLinks(path);
            return path;
        }
    }

    private Uri UpdateServerAddress()
    {
        var host = LauncherService.NormalizeHost(UpdateAddress.Text ?? "");
        return new Uri($"http://{host}:{ReadInteger(UpdatePort, "HTTP-порт")}/");
    }

    private void RefreshUpdateControls()
    {
        UpdateFields.IsEnabled = !_updateBusy && _updateHost is null;
        HostUpdatesButton.IsEnabled = CheckUpdatesButton.IsEnabled = !_updateBusy && _updateHost is null && !_running && !_serverRunning;
        StopUpdatesButton.IsEnabled = _updateHost is not null && !_updateBusy;
        ApplyUpdatesButton.IsEnabled = !_updateBusy && _updateHost is null && _updatePlan is { Count: > 0 } && !_running && !_serverRunning;
        CancelUpdatesButton.IsEnabled = _updateBusy;
        CheckLauncherButton.IsEnabled = !_updateBusy && _updateHost is null && !_running && !_serverRunning && !_githubChecking;
        ApplyLauncherButton.IsEnabled = CheckLauncherButton.IsEnabled && (GitHubSelected ? _githubOffer is not null : _launcherRelease is not null);
        LauncherUpdateSource.IsEnabled = GitHubRepository.IsEnabled = !_updateBusy && !_githubChecking;
    }

    private async Task UpdateOperation(Func<CancellationToken, Task> action)
    {
        if (_updateBusy || _running || _serverRunning) return;
        _updateBusy = true;
        _updateCancel = new CancellationTokenSource();
        RefreshLaunchControls();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var progress = Interlocked.Exchange(ref _lastUpdateProgress, null);
            if (progress is null) return;
            UpdateStatus.Text = progress.Description;
            UpdateBar.Value = progress.Total > 0 ? 100d * progress.Done / progress.Total : 0;
        };
        timer.Start();
        try { await Task.Run(() => action(_updateCancel.Token)); }
        catch (OperationCanceledException) { UpdateStatus.Text = "Операция отменена. Частичные загрузки сохранены для докачки."; AppendLog(UpdateStatus.Text); }
        catch (Exception ex) { UpdateStatus.Text = "Ошибка обновлений: " + ex.Message; AppendLog(UpdateStatus.Text); }
        finally
        {
            timer.Stop(); _lastUpdateProgress = null;
            _updateBusy = false; _updateCancel.Dispose(); _updateCancel = null;
            RefreshLaunchControls();
        }
    }

    private void ReportUpdate(UpdateProgress progress) => Interlocked.Exchange(ref _lastUpdateProgress, progress);
    private void CancelUpdates(object? sender, RoutedEventArgs e) => _updateCancel?.Cancel();
    private void GenerateUpdateToken(object? sender, RoutedEventArgs e) => UpdateToken.Text = UpdateHost.GenerateToken();
    private async void CopyUpdateToken(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is not null) await Clipboard.SetTextAsync(UpdateToken.Text ?? ""); }
        catch { AppendLog("Не удалось скопировать токен. Повторите копирование или задайте свой токен вручную."); }
    }

    private async void HostUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null) return;
        try
        {
            var root = GameInstallation.Resolve(GamePath.Text);
            var bind = IPAddress.Parse(UpdateBind.Text ?? "");
            if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)) throw new ArgumentException("Укажите конкретный LAN/VPN IP, не 0.0.0.0.");
            var port = ReadInteger(UpdatePort, "Порт"); var rate = ReadInteger(UploadLimit, "Скорость");
            var token = UpdateToken.Text ?? ""; var remote = AllowRemoteUpdates.IsChecked == true;
            var launcherRoot = ShareLauncherUpdates.IsChecked == true ? LauncherPackagePath.Text?.Trim() : null;
            if (ShareLauncherUpdates.IsChecked == true && string.IsNullOrEmpty(launcherRoot)) throw new ArgumentException("Выберите папку публикации нового лаунчера.");
            SaveUpdatePreferences();
            UpdateStatus.Text = "Подготовка манифеста SHA-256. Для большой игры это займёт время…";
            await UpdateOperation(async ct =>
            {
                var host = new UpdateHost();
                try { await host.StartAsync(root, bind, port, token, rate, remote, AppendLog, ct, launcherRoot: launcherRoot); _updateHost = host; }
                catch { await host.DisposeAsync(); throw; }
                await Dispatcher.UIThread.InvokeAsync(() => UpdateStatus.Text = $"Раздача {host.Address}. Корень: {root}. Только файлы манифеста; токен обязателен.");
            });
        }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; AppendLog(ex.Message); }
    }

    private async void StopUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is null) return;
        await UpdateOperation(async _ =>
        {
            await _updateHost.DisposeAsync(); _updateHost = null;
            await Dispatcher.UIThread.InvokeAsync(() => UpdateStatus.Text = "Раздача остановлена.");
        });
    }

    private async void CheckUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null) return;
        try
        {
            var root = UpdateRoot(); var server = UpdateServerAddress(); var token = UpdateToken.Text ?? "";
            _updatePlan = null;
            SaveUpdatePreferences();
            await UpdateOperation(async ct =>
            {
                using var client = new UpdateClient(root, server, token);
                var plan = await client.CheckAsync(ReportUpdate, ct);
                _updatePlan = plan; _planRoot = root; _planAddress = server.AbsoluteUri; _planToken = token;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastUpdateProgress = null;
                    UpdateStatus.Text = $"Проверено. Скачать файлов: {plan.Count}, объём: {plan.Sum(f => f.Size) / 1048576d:F1} МиБ. Папка: {root}." + (plan.Count == 0 ? " Всё актуально." : " Нажмите «Скачать и установить».");
                    UpdatePlanDetails.Text = string.Join(Environment.NewLine, plan.Take(200).Select(f => $"{f.Path} · {f.Size / 1048576d:F2} МиБ")) + (plan.Count > 200 ? "\n… Остальные файлы будут перечислены в журнале при установке." : "");
                    UpdateBar.Value = 100;
                });
            });
        }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; AppendLog(ex.Message); }
    }

    private async void ApplyUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _updateHost is not null || _updatePlan is not { Count: > 0 }) return;
        try
        {
            var root = UpdateRoot(); var server = UpdateServerAddress(); var token = UpdateToken.Text ?? "";
            if (root != _planRoot || server.AbsoluteUri != _planAddress || token != _planToken) throw new InvalidOperationException("Настройки изменились: сначала повторите проверку обновлений.");
            var plan = _updatePlan;
            await UpdateOperation(async ct =>
            {
                using var client = new UpdateClient(root, server, token);
                await client.ApplyAsync(plan, ReportUpdate, AppendLog, ct);
                _updatePlan = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastUpdateProgress = null;
                    UpdateStatus.Text = "Обновление завершено. SHA-256 всех загруженных файлов проверены. Другие файлы не удалялись.";
                    UpdateBar.Value = 100;
                });
            });
        }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; AppendLog(ex.Message); }
    }

    private void SaveUpdatePreferences()
    {
        var data = new { Address = UpdateAddress.Text, Port = UpdatePort.Value, Bind = UpdateBind.Text, Limit = UploadLimit.Value };
        try { Directory.CreateDirectory(_dataFolder); File.WriteAllText(Path.Combine(_dataFolder, "updates.json"), JsonSerializer.Serialize(data)); }
        catch (Exception ex) { AppendLog("Не удалось сохранить адрес обновлений: " + ex.Message); }
    }

    private void LoadUpdatePreferences()
    {
        try
        {
            var path = Path.Combine(_dataFolder, "updates.json");
            if (!File.Exists(path)) return;
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            UpdateAddress.Text = data.RootElement.GetProperty("Address").GetString();
            UpdatePort.Value = data.RootElement.GetProperty("Port").GetDecimal();
            UpdateBind.Text = data.RootElement.GetProperty("Bind").GetString();
            UploadLimit.Value = data.RootElement.GetProperty("Limit").GetDecimal();
            // No token, remote-access checkbox, or automatic server start is persisted.
        }
        catch (Exception ex) { AppendLog("Не удалось загрузить настройки обновлений: " + ex.Message); }
    }
}
