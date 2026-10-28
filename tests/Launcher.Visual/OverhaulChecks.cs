using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Presentation;
using Xrmpe.Launcher.Views;

internal static class OverhaulChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Assert(bool value, string why) { if (!value) throw new Exception("Overhaul: " + why); }
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs(); }
    static T C<T>(MainWindow w, string name) where T : Control => w.FindControl<T>(name)!;
    static object? Call(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    static void Capture(Window w, string output, string name) { Pump(); using var frame = w.CaptureRenderedFrame()!; frame.Save(Path.Combine(output, name + ".png")); }
    public static void Run(string root, string output)
    {
        var absent = ServerFailure.FromException(new InvalidOperationException("Сначала запустите Steam и войдите в свой аккаунт."));
        Assert(absent.CanOpenSteam, "actual Steam preflight classified");
        Assert(!ServerFailure.FromExit(-1).CanOpenSteam, "exit -1 is not a diagnosis");
        Assert(!ServerFailure.FromException(new InvalidOperationException("Steam: unknown error")).CanOpenSteam, "no guessed diagnosis");
        var w = new MainWindow { Width = 1920, Height = 1080 };
        DeveloperChecks.Prepare(w); w.Show(); Pump();
        ((DispatcherTimer)typeof(MainWindow).GetField("_logTimer", Flags)!.GetValue(w)!).Stop();
        foreach (var button in w.GetVisualDescendants().OfType<Button>()) button.Transitions = null;
        string[] pages = ["PlayPage", "ServerPage", "ConnectPage", "SettingsPage", "UpdatesPage", "BrowserPage", "AddonsPage"];
        foreach (var page in pages) C<Control>(w, page).IsVisible = page == "ServerPage";
        foreach (var button in w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav"))) button.Classes.Set("selected", button.Name == "ServerNav");
        C<TextBlock>(w, "PageTitle").Text = "Создать сервер";
        Call(w, "ShowServerFailure", absent);
        Assert(C<Control>(w, "ServerFailurePanel").IsVisible && C<Button>(w, "OpenSteamAction").IsVisible, "missing Steam actions");
        Assert(C<TextBlock>(w, "ServerStatus").Text == "Сервер не работает", "no false running state");
        C<ScrollViewer>(w, "PageScroll").Offset = default;
        Capture(w, output, "Overhaul-SteamMissing-UI-FIXTURE-1920x1080");
        Call(w, "ShowServerFailure", ServerFailure.FromExit(-1));
        Assert(!C<Button>(w, "OpenSteamAction").IsVisible && C<TextBlock>(w, "ServerFailureReason").Text!.Contains("Причина не определена"), "unknown exit retains truth");
        Capture(w, output, "Overhaul-ExitMinus1-UI-FIXTURE-1920x1080");
        C<ListBox>(w, "RichLogList").IsVisible = true;
        Call(w, "RenderRichJournal", "[12:00:00] Готово\n[12:00:01] Warning: тест\n[12:00:02] Ошибка: тест");
        var rows = C<ListBox>(w, "RichLogList").ItemsSource!.Cast<object>().ToArray();
        Assert(rows.Length == 3 && rows.Select(r => r.GetType().GetProperty("Color")!.GetValue(r)!.ToString()).Distinct().Count() == 3, "three severity colors");
        var clock = Stopwatch.StartNew();
        Call(w, "RenderRichJournal", string.Join('\n', Enumerable.Range(0, 100000).Select(i => $"[{i}] сообщение")));
        Pump();
        Assert(C<ListBox>(w, "RichLogList").ItemCount == 1000, "100k stream retains 1000 rows");
        Assert(C<ListBox>(w, "RichLogList").GetVisualDescendants().OfType<ListBoxItem>().Count() < 100, "journal virtualized");
        File.WriteAllText(Path.Combine(output, "Overhaul-performance.json"), JsonSerializer.Serialize(new { journal100kInputToBoundedRenderMs = clock.Elapsed.TotalMilliseconds, retainedRows = 1000 }, new JsonSerializerOptions { WriteIndented = true }));
        w.Close();
        ExportLogos(output);
        Console.WriteLine("PASS: actual Steam-preflight/unknown exit diagnosis, truthful failed-server state, 100k-input bounded/virtual journal and severity colors; three vector logo PNGs exported. Error screenshots are injected UI fixtures, not a Steam session.");
    }
    public static void ExportLogos(string output)
    {
        ExportLogo(new ZoneMark(), 512, 512, output, "ZoneLink-logo-compact");
        var full = new Grid { ColumnDefinitions = new ColumnDefinitions("170,650"), Background = Brushes.Transparent };
        full.Children.Add(new ZoneMark { Margin = new Thickness(12) });
        var word = new ZoneWordmark { Margin = new Thickness(20,40,20,40) }; Grid.SetColumn(word, 1); full.Children.Add(word);
        ExportLogo(full, 820, 170, output, "ZoneLink-logo-full");
        ExportLogo(new ZoneMark(), 512, 512, output, "ZoneLink-logo-monochrome", true);
    }
    static void ExportLogo(Control content, int width, int height, string output, string name, bool mono = false)
    {
        var window = new Window { Width = width, Height = height, Background = Brushes.Transparent, Content = content };
        window.Show(); Pump();
        if (mono)
        {
            foreach (var shape in content.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Shape>())
            {
                if (shape is Avalonia.Controls.Shapes.Ellipse) shape.Fill = new SolidColorBrush(Color.Parse("#101010"));
                else if (shape.Fill != null) shape.Fill = new SolidColorBrush(Color.Parse(shape.Fill.ToString() == "#ffa15b2d" ? "#666666" : "#E8EDF2"));
                if (shape.Stroke != null) shape.Stroke = new SolidColorBrush(Color.Parse(shape.Stroke.ToString() == "#ff6d7377" ? "#555555" : "#E8EDF2"));
            }
        }
        Capture(window, output, name); window.Close();
    }
}
