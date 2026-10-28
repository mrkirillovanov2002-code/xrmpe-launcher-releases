using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Text.Json;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Views;

internal static class PolishChecks
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs(); }
    static void Assert(bool test, string why) { if (!test) throw new Exception("Polish: " + why); }
    public static void Run(string output)
    {
        var w = new MainWindow(); DeveloperChecks.Prepare(w); w.Show(); Pump();
        foreach (var (width, height, hero) in new[] { (1000,740,300), (1320,900,300), (1920,1080,390), (2560,1440,470) })
        {
            w.Width = width; w.Height = height; Pump();
            Assert(w.FindControl<StackPanel>("PlayPage")!.Bounds.Width <= 1320.1, "home width cap");
            Assert(w.FindControl<StackPanel>("PageContent")!.Bounds.Width <= 1440.1, "all-page width cap");
            Assert(w.FindControl<Border>("HomeHero")!.Height == hero, "responsive hero");
            Assert(w.FindControl<Grid>("HomeStatusGrid")!.Bounds.Width <= 1320.1, "no giant status cards");
        }
        Assert(w.FindControl<TextBlock>("HomeServerName")!.FontSize == 22, "larger server title");
        Assert(w.FindControl<TextBlock>("HomeServerAvailability")!.Text!.Contains("не подтверждена"), "no fabricated availability");
        w.Width = 1320; w.Height = 900; Pump();
        int layouts = 0; EventHandler handler = (_, _) => layouts++;
        w.LayoutUpdated += handler;
        var idle = Stopwatch.StartNew(); while (idle.ElapsedMilliseconds < 1000) { Pump(); Thread.Sleep(20); }
        w.LayoutUpdated -= handler;
        Assert(layouts < 20, "no constant layout cycle while idle");
        var backdrops = w.GetVisualDescendants().OfType<ZoneBackdrop>().ToArray();
        var cards = w.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToArray();
        var original = cards.Select(b => b.Background).ToArray();
        double Sample()
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(1320,900), new Vector(96,96));
            bitmap.Render(w); var clock = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++) bitmap.Render(w);
            return clock.Elapsed.TotalMilliseconds / 12;
        }
        var full = Sample();
        foreach (var backdrop in backdrops) backdrop.IsVisible = false; Pump(); var noBackdrop = Sample();
        foreach (var backdrop in backdrops) backdrop.IsVisible = true;
        foreach (var card in cards) card.Background = Brush.Parse("#1B2633"); Pump(); var opaque = Sample();
        for (int i = 0; i < cards.Length; i++) cards[i].Background = original[i];
        File.WriteAllText(Path.Combine(output,"Polish-render-cost.json"), JsonSerializer.Serialize(new { idleLayoutPasses1s=layouts, fullRasterMs=full, hiddenBackgroundRasterMs=noBackdrop, opaqueCardsRasterMs=opaque, decodedSourceRgbaBytes=1672L*941*4, scope="Headless offscreen full-tree raster cost, 12 renders per case. Not native FPS/GPU or idle CPU. Hidden/opaque controls exist only in this isolated test window." }, new JsonSerializerOptions { WriteIndented=true }));
        w.Close();
        Console.WriteLine("PASS: responsive composition caps/hero heights, larger typography, honest unconfirmed reachability, idle layout guard; background/transparency offscreen costs measured without changing production settings.");
    }
}
