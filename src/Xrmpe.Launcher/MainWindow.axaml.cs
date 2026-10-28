using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Xrmpe.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherService _launcher = new();
    private readonly string _dataFolder = Environment.GetEnvironmentVariable("ZONELINK_DATA_HOME") is { Length: > 0 } isolatedData ? Path.GetFullPath(isolatedData) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xrmpe.CustomLauncher");
    private readonly BufferedLog _journal = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long _logVersion = -1;
    private bool _running;
    private bool _serverRunning;
    private int? _activeServerPort;
    private bool _activeServerSteam;
    private readonly ConnectionHistory _history;
    private readonly GamePathSettings _gameSettings;
    private IReadOnlyList<ServerAddress> _addresses = [];

    public MainWindow()
    {
        _freshProfile = !Directory.Exists(_dataFolder) || !Directory.EnumerateFiles(_dataFolder, "*.json").Any();
        _history = new ConnectionHistory(Path.Combine(_dataFolder, "connection-history.json"));
        _gameSettings = new GamePathSettings(_dataFolder, Environment.MachineName);
        InitializeComponent();
        LauncherVersionLabel.Text = $"Версия {CurrentLauncherVersion.ToString(3)}";
        LauncherUpdateStatus.Text = $"Версия {CurrentLauncherVersion.ToString(3)} · проверка только по вашей команде. Игровые файлы проверяются отдельно. Адрес, порт и токен — выше.";
        LauncherPackagePath.Text = AppContext.BaseDirectory;
        Opened += (_, _) => ShowPreviousSelfUpdateError();
        LoadUpdatePreferences();
        InitializeInternetUpdates();
        _logTimer.Tick += (_, _) =>
        {
            var text = _journal.Snapshot(ref _logVersion);
            if (text is null) return;
            _displayLogText = text;
            RenderVisibleLog();
        };
        _logTimer.Start();
        Closed += async (_, _) => { _logTimer.Stop(); await _journal.DisposeAsync(); };
        try
        {
            Directory.CreateDirectory(_dataFolder);
            var logs = Path.Combine(_dataFolder, "Logs");
            Directory.CreateDirectory(logs);
            var logFile = Path.Combine(logs, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            _journal.SetFile(logFile);
            AppendLog($"Журнал: {logFile}");
        }
        catch (Exception ex) { AppendLog($"Не удалось загрузить настройки: {ex.Message}"); }
        InitializeGameFolder();
        try
        {
            _addresses = _history.Load();
            RecentAddresses.ItemsSource = _addresses;
            if (_addresses.Count > 0) RecentAddresses.SelectedIndex = 0;
        }
        catch (Exception ex) { AppendLog($"Не удалось загрузить историю адресов: {ex.Message}"); }
        InitializeBrowser();
        InitializeContent();
        InitializeDeveloperMode();
        InitializeModern();
        InitializeWorkshopGallery();
        Closing += async (_, e) =>
        {
            if (_closeApproved) return;
            if (_running || _updateBusy || _updateHost is not null)
            {
                e.Cancel = true;
                AppendLog("Закройте игровой клиент и завершите обновление/раздачу перед выходом.");
                return;
            }
            if (_serverRunning || _creatingGame)
            {
                e.Cancel = true;
                if (_closingServer) return;
                _closingServer = true;
                try { await StopOwnedServer(); _closeApproved = true; Close(); }
                catch (Exception ex) { AppendLog("Не удалось завершить сервер: " + ex.Message); }
                finally { _closingServer = false; }
            }
        };
    }

    private async void Navigate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var index)) return;
        Control[] pages = [PlayPage, ServerPage, ConnectPage, SettingsPage, UpdatesPage, BrowserPage, AddonsPage, WorkshopPage];
        if (index < 0 || index >= pages.Length) return;
        PageTitle.Text = new[] { "Главная", "Параметры игры", "Подключиться", "Настройки", "Обновления", "Серверы", "Игровой контент", "Мастерская" }[index];
        PageScroll.Offset = default;
        Button[] buttons = [PlayNav, ServerNav, ConnectNav, SettingsNav, UpdatesNav, BrowserNav, AddonsNav, WorkshopNav];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].IsVisible = i == index;
            buttons[i].Classes.Set("selected", i == index);
        }
        if (index == 7) OpenWorkshopGallery();
        await AnimateModern(pages[index]);
    }

    private async void Browse(object? sender, RoutedEventArgs e)
    {
        if (_running || _serverRunning || UpdatesLockGame) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка игры xrMPE", AllowMultiple = false });
            if (folders.Count == 0) return;
            GamePath.Text = folders[0].TryGetLocalPath();
            SavePath();
        }
        catch (Exception ex) { AppendLog($"Ошибка выбора папки: {ex.Message}"); }
    }

    public void ChooseGameFolderAfterInstall() => Browse(this, new RoutedEventArgs());

    private void PathChanged(object? sender, RoutedEventArgs e) => SavePath();
    private void SavePath()
    {
        ValidateGameFolder();
        ContentGameFolderChanged();
    }

    private void InitializeGameFolder()
    {
        try
        {
            var saved = _gameSettings.Load();
            if (!string.IsNullOrWhiteSpace(saved))
            {
                try { GamePath.Text = GameInstallation.Resolve(saved); ValidateGameFolder(); return; }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                { AppendLog($"Сохранённый путь недоступен на этом компьютере: {ex.Message}"); }
            }
        }
        catch (Exception ex) { AppendLog($"Не удалось прочитать путь игры: {ex.Message}"); }
        var nearby = GameInstallation.FindNearby(AppContext.BaseDirectory);
        if (nearby is not null)
        {
            GamePath.Text = nearby;
            AppendLog($"Игра найдена рядом с лаунчером: {nearby}");
            ValidateGameFolder();
        }
        else
        {
            GameFolderStatus.Text = "Игра рядом с лаунчером не найдена. Нажмите «Выбрать папку игры» и укажите папку game или установки.";
            AppendLog(GameFolderStatus.Text);
        }
    }

    private bool ValidateGameFolder()
    {
        string game;
        try { game = GameInstallation.Resolve(GamePath.Text); }
        catch (Exception ex)
        {
            GameFolderStatus.Text = ex.Message;
            Status.Text = "●  Выберите папку игры";
            AppendLog(ex.Message);
            return false;
        }
        GamePath.Text = game;
        GameFolderStatus.Text = "Проверено: клиент, dedicated и fsgame_sc.ltx найдены. Выбранный аддон проверяется отдельно.";
        try { _gameSettings.Save(game); }
        catch (Exception ex) { AppendLog($"Путь проверен, но не сохранён: {ex.Message}"); }
        return true;
    }

    private async void Launch(object? sender, RoutedEventArgs e)
    {
        if (UpdatesLockGame) return;
        if (_running) return;
        if (!ValidateGameFolder()) return;
        if (!await EnsureContentLaunch(false)) return;
        _running = true;
        RefreshLaunchControls();
        GamePath.IsEnabled = false;
        Status.Text = "●  PvP-клиент выполняется";
        SavePath();
        try
        {
            var port = _activeServerPort ?? ReadInteger(ServerPort, "Порт");
            var folder = GamePath.Text ?? "";
            var steam = _serverRunning ? _activeServerSteam : NetworkMode.SelectedIndex == 1;
            var menu = !_serverRunning && SelectedContentMode != "dm";
            var exitCode = steam
                ? await _launcher.RunSteamClientAsync(folder, menu ? null : port, AppendLog, _activeAddonName)
                : menu ? await _launcher.RunMenuClientAsync(folder,AppendLog,_activeAddonName) : await _launcher.RunClientAsync(folder, AppendLog, port, _activeAddonName);
            AppendLog($"PvP-клиент завершён. Код выхода: {exitCode}." + (exitCode == 0 ? "" : " Проверьте сообщения выше и логи движка."));
            Status.Text = exitCode == 0 ? "●  Клиент завершён" : $"●  Ошибка клиента: {exitCode}";
        }
        catch (Exception ex) { AppendLog($"Ошибка запуска: {ex.Message}"); Status.Text = "●  Ошибка запуска"; }
        finally { _running = false; RefreshLaunchControls(); }
    }

    private static int ReadInteger(NumericUpDown input, string label)
    {
        if (input.Value is not decimal value || value != decimal.Truncate(value))
            throw new ArgumentException($"{label}: укажите целое число.");
        return decimal.ToInt32(value);
    }

    private async void LaunchServer(object? sender, RoutedEventArgs e)
    {
        if (UpdatesLockGame || _creatingGame || _serverRunning || _running) return;
        _creatingGame = true; _serverStopRequested = false;
        _hostCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostCancel = new CancellationTokenSource();
        RefreshLaunchControls();
        Task<int>? serverTask = null;
        try
        {
            if (!ValidateGameFolder() || !await EnsureContentLaunch(true)) return;
            ClearServerFailure();
            var folder = GamePath.Text ?? "";
            var profile = ReadContentProfile();
            var addon = SelectedAddon!;
            var steam = NetworkMode.SelectedIndex == 1;
            LauncherService.CreateAddonServerStartInfo(folder, addon, profile, steam);
            var endpoint = new BrowserEndpoint("127.0.0.1", profile.Port, profile.QueryPort, steam);
            ServerStatus.Text = "Проверяем свободный порт…";
            await ServerReadiness.EnsureFreeAsync(endpoint, _hostCancel.Token);
            _profileStore.Save(_contentRoot!, profile);
            _hostCancel.Token.ThrowIfCancellationRequested();
            _serverRunning = true; _serverStarting = true;
            _activeServerPort = profile.Port; _activeServerSteam = steam;
            RefreshLaunchControls();
            ServerStatus.Text = "Загружаем сервер. Ожидаем ответ игрового протокола…";
            serverTask = _launcher.RunAddonServerAsync(folder, addon, profile, steam, AppendLog);
            _ownedServerTask = serverTask;
            var ready = await ServerReadiness.WaitAsync(endpoint, profile.Name, profile.Mode == "coop" ? null : profile.Map,
                serverTask, () => _launcher.IsServerProcessRunning, _hostCancel.Token);
            _hostCancel.Token.ThrowIfCancellationRequested();
            _serverStarting = false;
            ServerStatus.Text = "Сервер ответил. Подключаем создателя…";
            AppendLog("Готовность: QR2 " + ready.Map + ". Это подтверждает сетевой ответ, но не загрузку карты клиентом.");
            StartLanPublication(profile.Port, steam); StartCatalogPublication(profile.Port, steam);
            RefreshLaunchControls();
            Launch(this, new RoutedEventArgs());
            var code = await serverTask;
            ServerStatus.Text = _serverStopRequested ? "Сервер остановлен" : $"Сервер завершён · код {code}";
            AppendLog(ServerStatus.Text);
            if (code != 0 && !_serverStopRequested) ShowServerFailure(Presentation.ServerFailure.FromExit(code));
        }
        catch (OperationCanceledException) { ServerStatus.Text = "Создание игры отменено"; }
        catch (Exception ex) { ServerStatus.Text = "Игра не создана. Проверьте причину и повторите запуск."; ShowServerFailure(Presentation.ServerFailure.FromException(ex)); }
        finally
        {
            if (serverTask is not null && !serverTask.IsCompleted)
            {
                try { await _launcher.StopServerAsync(); }
                catch (Exception ex) { AppendLog("Ошибка завершения собственного сервера: " + ex.Message); }
            }
            try { await StopCatalogPublication(); if (_lanPublisher is not null) await _lanPublisher.DisposeAsync(); }
            catch (Exception ex) { AppendLog("Остановка публикации: " + ex.Message); }
            _lanPublisher = null; _serverStarting = false;
            _serverRunning = _launcher.IsServerProcessRunning;
            if (!_serverRunning) { _activeServerPort = null; _activeServerSteam = false; }
            _creatingGame = false; _ownedServerTask = null;
            _hostCancel?.Dispose(); _hostCancel = null;
            AdvertiseServer.IsEnabled = ServerQueryPort.IsEnabled = !_serverRunning;
            RefreshLaunchControls();
            _hostCompletion?.TrySetResult();
        }
    }

    private void RefreshLaunchControls()
    {
        StopServerButton.IsEnabled = (_serverRunning || _creatingGame) && !_serverStopRequested;
        LaunchButton.IsEnabled = ConnectButton.IsEnabled = SteamClientButton.IsEnabled = !_running;
        RemoteHost.IsEnabled = RemotePort.IsEnabled = RecentAddresses.IsEnabled = !_running;
        NetworkMode.IsEnabled = GamePath.IsEnabled = !_running && !_serverRunning;
        SelectGameFolderButton.IsEnabled = !_running && !_serverRunning;
        if (UpdatesLockGame)
        {
            LaunchButton.IsEnabled = ServerLaunchButton.IsEnabled = ConnectButton.IsEnabled = SteamClientButton.IsEnabled = false;
            GamePath.IsEnabled = SelectGameFolderButton.IsEnabled = NetworkMode.IsEnabled = false;
        }
        else ServerLaunchButton.IsEnabled = !_serverRunning;
        RefreshUpdateControls();
        if (BrowserConnectButton is not null) RefreshBrowserSelection();
        if (_profileStore is not null)
        {
            LaunchButton.IsEnabled &= ContentClientAllowed;
            ConnectButton.IsEnabled &= ContentClientAllowed; SteamClientButton.IsEnabled &= ContentClientAllowed;
            ServerLaunchButton.IsEnabled &= ContentServerAllowed;
            var editable = !_running && !_serverRunning && !_creatingGame && !_verifyingContentLaunch && !UpdatesLockGame;
            AddonCards.IsEnabled = ContentMode.IsEnabled = VerifiedMapChoice.IsEnabled = RefreshAddonsButton.IsEnabled = editable;
            SaveProfileButton.IsEnabled = editable && _contentReady;
            CoopScenarioPanel.IsEnabled=DefenceScenarioPanel.IsEnabled=ScenarioMinPanel.IsEnabled=editable;
        }
        if (_verifyingContentLaunch) { LaunchButton.IsEnabled = ConnectButton.IsEnabled = SteamClientButton.IsEnabled = ServerLaunchButton.IsEnabled = false; GamePath.IsEnabled = SelectGameFolderButton.IsEnabled = NetworkMode.IsEnabled = false; }
        CreateGameButton.IsEnabled = ServerLaunchButton.IsEnabled = ServerLaunchButton.IsEnabled && !_creatingGame && !_running;
        if (_serverStarting || (_creatingGame && !_serverRunning)) { LaunchButton.IsEnabled = SteamClientButton.IsEnabled = ConnectButton.IsEnabled = false; }
        AdvancedIpToggle.IsEnabled = !_running && !_serverRunning && !_creatingGame;
        RefreshArmbands();
        UpdateZoneReadiness();
    }

    private void NetworkModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while InitializeComponent is still constructing controls.
        if (IpConnectPanel is null || SteamConnectPanel is null) return;
        IpConnectPanel.IsVisible = _interfacePreferences.AdvancedIp && NetworkMode.SelectedIndex != 1;
        SteamConnectPanel.IsVisible = NetworkMode.SelectedIndex == 1;
        PersistTransportChoice();
        if (_profileStore is not null) UpdateContentAvailability();
    }

    private void RecentAddressChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RecentAddresses.SelectedItem is not ServerAddress address) return;
        RemoteHost.Text = address.Host;
        RemotePort.Value = address.Port;
    }

    private async void ConnectRemote(object? sender, RoutedEventArgs e)
    {
        if (UpdatesLockGame || !_interfacePreferences.AdvancedIp) return;
        if (_running) return;
        if (!ValidateGameFolder()) return;
        if (!await EnsureContentLaunch(false)) return;
        try
        {
            var host = LauncherService.NormalizeHost(RemoteHost.Text ?? "");
            var port = ReadInteger(RemotePort, "Порт сервера");
            var folder = GamePath.Text ?? "";
            LauncherService.CreateRemoteClientStartInfo(folder, host, port, _activeAddonName);
            try
            {
                _addresses = _history.Remember(host, port, _addresses);
                RecentAddresses.ItemsSource = _addresses;
                RecentAddresses.SelectedIndex = 0;
            }
            catch (Exception ex) { AppendLog($"Не удалось сохранить адрес: {ex.Message}"); }
            _running = true;
            RefreshLaunchControls();
            Status.Text = $"●  Клиент IP: {host}:{port}";
            SavePath();
            var code = await _launcher.RunRemoteClientAsync(folder, host, port, AppendLog, _activeAddonName);
            Status.Text = $"●  Клиент завершён: {code}";
            AppendLog($"IP-клиент завершён. Код выхода: {code}. Подключение проверяйте в игре и на сервере.");
        }
        catch (Exception ex) { AppendLog($"Ошибка подключения: {ex.Message}"); Status.Text = "●  Ошибка подключения"; }
        finally { _running = false; RefreshLaunchControls(); }
    }

    private async void LaunchSteamMenu(object? sender, RoutedEventArgs e)
    {
        if (UpdatesLockGame) return;
        if (_running) return;
        if (!ValidateGameFolder()) return;
        if (!await EnsureContentLaunch(false, true)) return;
        _running = true;
        RefreshLaunchControls();
        try
        {
            Status.Text = "●  Клиент Steam · эксперимент";
            var code = await _launcher.RunSteamClientAsync(GamePath.Text ?? "", null, AppendLog, _activeAddonName);
            AppendLog($"Steam-клиент завершён. Код выхода: {code}.");
            Status.Text = $"●  Клиент завершён: {code}";
        }
        catch (Exception ex) { AppendLog($"Ошибка Steam: {ex.Message}"); Status.Text = "●  Ошибка Steam"; }
        finally { _running = false; RefreshLaunchControls(); }
    }

    private void AppendLog(string message) => _journal.Append(Xrmpe.Launcher.Presentation.DiagnosticPrivacy.RemoveSecrets(message, _sensitiveUpdateToken));

    private void ClearLog(object? sender, RoutedEventArgs e)
    {
        _journal.Clear();
        _displayLogText = "";
        LogBox.Text = "";
        _richLogText = null; RichLogList.ItemsSource = Array.Empty<object>();
    }

    private async void StopServer(object? sender, RoutedEventArgs e)
    {
        if (_serverStopRequested) return;
        try { await StopOwnedServer(); }
        catch (Exception ex) { AppendLog("Ошибка остановки сервера: " + ex.Message); _serverStopRequested = false; }
        finally { RefreshLaunchControls(); }
    }
}
