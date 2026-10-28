using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private ServerProfileStore _profileStore = null!;
    private IReadOnlyList<AddonDescriptor> _addons = [];
    private readonly List<Bitmap> _addonImages = [];
    private CancellationTokenSource? _contentScan;
    private string? _contentRoot;
    private string _activeAddonName = "xrmpe_pvp.addon";
    private bool _contentReady, _applyingContent, _contentClosed;
    private bool _verifyingContentLaunch;
    private AddonDescriptor? SelectedAddon => _addons.FirstOrDefault(x => x.FileName == _activeAddonName);
    private string SelectedContentMode => ContentMode.SelectedItem is ModeChoice mode ? mode.Code : ContentMode.SelectedItem as string ?? "dm";
    private void InitializeContent()
    {
        _profileStore = new(Path.Combine(_dataFolder, "server-profiles.json"));
        _modWorkspaces = new(Path.Combine(_dataFolder, "mod-workspaces.json"));
        Opened += async (_, _) => await RefreshContentAsync();
        Closed += (_, _) => { _contentClosed = true; _contentScan?.Cancel(); foreach (var image in _addonImages) image.Dispose(); };
    }
    private void ContentGameFolderChanged()
    {
        if (_profileStore is null || _running || _serverRunning || UpdatesLockGame) return;
        try { if (ContentPaths.Root(GamePath.Text ?? "") != _contentRoot) _ = RefreshContentAsync(); }
        catch { _contentReady = false; RefreshLaunchControls(); }
    }
    private async void RefreshAddons(object? sender, RoutedEventArgs e)
    {
        if (_running || _serverRunning || UpdatesLockGame) return;
        await RefreshContentAsync();
    }
    private async Task RefreshContentAsync()
    {
        if (_contentClosed || _running || _serverRunning || UpdatesLockGame) return;
        _contentScan?.Cancel(); var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _contentScan = cancel;
        ServerProfile? unsaved = null;
        try { if (_hasContentProfile && _contentRoot == ContentPaths.Root(GamePath.Text ?? "")) unsaved = ReadContentProfile(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { }
        _contentReady = false; RefreshLaunchControls(); AddonCatalogStatus.Text = "Чтение заголовков и метаданных…";
        try
        {
            var root = ContentPaths.Root(GamePath.Text ?? "");
            var list = await AddonCatalog.ScanAsync(root, cancel.Token);
            if (cancel.IsCancellationRequested || _contentClosed) return;
            _contentRoot = root; _addons = list;
            // Refresh of the same installation preserves unsaved controls; a different game gets its own profile.
            var profile = unsaved ?? _profileStore.Load(root);
            ApplyContentProfile(profile); _hasContentProfile = true; _contentReady = true;
            AddonCatalogStatus.Text = $"Найдено: {list.Count} (лимит 128) · метаданные прочитаны: {list.Count(x => x.Healthy)}. DB не распаковывались; целостность ресурсов не проверялась.";
            if (_profileStore.RecoveryMessage is { } message) { ProfileStatus.Text = message; AppendLog(message); }
            RenderAddonCards(); UpdateContentAvailability();
            // The workshop may have opened while the initial catalogue scan was running.
            if (_workshopReady && WorkshopPage.IsVisible && !_workshopPreviewLoading)
                _ = LoadLocalWorkshopPreviews();
        }
        catch (OperationCanceledException) { if (_contentScan == cancel) AddonCatalogStatus.Text = "Чтение остановлено или превышено время ожидания."; }
        catch (Exception ex) { if (_contentScan == cancel) { AddonCatalogStatus.Text = "Каталог не прочитан: " + ex.Message; ContentAvailability.Text = AddonCatalogStatus.Text; } }
        finally { if (_contentScan == cancel) { _contentScan = null; RefreshLaunchControls(); } cancel.Dispose(); }
    }
    private bool _hasContentProfile;
    private async void CopyAddonFingerprint(object? sender, RoutedEventArgs e)
    {
        if (!_contentReady || _contentRoot is null || SelectedAddon is not { Healthy: true } addon) return;
        var path = Path.Combine(_contentRoot, "addons", addon.FileName);
        AddonFingerprintButton.IsEnabled = false;
        AddonFingerprintStatus.Text = "Вычисляем SHA-256 в фоне…";
        try
        {
            ContentPaths.NoLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
            var text = $"Аддон: {addon.FileName}\nВерсия: {addon.Version}\nРазмер: {stream.Length} байт\nSHA-256: {hash}";
            if (Clipboard is not { } clipboard) throw new IOException("Буфер обмена недоступен.");
            await clipboard.SetTextAsync(text);
            AddonFingerprintStatus.Text = $"Скопировано: {addon.FileName}, {addon.Version}. Сравните все четыре строки у хоста и друзей. Совпадение пакета не проверяет остальные файлы игры.";
        }
        catch (Exception ex) { AddonFingerprintStatus.Text = "Не удалось получить сведения: " + ex.Message; }
        finally { AddonFingerprintButton.IsEnabled = true; }
    }
    private ServerProfile ReadContentProfile() => ServerProfileStore.Validate(new(_activeAddonName, SelectedContentMode, ServerMap.Text ?? "unselected",
        ServerName.Text ?? "", ReadInteger(ServerPlayers, "Игроки"), ReadInteger(ServerPort, "Порт"), ReadInteger(ServerKills, "Лимит убийств"), ReadInteger(ServerQueryPort, "Query-порт"),
        (CampaignSaveChoice.SelectedItem as CampaignSave)?.Name ?? "", (CampaignSaveChoice.SelectedItem as CampaignSave)?.Official ?? false,
        (CampaignDifficulty.SelectedItem as DifficultyChoice)?.Code ?? "none", ReadInteger(ScenarioMinPlayers,"Минимум игроков"),
        SelectedContentMode == "defence" ? ReadRotation() : null));
    private void ApplyContentProfile(ServerProfile profile)
    {
        _applyingContent = true;
        try
        {
            _activeAddonName = profile.Addon;
            ServerName.Text = profile.Name; ServerPlayers.Value = profile.Players; ServerPort.Value = profile.Port;
            ServerKills.Value = profile.Kills; ServerQueryPort.Value = profile.QueryPort;
            var modes = (SelectedAddon?.Modes ?? []).Append(profile.Mode).Distinct().Select(x => new ModeChoice(x)).ToArray();
            ContentMode.ItemsSource = modes;
            ContentMode.SelectedItem = modes.First(x => x.Code == profile.Mode);
            UpdateMapChoices(profile.Map);
            ApplyScenarioControls(profile);
        }
        finally { _applyingContent = false; }
        ActiveAddonLabel.Text = SelectedAddon is { } addon ? $"Выбран: {addon.Name} · {addon.FileName}" : $"Выбранный пакет отсутствует: {_activeAddonName}";
        if (AddonFingerprintStatus is not null) AddonFingerprintStatus.Text = "Сравните сведения выбранного аддона у всех участников перед подключением.";
    }
    private void UpdateMapChoices(string wanted)
    {
        var rows = _contentRoot is null ? [] : ContentPolicy.Maps(SelectedAddon, SelectedContentMode, _contentRoot);
        VerifiedMapChoice.ItemsSource = rows;
        VerifiedMapChoice.SelectedItem = rows.FirstOrDefault(x => x.Id == wanted);
        ServerMap.Text = wanted;
        UpdateMapEvidence();
    }
    private void ContentModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingContent || _profileStore is null) return;
        _applyingContent = true;
        try { var map=ContentPolicy.Maps(SelectedAddon,SelectedContentMode,_contentRoot!).FirstOrDefault()?.Id??"unselected"; UpdateMapChoices(map); ApplyScenarioControls(new(_activeAddonName,SelectedContentMode,map)); } finally { _applyingContent = false; }
        UpdateContentAvailability();
    }
    private void ContentMapChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingContent || _profileStore is null || sender is not ComboBox combo || combo.SelectedItem is not MapChoice map) return;
        _applyingContent = true;
        try { ServerMap.Text = map.Id; if(SelectedContentMode=="defence") DefenceRotation.Text=map.Id; UpdateMapEvidence(); }
        finally { _applyingContent = false; }
        UpdateContentAvailability();
    }
    private void SaveServerProfile(object? sender, RoutedEventArgs e)
    {
        if (!_contentReady || _contentRoot is null || _running || _serverRunning || UpdatesLockGame) return;
        try { _profileStore.Save(_contentRoot, ReadContentProfile()); ProfileStatus.Text = "Профиль сохранён отдельно для этой установки игры. Предыдущая версия — в .bak."; }
        catch (Exception ex) { ProfileStatus.Text = "Профиль не сохранён: " + ex.Message; }
    }
    private void PickContentAddon(AddonDescriptor addon)
    {
        if (!_contentReady || _contentRoot is null || _running || _serverRunning || _verifyingContentLaunch || UpdatesLockGame || !addon.Healthy) return;
        try
        {
            SaveModNotesIfChanged();
            var previous = ReadContentProfile(); var mode = addon.Modes.FirstOrDefault() ?? "unknown";
            var maps = ContentPolicy.Maps(addon, mode, _contentRoot);
            _profileStore.Save(_contentRoot,previous);
            var profile = _profileStore.LoadForAddon(_contentRoot,addon.FileName) ?? previous with { Addon = addon.FileName, Mode = mode, Map = maps.FirstOrDefault(x=>x.Id==addon.StartMap)?.Id ?? maps.FirstOrDefault()?.Id ?? "unselected", CoopSave="", OfficialSave=false, Rotation=null, Difficulty="none", MinPlayers=1 };
            _profileStore.Save(_contentRoot, profile); ApplyContentProfile(profile); RenderAddonCards(); UpdateContentAvailability();
            ProfileStatus.Text = "Выбор аддона и его отдельный профиль сохранены. Игровые файлы не изменены.";
        }
        catch (Exception ex) { ProfileStatus.Text = "Выбор не сохранён: " + ex.Message; }
    }
    private bool SelectedSteam => _serverRunning ? _activeServerSteam : NetworkMode.SelectedIndex == 1;
    private bool ContentClientAllowed => _contentReady && ContentPolicy.CanLaunch(SelectedAddon, SelectedSteam);
    private string? ServerContentReason() {
        try { return !_contentReady || _contentRoot is null ? "Каталог не готов." : AddonScenarios.ServerReason(SelectedAddon,ReadContentProfile(),_contentRoot,SelectedSteam); }
        catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException) {return ex.Message;}
    }
    private bool ContentServerAllowed => ServerContentReason() is null;
    private async Task<bool> EnsureContentLaunch(bool server, bool? steamOverride = null)
    {
        if (_verifyingContentLaunch) return false;
        _verifyingContentLaunch = true;
        var preparingArmbands = false;
        RefreshLaunchControls();
        try
        {
            if (_contentRoot != ContentPaths.Root(GamePath.Text ?? "")) throw new InvalidOperationException("Папка игры изменилась. Обновите каталог аддонов.");
            if (!(server ? ContentServerAllowed : ContentClientAllowed) || !ContentPolicy.CanLaunch(SelectedAddon, steamOverride ?? SelectedSteam))
                throw new InvalidOperationException(server ? ServerContentReason() ?? "Аддон недоступен для выбранного подключения." : AddonScenarios.ClientReason(SelectedAddon) ?? "Аддон недоступен для выбранного подключения.");
            LauncherService.ValidateAddon(_contentRoot!, _activeAddonName);
            var current = await Task.Run(() => AddonCatalog.ReadLaunchCandidate(Path.Combine(_contentRoot!, "addons", _activeAddonName)));
            if (!ContentPolicy.CanLaunch(current, steamOverride ?? SelectedSteam) || current.Version != SelectedAddon?.Version || current.StartMap != SelectedAddon?.StartMap || !current.Modes.SequenceEqual(SelectedAddon?.Modes??[]) || !(current.StartupArgs??[]).SequenceEqual(SelectedAddon?.StartupArgs??[]))
                throw new InvalidOperationException("Аддон изменился, повреждён или его SHA-256 не соответствует проверенному контрольному пакету. Обновите каталог.");
            if (!server && _launcher.ArmbandsEnabled && _activeAddonName.Equals(LauncherService.DefaultAddon, StringComparison.OrdinalIgnoreCase))
            {
                preparingArmbands = true;
                ContentAvailability.Text = "Проверяем локальные 3D-повязки…";
                var progress = new Progress<EmissiveBuildProgress>(p =>
                {
                    ContentAvailability.Text = p.Message;
                    SetArmbandsProgress(p);
                });
                var build = await EmissiveWorkshop.EnsureBuiltAsync(_contentRoot!, _dataFolder, progress, _workshopBuildStop.Token);
                SetArmbandsReady(build);
                if (!build.ReferenceHashMatches)
                {
                    var choice = await DecideCosmeticMismatch(build);
                    if (choice == CosmeticDecision.Cancel) return false;
                    if (choice == CosmeticDecision.Without)
                    {
                        RefreshArmbands();
                        AppendLog("Косметика отключена; запуск клиента продолжается с исходным внешним видом.");
                    }
                    else RefreshArmbands();
                }
            }
            AppendLog($"Выбран один аддон: {_activeAddonName}, версия {current.Version}. Другие .addon не передаются движку.");
            return true;
        }
        catch (Exception ex) { if (preparingArmbands) SetArmbandsFailure(ex); ContentAvailability.Text = ex.Message; AppendLog(ex.Message); return false; }
        finally { _verifyingContentLaunch = false; RefreshLaunchControls(); }
    }
    private void UpdateContentAvailability()
    {
        if (ContentAvailability is null) return;
        LoadModWorkspace();
        bool known = SelectedContentMode is "dm" or "coop" or "defence";
        PvpGameplayFields.IsVisible = known; PvpFragFields.IsVisible = SelectedContentMode=="dm";
        ServerTechnicalOptions.IsVisible = known; CoopScenarioPanel.IsVisible=SelectedContentMode=="coop";
        DefenceScenarioPanel.IsVisible=SelectedContentMode=="defence"; ScenarioMinPanel.IsVisible=SelectedContentMode is "coop" or "defence";
        UnverifiedModeInfo.IsVisible = SelectedContentMode!="dm";
        UnverifiedModeInfo.Text = SelectedContentMode switch {
            "coop" => "Штатный сценарий: новая кампания или сохранение. Данные ZoneLink изолированы; оригинальные сохранения копируются. Ограничение числа игроков зависит от кампании. Игровая проверка этой сборки ещё не проведена.",
            "defence" => "Штатная оборона (df), карты и ротация из метаданных аддона. Игровая проверка этой сборки ещё не проведена.",
            _ => "В метаданных нет поддерживаемого сценария запуска." };
        ClientAddonNotice.Text = ContentClientAllowed ? $"Выбран {_activeAddonName}. У друзей должны совпадать аддон, версия и содержимое. QR2 не определяет аддон; автоматическая подмена отключена." : AddonScenarios.ClientReason(SelectedAddon) ?? "Аддон недоступен для выбранного подключения.";
        HomeLaunchReason.IsVisible = !ContentClientAllowed; HomeLaunchReason.Text=ClientAddonNotice.Text;
        ContentAvailability.Text = ServerContentReason() ?? (SelectedContentMode=="dm" ? "✓ PvP: выбранная карта разрешена." : "Готов штатный сценарий оригинального лаунчера. Требуется короткая игровая приёмка.");
        UpdateMapEvidence();
        RefreshLaunchControls();
    }
    private void UpdateMapEvidence()
    {
        if (MapEvidenceText is null) return;
        MapEvidenceText.Text = VerifiedMapChoice.SelectedItem is MapChoice map ? $"{map.Status}. Источник: {map.Source}." : "Выберите карту. Наличие архива не подтверждает поддержку режима или успешную загрузку.";
    }
    private void FilterAddons(object? sender, RoutedEventArgs e) { if (_profileStore is not null) RenderAddonCards(); }
    private void FilterAddonModes(object? sender, SelectionChangedEventArgs e) { if (_profileStore is not null) RenderAddonCards(); }
    private void RenderAddonCards()
    {
        AddonCards.Children.Clear(); foreach (var image in _addonImages) image.Dispose(); _addonImages.Clear();
        var search = AddonSearch.Text ?? ""; var filter = AddonModeFilter.SelectedIndex; int pixelsLeft = 8 * 1024 * 1024;
        foreach (var addon in _addons.OrderByDescending(a => a.FileName == _activeAddonName).ThenBy(a => a.Name).Where(a => (a.Name + " " + a.Author + " " + a.Description + " " + a.FileName).Contains(search, StringComparison.CurrentCultureIgnoreCase))
                     .Where(a => filter switch { 1 => a.Modes.Contains("dm"), 2 => a.Modes.Contains("coop"), 3 => a.Modes.Contains("defence"), 4 => !a.Healthy, _ => true }))
        {
            var text = new StackPanel { Spacing = 6 };
            text.Children.Add(new TextBlock { Text = addon.Name, FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = $"{addon.Author} · {addon.Version}", FontSize = 14, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            text.Children.Add(new TextBlock { Text = addon.Description.Length > 160 ? addon.Description[..160] + "…" : addon.Description, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            if (addon.Description.Length > 160) text.Children.Add(new Expander { Header = "Подробнее", Content = new TextBlock { Text = addon.Description, FontSize = 15, TextWrapping = TextWrapping.Wrap } });
            text.Children.Add(new TextBlock { Text = !addon.Healthy ? addon.Summary : ContentPolicy.VerifiedAddon(addon) ? "✓ PvP: основной вариант для Steam; сравните SHA-256 с друзьями" : ContentPolicy.LocalControlAddon(addon) ? "Локальный контрольный DM: только IP, SHA-256 подтверждён. Для Steam не разрешён." : addon.FileName != addon.FileName.ToLowerInvariant() ? "Имя должно быть в нижнем регистре. Автоматическое переименование не выполняется." : (AddonScenarios.ClientReason(addon) ?? "Штатный запуск · ") + string.Join(", ", addon.Modes.Select(x => new ModeChoice(x).ToString())), FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FFB47D")) });
            if (addon.Warning is not null) text.Children.Add(new TextBlock { Text = addon.Warning, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            var pick = new Button { Content = addon.FileName == _activeAddonName ? "✓ Включён" : "Включить →", FontSize = 15, MinHeight = 42, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, IsEnabled = addon.Healthy && addon.FileName != _activeAddonName && !_running && !_serverRunning && !UpdatesLockGame };
            pick.Classes.Add("primary"); pick.Click += (_, _) => PickContentAddon(addon);
            if (addon.FileName == _activeAddonName && addon.FileName != LauncherService.DefaultAddon)
            {
                var disable = new Button { Content = "Отключить → вернуть PvP", IsEnabled = !_running && !_serverRunning && !UpdatesLockGame };
                disable.Click += (_, _) => { var fallback = _addons.FirstOrDefault(a => a.FileName == LauncherService.DefaultAddon && ContentPolicy.VerifiedAddon(a));
                    if (fallback is not null) PickContentAddon(fallback); else ProfileStatus.Text = "Для безопасного отключения нужен установленный PvP 1.4.7. Файлы не изменены."; };
                text.Children.Add(disable);
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto"), ColumnSpacing = 16 };
            Control iconControl = new TextBlock { Text = "◈", FontSize = 40, Foreground = new SolidColorBrush(Color.Parse("#FFB47D")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            if (addon.Icon is not null && addon.IconPixels <= pixelsLeft)
            {
                try { using var data = new MemoryStream(addon.Icon); var bitmap = Bitmap.DecodeToWidth(data, 144); _addonImages.Add(bitmap); pixelsLeft -= addon.IconPixels; iconControl = new Image { Source = bitmap, Width = 72, Height = 72, Stretch = Stretch.UniformToFill }; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
            }
            iconControl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            grid.Children.Add(iconControl); Grid.SetColumn(text, 1); grid.Children.Add(text); Grid.SetColumn(pick, 2); grid.Children.Add(pick);
            AddonCards.Children.Add(new Border { Child = grid, MaxWidth = 1120, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(16), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse(addon.FileName == _activeAddonName ? "#FFB47D" : "#304963")), Background = new SolidColorBrush(Color.Parse("#E81B2633")) });
        }
        if (AddonCards.Children.Count == 0) AddonCards.Children.Add(new TextBlock { Text = "Аддоны не найдены или скрыты фильтром.", TextWrapping = TextWrapping.Wrap });
    }
}


