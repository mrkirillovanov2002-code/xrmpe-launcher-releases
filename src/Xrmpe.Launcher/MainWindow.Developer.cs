using Avalonia.Controls;
using Avalonia.Interactivity;
using Xrmpe.Launcher.Presentation;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private InterfacePreferencesStore _interfaceStore = null!;
    private InterfacePreferences _interfacePreferences = new();
    private bool _interfaceReady, _applyingInterface;
    private string _displayLogText = "";
    private volatile string _sensitiveUpdateToken = "";
    private CatalogEditPlan? _pendingCatalogEdit;
    private bool DeveloperEnabled => _interfacePreferences.DeveloperMode;
    private void InitializeDeveloperMode()
    {
        _interfaceStore = new(Path.Combine(_dataFolder, "interface.json"));
        _interfacePreferences = _interfaceStore.Load();
        InterfaceStatus.Text = _interfaceStore.Warning ?? "Обычный режим скрывает технические панели, сохраняя работу каталогов и обновлений.";
        _interfaceReady = true;
        UpdateToken.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) _sensitiveUpdateToken = UpdateToken.Text ?? ""; };
        BrowserStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) UpdatePlayerNetworkStatus(); };
        CatalogStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) UpdatePlayerNetworkStatus(); };
        ApplyDeveloperPresentation();
    }
    private void ApplyDeveloperPresentation()
    {
        _applyingInterface = true;
        try { DeveloperModeToggle.IsChecked = DeveloperEnabled; } finally { _applyingInterface = false; }
        Status.Margin = new Avalonia.Thickness(0, DeveloperEnabled ? 40 : 23, 0, 0);
        foreach (var control in new Control[] { DeveloperIndicator, DeveloperSettingsSection, CatalogSettingsPanel, BrowserStatus, BrowserDetails,
            DeveloperQueryPortPanel, ManualQueryLabel, ManualQueryPort, GitHubRepositoryEditor, DeveloperPackageFields, ShareLauncherUpdates }) control.IsVisible = DeveloperEnabled;
        Grid.SetColumnSpan(ManualServerPort, DeveloperEnabled ? 1 : 2);
        PlayerBrowserStatus.IsVisible = PlayerServerDetails.IsVisible = PlayerCatalogNotice.IsVisible = !DeveloperEnabled;
        if (!DeveloperEnabled)
        {
            _pendingCatalogEdit = null; CatalogChangeConfirmation.IsVisible = false;
            DeveloperEnableConfirmation.IsVisible = false;
            CancelRepositoryEdit(null, new RoutedEventArgs());
        }
        LogBox.Height = DeveloperEnabled ? 180 : 100;
        RenderVisibleLog(); UpdatePlayerNetworkStatus(); RefreshLaunchControls();
    }
    private bool StoreInterface(InterfacePreferences next)
    {
        try { _interfaceStore.Save(next); _interfacePreferences = next; ApplyDeveloperPresentation(); return true; }
        catch (Exception ex) { InterfaceStatus.Text = "Режим не изменён: не удалось сохранить настройки интерфейса. " + DiagnosticPrivacy.ForPlayer(ex.Message); ApplyDeveloperPresentation(); return false; }
    }
    private void DeveloperModeChanged(object? sender, RoutedEventArgs e)
    {
        if (!_interfaceReady || _applyingInterface) return;
        bool requested = DeveloperModeToggle.IsChecked == true;
        if (requested && !_interfacePreferences.DeveloperWarningAccepted)
        {
            _applyingInterface = true; DeveloperModeToggle.IsChecked = false; _applyingInterface = false;
            DeveloperEnableConfirmation.IsVisible = true; return;
        }
        if (StoreInterface(_interfacePreferences with { DeveloperMode = requested }))
            InterfaceStatus.Text = requested ? "Режим разработчика включён. Проверки совместимости и подписей обязательны." : "Обычный режим включён. Значения технических настроек сохранены.";
    }
    private void ConfirmDeveloperMode(object? sender, RoutedEventArgs e)
    {
        if (!DeveloperEnableConfirmation.IsVisible) return;
        if (StoreInterface(_interfacePreferences with { DeveloperMode = true, DeveloperWarningAccepted = true }))
        { DeveloperEnableConfirmation.IsVisible = false; InterfaceStatus.Text = "Режим разработчика включён."; }
    }
    private void CancelDeveloperMode(object? sender, RoutedEventArgs e) => DeveloperEnableConfirmation.IsVisible = false;
    private void RenderVisibleLog()
    {
        LogBox.Text = DeveloperEnabled ? _displayLogText : DiagnosticPrivacy.ForPlayer(_displayLogText);
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        RenderRichJournal(LogBox.Text ?? "");
    }
    private void UpdatePlayerNetworkStatus()
    {
        var raw = BrowserStatus.Text ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"Серверов: (\d+) · ответ QR2: (\d+)");
        PlayerBrowserStatus.Text = match.Success ? $"Найдено серверов: {match.Groups[1].Value}; ответили на запрос: {match.Groups[2].Value}." : DiagnosticPrivacy.ForPlayer(raw);
        bool failure = System.Text.RegularExpressions.Regex.IsMatch(raw, @"(?i)ошиб|отказ|недоступ|не удалось|некоррект|не опубликован");
        if (match.Success && failure) PlayerBrowserStatus.Text += raw.Contains("Все настроенные каталоги недоступны", StringComparison.Ordinal)
            ? " Все настроенные интернет-каталоги недоступны. Можно использовать сохранённый адрес или LAN; повторите проверку позже."
            : " Часть источников недоступна; подробности ошибки — в журнале.";
        var selected = ServerList.SelectedItem as BrowserServer;
        PlayerServerDetails.Text = selected is null ? "Выберите сервер для подключения." : $"{selected.Address} · {selected.Availability}";
        var publication = CatalogStatus.Text ?? "";
        PlayerCatalogNotice.Text = System.Text.RegularExpressions.Regex.IsMatch(publication, @"(?i)ошиб|отказ|недоступ|не удалось|не опубликован") ?
            "Ошибка публикации: " + DiagnosticPrivacy.ForPlayer(publication) : _catalogPreferences.Publish ?
            "Автоматическая публикация сервера включена в сохранённых настройках." : "Автоматическая публикация сервера выключена. Список серверов доступен для просмотра.";
    }
    private void OpenDeveloperCatalog(object? sender, RoutedEventArgs e)
    {
        if (!DeveloperEnabled) return;
        Navigate(new Button { Tag = "5" }, e); CatalogSettingsPanel.IsExpanded = true;
    }
    private string BuildSafeDiagnostics() => DiagnosticPrivacy.Export(new(CurrentLauncherVersion.ToString(3), DeveloperEnabled,
        _catalogPreferences.Address.Length != 0, _catalogPreferences.BackupAddress.Length != 0, _catalogPreferences.Publish,
        _running, _serverRunning, UpdatesLockGame, _browserRefresh is not null));
    private async void CopySafeDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (!DeveloperEnabled) return;
        try { if (Clipboard is null) throw new InvalidOperationException("Буфер обмена недоступен."); await Clipboard.SetTextAsync(BuildSafeDiagnostics()); DiagnosticCopyStatus.Text = "Безопасная сводка скопирована. Журнал, адреса, пути и секреты не включены."; }
        catch (Exception ex) { DiagnosticCopyStatus.Text = DiagnosticPrivacy.ForPlayer(ex.Message); }
    }
}
