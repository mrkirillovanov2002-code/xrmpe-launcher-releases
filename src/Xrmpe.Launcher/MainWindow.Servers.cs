using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private ServerBrowserStore _browserStore = null!;
    private BrowserPreferences _browserPreferences = new([], []);
    private List<BrowserServer> _browserServers = [];
    private CancellationTokenSource? _browserRefresh;
    private LanPublisher? _lanPublisher;
    private readonly DispatcherTimer _browserTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private void InitializeBrowser()
    {
        _browserStore = new(Path.Combine(_dataFolder, "server-browser.json"));
        try { _browserPreferences = _browserStore.Load(); }
        catch (Exception ex) { AppendLog($"Список серверов не загружен: {ex.Message}. Исходный файл сохранён."); }
        AdvertiseServer.IsChecked = _browserPreferences.Advertise;
        foreach (var address in _addresses)
            if (!_browserPreferences.Saved.Any(x => x.Host == address.Host && x.Port == address.Port))
                _browserServers.Add(new(new(address.Host, address.Port)));
        _browserServers.AddRange(_browserPreferences.Saved.Select(x => new BrowserServer(x, Kind: x.Steam ? "Steam" : "IP")));
        ApplyBrowserFilters();
        _browserTimer.Tick += (_, _) => { if (BrowserPage.IsVisible && AutoRefreshServers.IsChecked == true && _browserRefresh is null) RefreshServers(this, new RoutedEventArgs()); };
        _browserTimer.Start();
        InitializeCatalog();
        Closed += async (_, _) => { _browserTimer.Stop(); _browserRefresh?.Cancel(); if (_lanPublisher is not null) await _lanPublisher.DisposeAsync(); };
    }

    private void SaveBrowser()
    {
        try { _browserStore.Save(_browserPreferences); }
        catch (Exception ex) { AppendLog($"Не удалось сохранить список серверов: {ex.Message}"); BrowserStatus.Text = ex.Message; }
    }

    private async void RefreshServers(object? sender, RoutedEventArgs e)
    {
        if (_browserRefresh is not null) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        _browserRefresh = cancel;
        BrowserRefreshButton.IsEnabled = false;
        BrowserStatus.Text = "Обновление каталога и запросы QR2…";
        try
        {
            var internetOnly = ServerTabs.SelectedIndex == 4;
            var lan = internetOnly ? Array.Empty<BrowserServer>() : await new LanDiscovery().DiscoverAsync(cancel.Token);
            IReadOnlyList<BrowserServer> internet = Array.Empty<BrowserServer>();
            var catalogStatus = "Каталог интернета не настроен.";
            if (_catalogPreferences.Address.Length != 0)
            {
                _catalogReader ??= new CatalogCluster(_catalogPreferences);
                try { internet = await _catalogReader.DiscoverAsync(cancel.Token); catalogStatus = _catalogReader.Status; }
                catch (Exception ex) when (ex is not OperationCanceledException) { catalogStatus = _catalogReader.Status + " Ошибка: " + ex.Message; }
            }
            CatalogSourceStatus.Text = catalogStatus;
            var endpoints = _browserPreferences.Saved.Select(x => new BrowserServer(x, Kind: x.Steam ? "Steam" : "IP")).Concat(
                _addresses.Select(x => new BrowserServer(new(x.Host, x.Port)))).Concat(lan).Concat(internet)
                .Where(x => !internetOnly || x.Internet)
                .GroupBy(x => x.Endpoint.Key).Select(group => group.LastOrDefault(x => x.Internet) ?? group.FirstOrDefault(x => x.Kind == "Steam") ?? group.Last()).Take(128).ToArray();
            using var gate = new SemaphoreSlim(12);
            var results = await Task.WhenAll(endpoints.Select(async item =>
            {
                await gate.WaitAsync(cancel.Token);
                try { return await Qr2Query.ProbeAsync(item, cancel.Token); }
                finally { gate.Release(); }
            }));
            _browserServers = internetOnly ? _browserServers.Where(x => !x.Internet && !results.Any(r => r.Endpoint.Key == x.Endpoint.Key)).Concat(results).ToList() : results.ToList();
            ApplyBrowserFilters();
            BrowserStatus.Text = $"Серверов: {results.Length} · ответ QR2: {results.Count(x => x.Snapshot is not null)} · {DateTime.Now:HH:mm:ss}. {catalogStatus}";
        }
        catch (OperationCanceledException) { BrowserStatus.Text = "Поиск остановлен или превышено время ожидания."; }
        catch (Exception ex) { BrowserStatus.Text = $"Ошибка поиска: {ex.Message}"; AppendLog(BrowserStatus.Text); }
        finally { _browserRefresh = null; BrowserRefreshButton.IsEnabled = true; }
    }

    private void FilterServers(object? sender, RoutedEventArgs e) => ApplyBrowserFilters();
    private void FilterServerTab(object? sender, SelectionChangedEventArgs e) { ApplyBrowserFilters(); if (_modernReady) _ = AnimateModern(ServerList, true); }
    private void ApplyBrowserFilters()
    {
        if (ServerList is null || ServerNameFilter is null || ServerMapFilter is null || ServerTabs is null || FreeServerSlots is null) return;
        var selected = (ServerList.SelectedItem as BrowserServer)?.Endpoint.Key;
        var history = _addresses.Select(x => new BrowserEndpoint(x.Host, x.Port).Key).ToHashSet();
        var list = _browserServers.Select(x => x with { Favorite = _browserPreferences.Favorites.Contains(x.Endpoint.Key) })
            .Where(x => ServerTabs.SelectedIndex switch { 1 => x.Favorite, 2 => !x.Internet && (x.Kind is "LAN" or "Steam"), 3 => history.Contains(x.Endpoint.Key), 4 => x.Internet, _ => true })
            .Where(x => new[] { x.Name, x.Address, x.Map, x.Mode }.Any(value => value.Contains(ServerNameFilter.Text ?? "", StringComparison.CurrentCultureIgnoreCase)))
            .Where(x => (ServerMapFilter.Text ?? "").Length == 0 || x.Snapshot is not null && x.Map.Contains(ServerMapFilter.Text!, StringComparison.OrdinalIgnoreCase))
            .Where(x => FreeServerSlots.IsChecked != true || x.Snapshot is { Players: int players, MaxPlayers: int max } && players < max)
            .ToArray();
        list = (ServerSort?.SelectedIndex switch
        {
            1 => list.OrderBy(x => x.Ping ?? int.MaxValue).ThenBy(x => x.Name),
            2 => list.OrderByDescending(x => x.Snapshot?.Players ?? -1).ThenBy(x => x.Name),
            3 => list.OrderBy(x => x.Map).ThenBy(x => x.Name),
            _ => list.OrderBy(x => x.Name)
        }).ToArray();
        ServerList.ItemsSource = list;
        ServerList.SelectedItem = list.FirstOrDefault(x => x.Endpoint.Key == selected);
        RefreshBrowserSelection();
        if (selected is not null && !_browserServers.Any(x => x.Endpoint.Key == selected)) BrowserStatus.Text = "Выбранный сервер исчез из списка. Обновите список или выберите другой сервер.";
    }

    private void BrowserSelectionChanged(object? sender, SelectionChangedEventArgs e) { RefreshBrowserSelection(); if (_modernReady) _ = AnimateModern(BrowserDetailPanel, true); }

    private void RefreshBrowserSelection()
    {
        if (BrowserConnectButton is null || BrowserFavoriteButton is null || BrowserDetails is null) return;
        var selected = ServerList.SelectedItem as BrowserServer;
        BrowserDetails.Text = selected?.Details ?? "Выберите сервер. «—» означает, что данные не получены от движка.";
        BrowserConnectButton.IsEnabled = selected is not null && !_running && !UpdatesLockGame;
        if (_profileStore is not null) BrowserConnectButton.IsEnabled &= ContentClientAllowed;
        if (_modernReady && selected is not null) BrowserConnectButton.IsEnabled &= BrowserTransportAllowed(selected);
        BrowserFavoriteButton.IsEnabled = selected is not null;
        BrowserFavoriteButton.Content = selected?.Favorite == true ? "Убрать из избранного" : "В избранное";
        if (_interfaceReady) UpdatePlayerNetworkStatus();
        PresentSelectedServer(selected);
    }

    private void AddBrowserServer(object? sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = new BrowserEndpoint(ManualServerHost.Text ?? "", ReadInteger(ManualServerPort, "Порт игры"), ReadInteger(ManualQueryPort, "Query-порт")).Validate();
            var saved = _browserPreferences.Saved.Where(x => x.Key != endpoint.Key).Append(endpoint).ToList();
            if (saved.Count > 128) throw new InvalidOperationException("Сохранено 128 серверов; удалите ненужный адрес.");
            _browserPreferences = _browserPreferences with { Saved = saved };
            _browserServers.RemoveAll(x => x.Endpoint.Key == endpoint.Key);
            _browserServers.Add(new(endpoint));
            SaveBrowser(); ApplyBrowserFilters();
            BrowserStatus.Text = "Адрес сохранён. Нажмите «Обновить список» для запроса серверу.";
        }
        catch (Exception ex) { BrowserStatus.Text = ex.Message; }
    }

    private void FavoriteBrowserServer(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not BrowserServer item) return;
        var favorites = _browserPreferences.Favorites.ToList();
        if (!favorites.Remove(item.Endpoint.Key))
        {
            if (favorites.Count >= 128) { BrowserStatus.Text = "В избранном уже 128 серверов."; return; }
            favorites.Add(item.Endpoint.Key);
        }
        var saved = _browserPreferences.Saved.Where(x => x.Key != item.Endpoint.Key).Append(item.Endpoint).TakeLast(128).ToList();
        _browserPreferences = _browserPreferences with { Favorites = favorites, Saved = saved };
        SaveBrowser(); ApplyBrowserFilters();
    }

    private void RemoveBrowserServer(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not BrowserServer item) return;
        _browserPreferences = _browserPreferences with { Saved = _browserPreferences.Saved.Where(x => x.Key != item.Endpoint.Key).ToList(), Favorites = _browserPreferences.Favorites.Where(x => x != item.Endpoint.Key).ToList() };
        _browserServers.RemoveAll(x => x.Endpoint.Key == item.Endpoint.Key);
        SaveBrowser(); ApplyBrowserFilters();
        BrowserStatus.Text = "Адрес удалён из сохранённых. Обнаруживаемый LAN-сервер может появиться после поиска; история подключений сохранена.";
    }

    private async void ConnectBrowserServer(object? sender, RoutedEventArgs e)
    {
        if (_running || UpdatesLockGame || ServerList.SelectedItem is not BrowserServer item) return;
        if (_modernReady && !BrowserTransportAllowed(item)) { RefreshBrowserSelection(); return; }
        if (item.Kind == "Steam")
        {
            BrowserStatus.Text = "Сервер работает через Steam: запущен существующий Steam-клиент. Примите приглашение друга в оверлее; автоматический переход на выбранный сервер не поддерживается.";
            LaunchSteamMenu(sender, e);
            return;
        }
        BrowserConnectButton.IsEnabled = false;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var current = await Qr2Query.ProbeAsync(item, cancel.Token);
            if (_running || UpdatesLockGame) return;
            if (current.Snapshot is null)
            {
                var dialog = new Window { Title = "Сервер не подтвердил доступность", Width = 500, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var yes = new Button { Content = "Попробовать подключиться", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
                yes.Click += (_, _) => dialog.Close(true);
                dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 18, Children = {
                    new TextBlock { Text = $"{item.Address}: сервер не отвечает на запрос сведений. Он может быть выключен или недоступен в вашей сети. Можно попробовать прямое IP-подключение; успешное соединение не гарантируется.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, yes } };
                if (!await dialog.ShowDialog<bool>(this)) return;
            }
            RemoteHost.Text = item.Endpoint.Host; RemotePort.Value = item.Endpoint.Port;
            // Route through the exact pre-existing IP launch and history handler.
            ConnectRemote(sender, e);
            ApplyBrowserFilters();
        }
        catch (Exception ex) { BrowserStatus.Text = $"Ошибка подключения: {ex.Message}"; }
        finally { RefreshBrowserSelection(); }
    }

    private void AdvertiseServerChanged(object? sender, RoutedEventArgs e)
    {
        if (_browserStore is null) return;
        _browserPreferences = _browserPreferences with { Advertise = AdvertiseServer.IsChecked == true };
        SaveBrowser();
    }

    private void StartLanPublication(int port, bool steam)
    {
        AdvertiseServer.IsEnabled = false;
        ServerQueryPort.IsEnabled = false;
        if (AdvertiseServer.IsChecked != true) return;
        try
        {
            _lanPublisher = new(port, ReadInteger(ServerQueryPort, "Query-порт"), steam, () => _launcher.IsServerProcessRunning);
            AppendLog($"LAN-публикация включена: UDP {LanPublisher.DiscoveryPort}. Публикуется адрес процесса, статистика запрашивается отдельно у движка. Брандмауэр не изменяется.");
            _ = RefreshOwnedServerAsync(port, steam);
        }
        catch (Exception ex) { AppendLog($"LAN-публикация недоступна: {ex.Message}. Запуск dedicated продолжается."); }
    }

    private async Task RefreshOwnedServerAsync(int port, bool steam)
    {
        await Task.Delay(1000);
        if (!_launcher.IsServerProcessRunning || _lanPublisher is null) return;
        var endpoint = new BrowserEndpoint("127.0.0.1", port, ReadInteger(ServerQueryPort, "Query-порт"), steam);
        _browserServers.RemoveAll(x => x.Endpoint.Key == endpoint.Key);
        _browserServers.Add(new(endpoint, Kind: steam ? "Steam" : "LAN", LauncherResponded: true));
        ApplyBrowserFilters();
    }
}


