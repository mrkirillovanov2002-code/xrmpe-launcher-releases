using Avalonia;
using System.Runtime.InteropServices;

namespace Xrmpe.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Presence marker for Inno Setup; it does not prevent multiple launcher windows.
        using var installMarker = OperatingSystem.IsWindows() ? new Mutex(false, @"Local\xrMPE.CustomLauncher.Running") : null;
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID("xrMPE.CustomLauncher");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
