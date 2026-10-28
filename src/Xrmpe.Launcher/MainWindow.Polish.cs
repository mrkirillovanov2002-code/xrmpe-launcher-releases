using Avalonia;
using Avalonia.Controls;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private void InitializePolish()
    {
        SizeChanged += (_, _) => ArrangePolish();
        ServerStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) PresentHomeAvailability(); };
        ServerList.PropertyChanged += (_, e) => { if (e.Property == ItemsControl.ItemsSourceProperty) PresentHomeAvailability(); };
        ArrangePolish(); PresentHomeAvailability();
    }
    private void ArrangePolish()
    {
        if (HomeHero is null) return;
        var height = Bounds.Height;
        bool compact = Bounds.Width < 1220;
        double hero = compact || height < 1000 ? 390 : 430;
        if (HomeHero.Height != hero) HomeHero.Height = hero;
        var margin = new Thickness(0, compact ? 0 : Math.Clamp((height - 1040) * .16, 0, 64), 0, 0);
        if (PlayPage.Margin != margin) PlayPage.Margin = margin;
    }
    private void PresentHomeAvailability()
    {
        // Read existing results only. This presentation code sends no query and infers no NAT/Steam reachability.
        bool reply = _serverRunning && _launcher.IsServerProcessRunning && _activeServerPort is int port &&
            _browserServers.Any(s => s.Endpoint.Host == "127.0.0.1" && s.Endpoint.Port == port && s.Snapshot is not null);
        HomeServerAvailability.Text = reply
            ? "Последняя проверка: получен ответ QR2. Доступность из Интернета этим не подтверждена."
            : "Доступность не подтверждена. Проверить можно в списке серверов.";
    }
}
