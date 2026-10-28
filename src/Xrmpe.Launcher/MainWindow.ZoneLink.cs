using Avalonia.Controls;
using Avalonia.Interactivity;
using Xrmpe.Launcher.Content;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private void InitializeZoneHelpDismissal()
    {
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            var open = _helpFlyouts.Values.Where(x => x.IsOpen).ToArray();
            foreach (var flyout in open) flyout.Hide();
            if (open.Length != 0) e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            foreach (var pair in _helpFlyouts.Where(x => x.Value.IsOpen).ToArray())
            {
                if (e.Source is Avalonia.Visual source && (pair.Key == source || pair.Key.IsVisualAncestorOf(source) ||
                    pair.Value.Content is Avalonia.Visual content && (content == source || content.IsVisualAncestorOf(source)))) continue;
                pair.Value.Hide();
            }
        }, RoutingStrategies.Tunnel, true);
    }
    private sealed record LaunchReadiness(bool Ready, string Message, string Action = "", string Destination = "");
    private LaunchReadiness DescribeReadiness(bool server)
    {
        if (!_contentReady) return _contentScan is not null
            ? new(false, "Проверяем игровой контент…")
            : new(false, "Игровая папка не готова. Выберите папку установленной игры.", "Выбрать папку игры", "settings");
        if (SelectedAddon is null) return new(false, "Аддон не выбран или не найден в игровой папке.", "Выбрать аддон", "content");
        if (!SelectedAddon.Healthy) return new(false, "Не удалось прочитать выбранный аддон. Проверьте его в игровом контенте.", "Открыть игровой контент", "content");
        if (!ContentPolicy.CanLaunch(SelectedAddon, SelectedSteam)) return new(false, AddonScenarios.ClientReason(SelectedAddon) ?? "Этот аддон недоступен для выбранного подключения.", "Выбрать аддон", "content");
        if(server && ServerContentReason() is {} reason)return new(false,reason,"Настроить сервер","map");
        return new(true, server ? (SelectedContentMode=="dm" ? "Готов к запуску PvP." : "Готов штатный сценарий; игровая приёмка этой сборки не проведена.") : "Клиент готов. У участников должны совпадать аддон и версия.");
    }
    private static void SetReadinessAction(Button button, LaunchReadiness state)
    { button.IsVisible = !state.Ready && state.Destination.Length != 0; button.Content = state.Action; button.Tag = state.Destination; }
    private void UpdateZoneReadiness()
    {
        if (!_modernReady) return;
        var client = DescribeReadiness(false); var server = DescribeReadiness(true);
        HomeReadinessCard.IsVisible = HomeLaunchReason.IsVisible = !client.Ready;
        HomeLaunchReason.Text = ClientAddonNotice.Text = client.Message;
        ConnectionReadinessCard.IsVisible = !client.Ready;
        SetReadinessAction(HomeFixButton, client); SetReadinessAction(ConnectionFixButton, client);
        ContentAvailability.Text = server.Message; SetReadinessAction(ServerFixButton, server);
        HomeLastServer.Text = _addresses.FirstOrDefault() is { } last
            ? $"Последний IP-адрес: {last.Host}:{last.Port}" : "Последних IP-подключений пока нет";
    }
    private void OpenReadinessAction(object? sender, RoutedEventArgs e)
    {
        var destination = (sender as Button)?.Tag as string ?? "content";
        var page = destination switch { "settings" => "3", "mode" or "map" => "1", _ => "6" };
        Navigate(new Button { Tag = page }, e);
        if (destination == "mode") { ContentMode.BringIntoView(); ContentMode.Focus(); }
        if (destination == "map") { VerifiedMapChoice.BringIntoView(); VerifiedMapChoice.Focus(); }
    }
    private void ChooseConnectionMethod(object? sender, RoutedEventArgs e)
    {
        if (!NetworkMode.IsEnabled || sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var choice) || choice is < 0 or > 1) return;
        if (choice == 0 && !_interfacePreferences.AdvancedIp) return;
        NetworkMode.SelectedIndex = choice;
        SyncConnectionChoices();
    }
    private void SyncConnectionChoices()
    {
        if (SteamChoice is null) return;
        var steam = NetworkMode.SelectedIndex == 1;
        foreach (var button in new[] { SteamChoice, BrowserSteamChoice })
        { button.Classes.Set("selected", steam); button.SetValue(Avalonia.Automation.AutomationProperties.HelpTextProperty, steam ? "Steam выбран" : "Выбрать Steam"); }
        foreach (var button in new[] { IpChoice, BrowserIpChoice })
        { button.Classes.Set("selected", !steam); button.SetValue(Avalonia.Automation.AutomationProperties.HelpTextProperty, steam ? "Выбрать подключение по IP" : "Подключение по IP выбрано"); }
    }
}
