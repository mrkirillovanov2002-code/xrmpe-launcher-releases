using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher;
public partial class MainWindow
{
    private bool _loadingArmbands, _installingArmbands;
    private string? _armbandsBuildMessage, _armbandsBuildError;
    private int _armbandsBuildPercent;
    private bool _armbandsBuilt, _armbandsHashWarning;
    private string ArmbandsPackage => EmissiveWorkshop.InstalledPath(_dataFolder);

    private void RefreshArmbands()
    {
        if (ArmbandsToggle is null || _modWorkspaces is null) return;
        _loadingArmbands = true;
        try
        {
            var pvp = _contentReady && _contentRoot is not null && ContentPolicy.VerifiedAddon(SelectedAddon);
            var state = pvp ? _modWorkspaces.Load(_contentRoot!, LauncherService.DefaultAddon) : ModWorkspace.Empty;
            // A former DDS preference is deliberately not applied to the new 3D option.
            var enabled = state.ArmbandsEnabled && state.ArmbandsVariant == "emissive";
            var idle = !_running && !_serverRunning && !_creatingGame && !UpdatesLockGame && !_installingArmbands;
            if (!_installingArmbands) ArmbandsToggle.IsChecked = enabled;
            ArmbandsToggle.IsEnabled = pvp && idle;
            _launcher.ArmbandsEnabled = pvp && enabled;
            _launcher.ArmbandsPackage = ArmbandsPackage;
            _launcher.ArmbandsVariant = "emissive";
            _launcher.ArmbandsApprovedHash = enabled && state.ArmbandsApprovalBuilderHash == EmissiveWorkshop.BuilderZipHash &&
                state.ArmbandsApprovedHash.Length == 64 ? state.ArmbandsApprovedHash : null;
            ArmbandsStatus.Text = !pvp ? "Доступно только для установленного xrMPE PvP 1.4.7." :
                _armbandsBuildError is not null ? "Ошибка подготовки: " + _armbandsBuildError :
                _armbandsBuildMessage is not null ? _armbandsBuildMessage :
                enabled && _armbandsHashWarning ? "3D-повязки собраны локально; SHA отличается от эталона. Можно повторить сборку или выключить повязки." :
                enabled && _armbandsBuilt ? "3D-повязки готовы · Появятся при следующем запуске PvP." :
                enabled ? "ВКЛ · Локальный пакет будет проверен перед запуском PvP." :
                "ВЫКЛ · Исходный вид костюмов. Включение соберёт локальный пакет автоматически.";
            ArmbandsProgress.IsVisible = _armbandsBuildMessage is not null && _armbandsBuildPercent < 100;
            ArmbandsProgress.Value = _armbandsBuildPercent;
            ArmbandsRetry.IsVisible = pvp && (_armbandsBuildError is not null || _armbandsHashWarning) && !_installingArmbands;
            ArmbandsOpenLog.IsVisible = _armbandsBuildError is not null || _armbandsHashWarning;
            RefreshWorkshopCardStates();
        }
        finally { _loadingArmbands = false; }
    }

