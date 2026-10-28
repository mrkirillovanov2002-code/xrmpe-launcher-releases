using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private bool _modernReady, _syncingTransport;
    private readonly bool _freshProfile;
    private readonly Dictionary<Control, CancellationTokenSource> _motion = new();
    private readonly Dictionary<Button, Flyout> _helpFlyouts = new();
    private int MotionLevel => ReducedMotionRequested() ? 0 : _interfacePreferences.Animations;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
    private static bool ReducedMotionRequested() => OperatingSystem.IsWindows() && SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled == 0;

    private void InitializeModern()
    {
        AnimationMode.SelectedIndex = _interfacePreferences.Animations;
        NetworkMode.SelectedIndex = _interfacePreferences.AdvancedIp ? (_interfacePreferences.NetworkChoice ?? 1) : 1;
        AdvancedIpToggle.IsChecked = _interfacePreferences.AdvancedIp;
        BrowserTransport.SelectedIndex = NetworkMode.SelectedIndex;
        ServerTransportSummary.Text = NetworkMode.SelectedIndex == 1 ? "Подключение: Steam — приглашения" : "Подключение: по IP";
        NetworkModeChanged(null, null!);
        _modernReady = true;
        SyncConnectionChoices(); UpdateZoneReadiness(); ApplyIpVisibility();
        UpdateServerScenario();
        InitializePolish();
        InitializeZoneHelpDismissal();
        if (_freshProfile) StoreInterface(_interfacePreferences with { NetworkChoice = NetworkMode.SelectedIndex });
        foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("help")))
        {
            if (ToolTip.GetTip(button) is not string brief) continue;
            var tooltip = new ToolTip { Content = new TextBlock { Text = brief, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 330 } };
            tooltip.AttachedToVisualTree += async (_, _) => await AnimateModern(tooltip, true);
            ToolTip.SetTip(button, tooltip);
        }
        ApplyModernMotion();
        Opened += (_, _) => ApplyModernMotion();
        SizeChanged += (_, _) => ArrangeHybridBrowser();
        ArrangeHybridBrowser();
        Closed += (_, _) => { _helpFlyouts.Clear(); var active = _motion.Values.ToArray(); _motion.Clear(); foreach (var cancellation in active) { cancellation.Cancel(); cancellation.Dispose(); } };
    }
    private void ArrangeHybridBrowser()
    {
        if (BrowserHybrid is null) return;
        bool narrow = Bounds.Width < 1220;
        BrowserHybrid.ColumnDefinitions = new(narrow ? "*" : "2*,*");
        Grid.SetColumn(BrowserDetailPanel, narrow ? 0 : 1);
        Grid.SetRow(BrowserDetailPanel, narrow ? 1 : 0);
        ServerList.Height = narrow ? 260 : 360;
    }
    private void AnimationModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_modernReady || AnimationMode.SelectedIndex is < 0 or > 2) return;
        if (!StoreInterface(_interfacePreferences with { Animations = AnimationMode.SelectedIndex })) AnimationMode.SelectedIndex = _interfacePreferences.Animations;
        ApplyModernMotion();
    }
    private void ApplyModernMotion()
    {
        if (!_modernReady) return;
        var level = MotionLevel;
        Classes.Set("motionOff", level == 0);
        Transitions? HoverTransitions() => level == 0 ? null : new Transitions
        {
            new BrushTransition { Property = TemplatedControl.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(level == 1 ? 120 : 180) },
            new BrushTransition { Property = TemplatedControl.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(level == 1 ? 120 : 180) }
        };
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            button.Transitions = HoverTransitions();
            foreach (var presenter in button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Where(x => x.Name == "PART_ContentPresenter")) presenter.Transitions = HoverTransitions();
        }
        if (level == 0) foreach (var pair in _motion.ToArray()) { pair.Value.Cancel(); pair.Key.Opacity = 1; }
        MotionStatus.Text = ReducedMotionRequested() ? "Анимации отключены системной настройкой Windows уменьшения движения." :
            level == 0 ? "Все переходы выполняются мгновенно." : level == 1 ? "120 мс: переходы страниц и подсветка кнопок." : "180 мс: страницы, вкладки, выбор сервера и карточки подсказок.";
    }
    private async Task AnimateModern(Control target, bool fullOnly = false)
    {
        if (!_modernReady || MotionLevel == 0 || fullOnly && MotionLevel < 2) return;
        if (_motion.Remove(target, out var previous)) { previous.Cancel(); previous.Dispose(); }
        using var cancellation = new CancellationTokenSource(); _motion[target] = cancellation;
        try
        {
            var animation = new Animation { Duration = TimeSpan.FromMilliseconds(MotionLevel == 1 ? 120 : 180), Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.55d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }};
            await animation.RunAsync(target, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        finally { if (_motion.TryGetValue(target, out var active) && active == cancellation) _motion.Remove(target); target.Opacity = 1; }
    }
    private void BrowserTransportChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_modernReady || _syncingTransport) return;
        NetworkMode.SelectedIndex = BrowserTransport.SelectedIndex;
        RefreshBrowserSelection();
    }
    private void PersistTransportChoice()
    {
        if (!_modernReady || _syncingTransport) return;
        ServerTransportSummary.Text = NetworkMode.SelectedIndex == 1 ? "Подключение: Steam — приглашения" : "Подключение: по IP";
        _syncingTransport = true;
        try { BrowserTransport.SelectedIndex = NetworkMode.SelectedIndex; }
        finally { _syncingTransport = false; }
        StoreInterface(_interfacePreferences with { NetworkChoice = NetworkMode.SelectedIndex });
        SyncConnectionChoices();
        UpdateServerScenario();
        RefreshBrowserSelection();
    }
    private bool BrowserTransportAllowed(BrowserServer item) => (NetworkMode.SelectedIndex == 1) == (item.Kind == "Steam");
    private void PresentSelectedServer(BrowserServer? item)
    {
        if (SelectedServerName is null) return;
        SelectedServerName.Text = item?.Name ?? "Выберите сервер";
        SelectedServerFacts.Text = item is null ? "Выберите строку слева, чтобы увидеть сведения и способы подключения." :
            $"{item.Kind} · {item.Address}\nРежим: {item.Mode}\nКарта: {item.Map}\nИгроки: {item.PlayerText} · Пинг: {item.PingText}\n{item.Availability}";
        SelectedServerCompatibility.Text = "Совместимость аддона не проверена. Автовыбор отключён.";
        BrowserConnectionReason.IsVisible = item is not null && (!ContentClientAllowed || !BrowserTransportAllowed(item) || item.Kind == "Steam" || item.Snapshot is null);
        BrowserConnectionReason.Text = item is null ? "" : !ContentClientAllowed ? DescribeReadiness(false).Message :
            !BrowserTransportAllowed(item) ? item.Kind == "Steam" ? "Выберите Steam. Этот сервер принимает приглашения внутри игры." : "Для этого адреса используйте «По IP». Поддержка Steam-приглашения не подтверждена." :
            item.Kind == "Steam" ? "Будет открыто меню Steam-клиента. Для вступления примите приглашение друга в оверлее." :
            item.Snapshot is null ? "Сервер не подтвердил доступность. Он может быть выключен или не отвечать на запрос сведений. Перед попыткой подключения потребуется подтверждение." : "Сервер ответил. Совместимость игрового содержимого не подтверждена.";
        SetReadinessAction(BrowserFixButton, item is null ? new(true, "") : DescribeReadiness(false));
        BrowserConnectButton.Content = item?.Kind == "Steam" ? "Открыть Steam-клиент" : "Подключиться";
        if (_verifyingContentLaunch) BrowserConnectButton.IsEnabled = false;
        BrowserEmptyState.IsVisible = !ServerList.Items.Cast<object>().Any();
    }
    private void ShowContextHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string text) return;
        if (button.Flyout is Flyout existing && existing.IsOpen) { existing.Hide(); return; }
        if (!_helpFlyouts.TryGetValue(button, out var flyout))
        {
            var body = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360, FontSize = 15, Margin = new Thickness(8) };
            flyout = new Flyout { Content = body, Placement = PlacementMode.Right };
            flyout.Opened += async (_, _) => await AnimateModern(body, true);
            flyout.Closed += (_, _) => { button.Flyout = null; };
            _helpFlyouts.Add(button, flyout);
        }
        button.Flyout = flyout; flyout.ShowAt(button);
    }
    private void ToggleJournal(object? sender, RoutedEventArgs e)
    { RichLogList.IsVisible = !RichLogList.IsVisible; JournalToggle.Content = RichLogList.IsVisible ? "Свернуть журнал" : "Показать журнал"; RenderVisibleLog(); _ = AnimateModern(RichLogList, true); }
}
