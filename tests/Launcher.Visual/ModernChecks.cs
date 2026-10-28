using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Presentation;

internal static class ModernChecks
{
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static T C<T>(MainWindow w, string name) where T : Control => w.FindControl<T>(name)!;
    static void Set(MainWindow w, string name, object value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(w, value);
    static object? Call(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    static void Assert(bool ok, string text) { if (!ok) throw new Exception("Modern: " + text); }
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs(); }
    static void Capture(MainWindow w, string output, string name) { C<ScrollViewer>(w, "PageScroll").Offset = default; Pump(); using var frame = w.CaptureRenderedFrame()!; frame.Save(Path.Combine(output, name + ".png")); }

    public static void Run(string root, string output)
    {
        var metrics = new Dictionary<string, object>();
        var process = Process.GetCurrentProcess();
        var startup = Stopwatch.StartNew();
        var w = new MainWindow { Width = 1320, Height = 900 };
        DeveloperChecks.Prepare(w); w.Show(); Pump();
        metrics["headlessWindowConstructShowMs"] = startup.Elapsed.TotalMilliseconds;
        Set(w, "_interfaceStore", new InterfacePreferencesStore(Path.Combine(root, "modern-interface.json")));
        Set(w, "_profileStore", new Xrmpe.Launcher.Content.ServerProfileStore(Path.Combine(root, "modern-profiles.json")));
        Set(w, "_modWorkspaces", new Xrmpe.Launcher.Content.ModWorkspaceStore(Path.Combine(root, "modern-mods.json")));
        Set(w, "_browserStore", new ServerBrowserStore(Path.Combine(root, "modern-browser.json")));
        Set(w, "_browserPreferences", new BrowserPreferences([], []));
        var contentWait = Stopwatch.StartNew();
        while (!(bool)typeof(MainWindow).GetField("_contentReady", Flags)!.GetValue(w)! && contentWait.ElapsedMilliseconds < 35000) { Pump(); Thread.Sleep(20); }
        Assert((bool)typeof(MainWindow).GetField("_contentReady", Flags)!.GetValue(w)!, "content discovery ready");
        var addons = (IReadOnlyList<Xrmpe.Launcher.Content.AddonDescriptor>)typeof(MainWindow).GetField("_addons", Flags)!.GetValue(w)!;
        Call(w, "PickContentAddon", addons.Single(Xrmpe.Launcher.Content.ContentPolicy.VerifiedAddon));
        Assert((bool)typeof(MainWindow).GetProperty("ContentClientAllowed", Flags)!.GetValue(w)!, "verified PvP selected in isolated profile");
        Set(w, "_interfacePreferences", new InterfacePreferences(Animations: 0)); Call(w, "ApplyModernMotion");
        Assert(ModernDefaults.Transport(null, true, true) == 1 && ModernDefaults.Transport(null, true, false) == 0 &&
            ModernDefaults.Transport(null, false, true) == 0 && ModernDefaults.Transport(0, true, true) == 0 && ModernDefaults.Transport(1, false, false) == 1, "default Steam capability and legacy/manual preference preservation");
        var store = new InterfacePreferencesStore(Path.Combine(root, "modern-interface.json"));
        var freshCatalog = new CatalogPreferencesStore(Path.Combine(root, "modern-fresh-catalog.json"));
        freshCatalog.Save(CatalogPreferencesStore.NewProfileDefaults());
        Assert(freshCatalog.Load().BackupAddress == "https://xrmpe-reserve-api.onrender.com" && !freshCatalog.Load().Publish, "new profile reserve survives restart without publication");
        C<ComboBox>(w, "NetworkMode").SelectedIndex = 1;
        Assert(C<ComboBox>(w, "BrowserTransport").SelectedIndex == 1 && store.Load().NetworkChoice == 1, "Steam synchronized and saved");
        Assert(!C<ComboBox>(w, "NetworkMode").IsVisible && !C<ComboBox>(w, "BrowserTransport").IsVisible, "old compact selectors hidden");
        C<Button>(w, "IpChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(C<ComboBox>(w, "NetworkMode").SelectedIndex == 0 && C<Button>(w, "IpChoice").Classes.Contains("selected"), "large IP choice");
        C<Button>(w, "BrowserSteamChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(C<ComboBox>(w, "NetworkMode").SelectedIndex == 1 && C<Button>(w, "SteamChoice").Classes.Contains("selected") && store.Load().NetworkChoice == 1, "large Steam choice persisted");
        C<ComboBox>(w, "BrowserTransport").SelectedIndex = 0;
        Assert(C<ComboBox>(w, "NetworkMode").SelectedIndex == 0 && store.Load().NetworkChoice == 0, "IP synchronized and saved");
        var fixtures = Enumerable.Range(1, 2000).Select(i => new BrowserServer(new($"fixture-{i}.invalid", 5446),
            new($"UI-FIXTURE {i:D4}", i % 2 == 0 ? "mp_kpp_2" : "fixture-map", "deathmatch", i % 16, 32, "unknown"), i % 80)).ToList();
        var browserWatch = Stopwatch.StartNew(); Set(w, "_browserServers", fixtures); Call(w, "ApplyBrowserFilters");
        C<Button>(w, "BrowserNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        metrics["fixture2000BindRenderMs"] = browserWatch.Elapsed.TotalMilliseconds;
        var list = C<ListBox>(w, "ServerList"); list.SelectedIndex = 7; var key = ((BrowserServer)list.SelectedItem!).Endpoint.Key;
        Call(w, "ApplyBrowserFilters"); Assert(((BrowserServer)list.SelectedItem!).Endpoint.Key == key, "stable selection");
        C<TextBox>(w, "ServerNameFilter").Text = "deathmatch"; Pump(); Assert(list.Items.Count == 2000, "mode search");
        C<TextBox>(w, "ServerNameFilter").Text = "mp_kpp_2"; Pump(); Assert(list.Items.Count == 1000, "map search");
        C<TextBox>(w, "ServerNameFilter").Text = "nothing matches"; Pump(); Assert(list.Items.Count == 0 && C<TextBlock>(w, "BrowserEmptyState").IsVisible, "empty state");
        C<TextBox>(w, "ServerNameFilter").Text = ""; Pump(); list.SelectedIndex = 0;
        var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count(); Assert(realized < 100, "virtualized list");
        metrics["fixtureRows"] = 2000; metrics["realizedListItems"] = realized;
        var scrollWatch = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++) { list.ScrollIntoView(fixtures[(i * 61) % 2000]); Pump(); }
        metrics["thirtyScrollRenderMs"] = scrollWatch.Elapsed.TotalMilliseconds;
        list.SelectedIndex = 0; list.ScrollIntoView(list.SelectedItem!); Pump();
        Assert(C<TextBlock>(w, "SelectedServerCompatibility").Text!.Contains("не проверена"), "no fabricated compatibility");
        C<ComboBox>(w, "BrowserTransport").SelectedIndex = 1;
        Pump();
        Assert(!C<Button>(w, "BrowserConnectButton").IsEnabled && C<TextBlock>(w, "BrowserConnectionReason").Text!.Contains("По IP"), $"IP is not Steam: mode={C<ComboBox>(w, "NetworkMode").SelectedIndex}; selected={list.SelectedItem}; reason={C<TextBlock>(w, "BrowserConnectionReason").Text}");
        C<ComboBox>(w, "BrowserTransport").SelectedIndex = 0;
        Capture(w, output, "ZoneLink-Browser-1320x900-UI-FIXTURE");
        w.Width = 1000; w.Height = 740; Pump();
        Assert(Grid.GetRow(C<Border>(w, "BrowserDetailPanel")) == 1, "narrow details below list");
        Capture(w, output, "ZoneLink-Browser-1000x740-UI-FIXTURE");
        w.Width = 1920; w.Height = 1080; Pump(); Assert(Grid.GetRow(C<Border>(w, "BrowserDetailPanel")) == 0, "wide two columns");
        Assert(C<Border>(w, "BrowserDetailPanel").Bounds.Width / C<Grid>(w, "BrowserHybrid").Bounds.Width is >= .30 and <= .35, "details occupy 30–35 percent");
        Capture(w, output, "ZoneLink-Browser-1920x1080-UI-FIXTURE");
        var selected = (BrowserServer)list.SelectedItem!; Set(w, "_browserServers", fixtures.Where(x => x.Endpoint.Key != selected.Endpoint.Key).ToList()); Call(w, "ApplyBrowserFilters");
        Assert(list.SelectedItem is null && C<TextBlock>(w, "BrowserStatus").Text!.Contains("исчез"), "disappeared selection");
        C<TextBlock>(w, "BrowserStatus").Text = "Серверов: 0 · ответ QR2: 0. Все настроенные каталоги недоступны.";
        Assert(C<TextBlock>(w, "PlayerBrowserStatus").Text!.Contains("Все настроенные интернет-каталоги недоступны"), "both catalogues offline explained");

        foreach (int mode in new[] { 0, 1, 2 })
        {
            C<ComboBox>(w, "AnimationMode").SelectedIndex = mode; Pump();
            Assert(store.Load().Animations == mode, "animation setting persisted");
            var level = (int)typeof(MainWindow).GetProperty("MotionLevel", Flags)!.GetValue(w)!;
            Assert((C<Button>(w, "LaunchButton").Transitions is null) == (level == 0), "actual animation transitions");
            process.Refresh(); var animationCpu = process.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew(); var task = (Task)Call(w, "AnimateModern", C<Control>(w, "BrowserDetailPanel"), false)!;
            while (!task.IsCompleted && elapsed.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(5); }
            Assert(task.IsCompletedSuccessfully, "finite animation completes"); metrics[$"animationMode{mode}ActualMs"] = elapsed.Elapsed.TotalMilliseconds;
            process.Refresh(); metrics[$"animationMode{mode}CpuMs"] = (process.TotalProcessorTime - animationCpu).TotalMilliseconds;
            if (level == 0) Assert(elapsed.ElapsedMilliseconds < 100, "disabled instant");
        }
        C<ComboBox>(w, "AnimationMode").SelectedIndex = 0;
        C<Button>(w, "ConnectNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        C<ComboBox>(w, "NetworkMode").SelectedIndex = 1; Capture(w, output, "ZoneLink-Connect-Steam-1920x1080");
        C<Button>(w, "IpChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Assert(C<Border>(w, "IpConnectPanel").IsVisible && !C<Border>(w, "SteamConnectPanel").IsVisible, "large choice switches scenario");
        Capture(w, output, "ZoneLink-Connect-IP-1920x1080");
        C<Button>(w, "SteamChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        var help = C<Control>(w, "ConnectPage").GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("help"));
        help.Focus(); w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r"); w.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r"); Pump();
        Assert(help.Flyout?.IsOpen == true, "keyboard help"); Capture(w, output, "ZoneLink-Help-1920x1080");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, ""); w.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, ""); Pump();
        Assert(help.Flyout is null || !help.Flyout.IsOpen, "Escape closes help");
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        w.MouseDown(new Point(5, 5), Avalonia.Input.MouseButton.Left, RawInputModifiers.None); w.MouseUp(new Point(5, 5), Avalonia.Input.MouseButton.Left, RawInputModifiers.None); Pump();
        Assert(help.Flyout is null || !help.Flyout.IsOpen, "outside click closes help");
        var hoverAt = help.TranslatePoint(new Point(12, 12), w)!.Value;
        ToolTip.SetShowDelay(help, 0); w.MouseMove(hoverAt, RawInputModifiers.None); Pump(); Thread.Sleep(100); Pump();
        Assert(ToolTip.GetIsOpen(help), "hover help"); w.MouseMove(new Point(0, 0), RawInputModifiers.None); ToolTip.SetIsOpen(help, false); Pump();
        GC.Collect(); GC.WaitForPendingFinalizers(); var beforeMemory = GC.GetTotalMemory(true);
        for (int i = 0; i < 100; i++) { help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(help.Flyout?.IsOpen == true, "click help"); help.Flyout!.Hide(); Pump(); }
        metrics["hundredHelpCyclesManagedBytesDelta"] = GC.GetTotalMemory(true) - beforeMemory;
        var secondMemory = GC.GetTotalMemory(true);
        for (int i = 0; i < 100; i++) { help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); help.Flyout!.Hide(); Pump(); }
        metrics["secondHundredHelpCyclesManagedBytesDelta"] = GC.GetTotalMemory(true) - secondMemory;
        Assert(help.Flyout is null, "closed flyout detached");
        C<Button>(w, "PlayNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Capture(w, output, "ZoneLink-Home-1920x1080");
        var originalAddon = (string)typeof(MainWindow).GetField("_activeAddonName", Flags)!.GetValue(w)!;
        Set(w, "_activeAddonName", "missing.addon"); Call(w, "RefreshLaunchControls");
        Assert(C<TextBlock>(w, "HomeLaunchReason").Text!.Contains("не выбран") && C<Button>(w, "HomeFixButton").IsVisible, "missing addon actionable");
        C<Button>(w, "HomeFixButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Assert(C<Control>(w, "AddonsPage").IsVisible, "fix opens existing content page");
        Set(w, "_activeAddonName", originalAddon); Call(w, "RefreshLaunchControls");
        Call(w, "PickContentAddon", addons.First(x => x.Healthy && x.Modes.Contains("coop")));
        Assert(C<TextBlock>(w, "HomeLaunchReason").Text!.Contains("ещё не подтверждён") && !C<Button>(w, "LaunchButton").IsEnabled, "unverified addon remains blocked with precise reason");
        Call(w, "PickContentAddon", addons.Single(Xrmpe.Launcher.Content.ContentPolicy.VerifiedAddon));
        var modes = C<ComboBox>(w, "ContentMode");
        modes.ItemsSource = new[] { new Xrmpe.Launcher.Content.ModeChoice("dm"), new Xrmpe.Launcher.Content.ModeChoice("coop") };
        modes.SelectedIndex = 1; Call(w, "RefreshLaunchControls");
        Assert(C<TextBlock>(w, "ContentAvailability").Text!.Contains("в этом режиме") && !C<Button>(w, "ServerLaunchButton").IsEnabled, "unsupported mode reason without permission bypass");
        modes.SelectedIndex = 0; var oldMap = C<TextBox>(w, "ServerMap").Text;
        C<TextBox>(w, "ServerMap").Text = "unverified-ui-fixture"; Call(w, "RefreshLaunchControls");
        Assert(C<TextBlock>(w, "ContentAvailability").Text!.Contains("карта не проверена") && !C<Button>(w, "ServerLaunchButton").IsEnabled, "unverified map reason without unlocking");
        C<TextBox>(w, "ServerMap").Text = oldMap; Call(w, "RefreshLaunchControls");
        DpiCaptures(w, output);
        process.Refresh(); var cpu = process.TotalProcessorTime; var idle = Stopwatch.StartNew();
        while (idle.ElapsedMilliseconds < 2000) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        process.Refresh(); metrics["headlessIdleCpuOneCorePercent2s"] = (process.TotalProcessorTime - cpu).TotalMilliseconds / idle.Elapsed.TotalMilliseconds * 100;
        metrics["testProcessWorkingSetMiB"] = process.WorkingSet64 / 1048576d;
        metrics["measurementScope"] = "Actual headless Avalonia/Skia test process; includes test fixtures and previous UI captures. Not native idle/GPU benchmark. No server fixture was contacted.";
        File.WriteAllText(Path.Combine(output, "ZoneLink-performance.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        w.Close();
        Console.WriteLine("PASS: Modern hybrid selection, 2000-row virtualization/search, disappeared server, honest Steam/IP gate, preference persistence, animation modes, keyboard/hover/100 help cycles; measured performance JSON written.");
    }
    private static void DpiCaptures(MainWindow w, string output)
    {
        // Only the test's headless platform is changed. Application DPI logic is untouched.
        var impl = typeof(Window).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.GetValue(w)!;
        var type = impl.GetType();
        var field = type.GetField("<RenderScaling>k__BackingField", Flags)!;
        var changed = (Action<double>)type.GetProperty("ScalingChanged")!.GetValue(impl)!;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            field.SetValue(impl, scale); changed(scale);
            w.Width = scale == 2 ? 1280 : 1920 / scale; w.Height = scale == 2 ? 720 : 1080 / scale;
            C<Button>(w, "ConnectNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(Math.Abs(w.RenderScaling - scale) < .001, "DPI scaling applied");
            var choice = C<Button>(w, "IpChoice"); var at = choice.TranslatePoint(default, w)!.Value;
            Assert(at.X >= 0 && at.X + choice.Bounds.Width <= w.Bounds.Width, "DPI choice inside window");
            Capture(w, output, $"ZoneLink-DPI-{scale * 100:F0}-Connect");
        }
        field.SetValue(impl, 1d); changed(1);
        Console.WriteLine("PASS: ZoneLink rendering with headless DPI 100/125/150/200%; native multi-monitor DPI not claimed.");
    }
}
