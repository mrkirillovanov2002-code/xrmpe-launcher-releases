using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Xrmpe.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.Opened += (_, _) => MainWindow.ConfirmUpdatedWindow(desktop.Args);
            if (desktop.Args?.Contains("--choose-game") == true)
                window.Opened += (_, _) => window.ChooseGameFolderAfterInstall();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
