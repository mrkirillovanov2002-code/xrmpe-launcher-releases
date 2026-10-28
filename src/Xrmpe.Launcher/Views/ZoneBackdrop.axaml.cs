using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
namespace Xrmpe.Launcher.Views;
public partial class ZoneBackdrop : UserControl
{
    private static readonly Lazy<Bitmap?> Scene = new(() => {
        try { using var data = AssetLoader.Open(new Uri("avares://Xrmpe.Launcher/Assets/zonelink-industrial-sunset.png")); return new Bitmap(data); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return null; }
    });
    public ZoneBackdrop() { InitializeComponent(); SceneImage.Source = Scene.Value; }
}
