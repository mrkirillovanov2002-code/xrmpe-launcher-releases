using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private bool _creatingGame, _serverStopRequested, _closingServer, _closeApproved;
    private CancellationTokenSource? _hostCancel;
    private Task<int>? _ownedServerTask;
    private TaskCompletionSource? _hostCompletion;

    private async Task StopOwnedServer()
    {
        _serverStopRequested = true;
        _hostCancel?.Cancel();
        ServerStatus.Text = "Останавливаем свой сервер…";
        RefreshLaunchControls();
        await _launcher.StopServerAsync();
        if (_hostCompletion is { } done) await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
        ServerStatus.Text = "Сервер остановлен";
    }

    private void JoinGame(object? sender, RoutedEventArgs e)
    {
        if (_serverStarting || (_creatingGame && !_serverRunning)) return;
        if (_serverRunning) { Launch(sender, e); return; }
        if (NetworkMode.SelectedIndex == 1) LaunchSteamMenu(sender, e);
        else if (_interfacePreferences.AdvancedIp) Navigate(new Button { Tag = "2" }, e);
    }

    private void AdvancedIpChanged(object? sender, RoutedEventArgs e)
    {
        if (!_modernReady) return;
        StoreInterface(_interfacePreferences with { AdvancedIp = AdvancedIpToggle.IsChecked == true });
        ApplyIpVisibility();
    }

    private void ApplyIpVisibility()
    {
        var enabled = _interfacePreferences.AdvancedIp;
        if (!enabled) NetworkMode.SelectedIndex = 1;
        IpChoice.IsVisible = BrowserIpChoice.IsVisible = ManualIpExpander.IsVisible = enabled;
        IpConnectPanel.IsVisible = enabled && NetworkMode.SelectedIndex != 1;
        // Existing LAN/catalogue pages remain available; IP connection stays an explicit opt-in.
    }
}