    private async void ArmbandsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingArmbands || _running || _serverRunning || _creatingGame || UpdatesLockGame || _installingArmbands) return;
        if (_contentRoot is null || !ContentPolicy.VerifiedAddon(SelectedAddon)) { RefreshArmbands(); return; }
        var enabled = ArmbandsToggle.IsChecked == true;
        _installingArmbands = enabled;
        RefreshArmbands();
        try
        {
            _armbandsBuildError = null;
            if (enabled)
            {
                var progress = new Progress<EmissiveBuildProgress>(SetArmbandsProgress);
                var build = await EmissiveWorkshop.EnsureBuiltAsync(_contentRoot, _dataFolder, progress, _workshopBuildStop.Token);
                ReloadWorkshopImages();
                SetArmbandsReady(build);
                if (!build.ReferenceHashMatches)
                {
                    var choice = await DecideCosmeticMismatch(build);
                    if (choice == CosmeticDecision.Cancel) { RefreshArmbands(); return; }
                    if (choice == CosmeticDecision.Without) enabled = false;
                }
            }
            else { _armbandsBuildMessage = null; _armbandsBuilt = false; }
            SaveModNotesIfChanged();
            var next = _modWorkspaces.Load(_contentRoot, LauncherService.DefaultAddon) with
            { ArmbandsEnabled = enabled, ArmbandsVariant = "emissive" };
            _modWorkspaces.Save(_contentRoot, LauncherService.DefaultAddon, next);
            _visibleModWorkspace = next;
            NotifyWorkshop(enabled ? "3D-повязки включены для следующего запуска PvP. Выбор сохранён." :
                "Повязки отключены. Следующий запуск использует исходный вид костюмов.");
        }
        catch (OperationCanceledException) when (_workshopBuildStop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetArmbandsFailure(ex);
            NotifyWorkshop("Не удалось включить повязки: " + ex.Message, true);
        }
        finally { _installingArmbands = false; RefreshArmbands(); }
    }

    private void SetArmbandsProgress(EmissiveBuildProgress progress)
    {
        if (_workshopBuildStop.IsCancellationRequested) return;
        if (progress.Message.Contains("SHA-256", StringComparison.Ordinal) || progress.Message.Contains("пересборки", StringComparison.Ordinal))
            AppendLog("Косметика: " + progress.Message);
        _armbandsBuildMessage = progress.Message;
        _armbandsBuildPercent = Math.Clamp(progress.Percent, 0, 100);
        _armbandsBuildError = null;
        RefreshArmbands();
    }

    private void SetArmbandsReady(EmissiveBuildResult? result = null)
    {
        _armbandsBuilt = true;
        if (result is not null) _armbandsHashWarning = !result.ReferenceHashMatches;
        _armbandsBuildError = null;
        _armbandsBuildMessage = _armbandsHashWarning ? null : "3D-повязки готовы.";
        _armbandsBuildPercent = 100;
        AppendLog("Локальный DB и 120 предпросмотров 3D-повязок проверены; сравнение с эталонным SHA выполняется отдельно.");
        RefreshArmbands();
    }

    private void SetArmbandsFailure(Exception ex)
    {
        _armbandsBuildMessage = null;
        _armbandsBuildPercent = 0;
        _armbandsBuildError = ex.Message;
        AppendLog("Подготовка 3D-повязок: " + ex);
        RefreshArmbands();
    }

    private void RetryArmbands(object? sender, RoutedEventArgs e)
    {
        if (_armbandsHashWarning && _contentRoot is not null && !_installingArmbands)
        {
            _ = RebuildArmbands();
            return;
        }
        _armbandsBuildError = null;
        RefreshArmbands();
        if (_contentRoot is null) return;
        if (ArmbandsToggle.IsChecked == true) _ = LoadLocalWorkshopPreviews();
        else ArmbandsToggle.IsChecked = true;
    }

    private async Task RebuildArmbands()
    {
        if (_contentRoot is null || _installingArmbands || _running || _serverRunning) return;
        _installingArmbands = true;
        RefreshArmbands();
        try
        {
            var progress = new Progress<EmissiveBuildProgress>(SetArmbandsProgress);
            var build = await EmissiveWorkshop.EnsureBuiltAsync(_contentRoot, _dataFolder, progress, _workshopBuildStop.Token, forceRebuild: true);
            ReloadWorkshopImages();
            SetArmbandsReady(build);
            if (!build.ReferenceHashMatches) await DecideCosmeticMismatch(build);
        }
        catch (OperationCanceledException) when (_workshopBuildStop.IsCancellationRequested) { }
        catch (Exception ex) { SetArmbandsFailure(ex); }
        finally { _installingArmbands = false; RefreshArmbands(); }
    }

    private void OpenArmbandsLog(object? sender, RoutedEventArgs e)
    {
        if (!RichLogList.IsVisible) ToggleJournal(sender, e);
    }

    private async Task<CosmeticDecision> DecideCosmeticMismatch(EmissiveBuildResult build)
    {
        AppendLog($"Косметика: локальный DB; ожидаемый SHA-256 {LocalArmbandsProfiles.EmissiveHash}; фактический {build.ActualHash}; размер {new FileInfo(build.DbPath).Length} байт; источник {(build.FromCache ? "проверенный кеш сборщика" : "новая сборка")}. Критические проверки не изменены.");
        if (!EmissiveWorkshop.IsTrustedLocalBuild(build.DbPath, build.ActualHash))
            throw new InvalidDataException("Для несовпадающего DB нет подтверждения штатной локальной сборки или состав архива небезопасен. Включение запрещено.");
        var state = _modWorkspaces.Load(_contentRoot!, LauncherService.DefaultAddon);
        if (state.ArmbandsApprovedHash == build.ActualHash && state.ArmbandsApprovalBuilderHash == EmissiveWorkshop.BuilderZipHash)
            return CosmeticDecision.Include;

        var dialog = CreateCosmeticMismatchDialog(build);
        var decision = await dialog.ShowDialog<CosmeticDecision>(this);
        AppendLog($"Косметика: решение при несовпадении SHA — {decision}.");
        if (decision != CosmeticDecision.Cancel)
        {
            var next = CosmeticChoicePolicy.Apply(state, decision, build.ActualHash);
            _modWorkspaces.Save(_contentRoot!, LauncherService.DefaultAddon, next);
            if (_modGame == _contentRoot && _modAddon == LauncherService.DefaultAddon)
                _visibleModWorkspace = _visibleModWorkspace with
                { ArmbandsEnabled = next.ArmbandsEnabled, ArmbandsApprovedHash = next.ArmbandsApprovedHash,
                    ArmbandsApprovalBuilderHash = next.ArmbandsApprovalBuilderHash, ArmbandsVariant = next.ArmbandsVariant };
        }
        return decision;
    }

    private static Window CreateCosmeticMismatchDialog(EmissiveBuildResult build)
    {
        var dialog = new Window { Title = "Проверка косметических повязок", Width = 640, Height = 285,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var without = new Button { Content = "Продолжить без повязок", MinWidth = 180 };
        var include = new Button { Content = "Всё равно включить", MinWidth = 150 };
        var cancel = new Button { Content = "Отмена", MinWidth = 90 };
        without.Click += (_, _) => dialog.Close(CosmeticDecision.Without);
        include.Click += (_, _) => dialog.Close(CosmeticDecision.Include);
        cancel.Click += (_, _) => dialog.Close(CosmeticDecision.Cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(22), Spacing = 16, Children =
        {
            new TextBlock { Text = "Не удалось подтвердить эталонный SHA-256 локально собранных 3D-повязок. Штатный сборщик проверил игровые источники, но внешний вид может отличаться или работать некорректно. Вы можете включить их на свой риск. Это предупреждение относится только к необязательной косметике; подписи обновлений и проверка аддона не обходятся.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = $"SHA-256 пакета: {build.ActualHash}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { without, include, cancel } }
        } };
        return dialog;
    }
}
