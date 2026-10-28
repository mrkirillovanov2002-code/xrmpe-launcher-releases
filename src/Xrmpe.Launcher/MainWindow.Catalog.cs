using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private CatalogPreferences _catalogPreferences = new();
    private CancellationTokenSource? _catalogPublicationCancel;
    private Task? _catalogPublication;
    private bool _catalogInitialized;
    private CatalogCluster? _catalogReader;
    private CatalogPreferencesStore _catalogStore = null!;
    private string _catalogSettingsPath = "";
    private bool _pendingCatalogReset;
    private void InitializeCatalog()
    {
        var path = Path.Combine(_dataFolder, "global-catalog.json");
        _catalogSettingsPath = path; _catalogStore = new(path);
        if (_freshProfile) _catalogPreferences = CatalogPreferencesStore.NewProfileDefaults();
        try
        {
            if (_freshProfile && !File.Exists(path)) _catalogStore.Save(_catalogPreferences);
            if (File.Exists(path))
            {
                _catalogPreferences = _catalogStore.Load();
            }
        }
        catch (Exception ex) { AppendLog("Каталог не настроен: " + ex.Message); _catalogPreferences = new(); }
        CatalogAddress.Text = _catalogPreferences.Address;
        CatalogBackupAddress.Text = _catalogPreferences.BackupAddress;
        AdvertiseInternet.IsChecked = _catalogPreferences.Publish;
        CatalogSourceStatus.Text = _catalogPreferences.Address.Length == 0 ? "Каталог не настроен." :
            "Основной: " + _catalogPreferences.Address + (_catalogPreferences.BackupAddress.Length == 0 ? " · резерв не настроен." : " · резерв: " + _catalogPreferences.BackupAddress) + " Связь ещё не проверена.";
        CatalogStatus.Text = _catalogPreferences.Publish ? "Публикация разрешена и начнётся при запуске dedicated. Доступность игры проверяется отдельно." : "Публикация выключена. Для чтения списка достаточно адресов каталогов.";
        _catalogInitialized = true;
        Closed += async (_, _) => { await StopCatalogPublication(); _catalogReader?.Dispose(); };
    }
    private void SaveCatalog(object? sender, RoutedEventArgs e)
    {
        if (!_catalogInitialized || !DeveloperEnabled) return;
        try
        {
            if (_serverRunning) throw new InvalidOperationException("Остановите dedicated перед изменением каталога.");
            if (_browserRefresh is not null) throw new InvalidOperationException("Дождитесь завершения поиска серверов.");
            var address = CatalogAddress.Text?.Trim() ?? "";
            if (address.Length != 0) address = GlobalCatalog.ValidateAddress(address).GetLeftPart(UriPartial.Authority);
            if (AdvertiseInternet.IsChecked == true && address.Length == 0) throw new ArgumentException("Для публикации укажите HTTPS-адрес своего каталога.");
            var preferences = CatalogPreferencesStore.Validate(new CatalogPreferences(address, AdvertiseInternet.IsChecked == true, CatalogBackupAddress.Text?.Trim() ?? ""));
            StageCatalogEdit(preferences, false);
        }
        catch (Exception ex) { CatalogStatus.Text = ex.Message; }
    }
    private void StageCatalogEdit(CatalogPreferences proposed, bool reset)
    {
        if (!DeveloperEnabled) return;
        if (_serverRunning || _browserRefresh is not null) throw new InvalidOperationException("Остановите dedicated и дождитесь завершения поиска серверов.");
        _pendingCatalogEdit = Xrmpe.Launcher.Presentation.CatalogEditPlan.Create(_catalogPreferences, proposed);
        _pendingCatalogReset = reset;
        CatalogChangeText.Text = (reset ? "Восстановление проверенных значений по умолчанию:\n\n" : "Подтвердите изменение сетевых настроек:\n\n") + _pendingCatalogEdit.Summary;
        CatalogChangeConfirmation.IsVisible = true;
    }
    private void ResetCatalogDefaults(object? sender, RoutedEventArgs e)
    {
        try { StageCatalogEdit(CatalogPreferencesStore.NewProfileDefaults(), true); }
        catch (Exception ex) { CatalogStatus.Text = ex.Message; }
    }
    private void CancelCatalogEdit(object? sender, RoutedEventArgs e)
    { _pendingCatalogEdit = null; CatalogChangeConfirmation.IsVisible = false; }
    private void ConfirmCatalogEdit(object? sender, RoutedEventArgs e)
    {
        if (!DeveloperEnabled || _pendingCatalogEdit is not { } plan) return;
        try
        {
            if (_serverRunning || _browserRefresh is not null) throw new InvalidOperationException("Остановите dedicated и дождитесь завершения поиска серверов.");
            if (_catalogPreferences != plan.Expected) throw new InvalidOperationException("Настройки изменились после открытия подтверждения. Повторите проверку.");
            foreach (var file in new[] { _catalogSettingsPath, _catalogSettingsPath + ".tmp", _catalogSettingsPath + ".bak" }) Xrmpe.Launcher.Content.ContentPaths.NoLinks(file);
            if (File.Exists(_catalogSettingsPath))
            {
                try { if (_catalogStore.Load() != plan.Expected) throw new InvalidOperationException("Файл настроек изменён извне. Повторно откройте лаунчер перед сохранением."); }
                catch (Exception ex) when (_pendingCatalogReset && ex is JsonException or InvalidDataException or ArgumentException)
                { File.Copy(_catalogSettingsPath, _catalogSettingsPath + ".corrupt-" + Guid.NewGuid().ToString("N")); }
                File.Copy(_catalogSettingsPath, _catalogSettingsPath + ".bak", true);
            }
            _catalogStore.Save(plan.Proposed);
            _catalogPreferences = plan.Proposed;
            CatalogAddress.Text = plan.Proposed.Address; CatalogBackupAddress.Text = plan.Proposed.BackupAddress;
            AdvertiseInternet.IsChecked = plan.Proposed.Publish;
            _catalogReader?.Dispose(); _catalogReader = null;
            CatalogSourceStatus.Text = "Настройки изменены локально. Связь будет проверена при следующем обновлении списка.";
            CatalogStatus.Text = "Настройки сохранены. Публикация " + (plan.Proposed.Publish ? "разрешена при следующем запуске dedicated." : "выключена.");
            CancelCatalogEdit(sender, e); UpdatePlayerNetworkStatus();
        }
        catch (Exception ex) { CatalogStatus.Text = "Настройки не сохранены: " + ex.Message; }
    }
    private void StartCatalogPublication(int port, bool steam)
    {
        AdvertiseInternet.IsEnabled = false;
        CatalogSaveButton.IsEnabled = false;
        if (!_catalogPreferences.Publish || _catalogPreferences.Address.Length == 0) return;
        var query = ReadInteger(ServerQueryPort, "Query-порт");
        var preferences = _catalogPreferences;
        _catalogPublicationCancel = new();
        var ct = _catalogPublicationCancel.Token;
        _catalogPublication = Run();
        async Task Run()
        {
            try
            {
                using var catalog = new CatalogCluster(preferences);
                await catalog.PublishWhileAlive(port, query, steam, () => _launcher.IsServerProcessRunning,
                    message => Dispatcher.UIThread.Post(() => { CatalogStatus.Text = message; AppendLog(message); }), ct);
            }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendLog("Публикация каталога остановлена: " + ex.Message)); }
        }
    }
    private async Task StopCatalogPublication()
    {
        var cancel = _catalogPublicationCancel; var task = _catalogPublication;
        _catalogPublicationCancel = null; _catalogPublication = null;
        cancel?.Cancel();
        if (task is not null) await task;
        cancel?.Dispose();
        AdvertiseInternet.IsEnabled = true; CatalogSaveButton.IsEnabled = true;
    }
}
