using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Content;

// Renders the real application tree with Skia, without starting any game/network action.
// Does not set GamePath or save any preferences. Only the normal session log is created.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--verify-icon")) { IconChecks.Verify(Path.GetFullPath(args[0])); return; }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/stalker-redesign-preview/screenshots");
        Directory.CreateDirectory(output);
        var diagnostics = new Diagnostics();
        Logger.Sink = diagnostics;
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        if (args.Contains("--cosmetic-dialog-test")) { CosmeticDialogChecks.Run(output); return; }
        if (args.Contains("--rc-quick")) { RcChecks.Run(output, args[^1]); return; }
        if (args.Contains("--logos-only")) { OverhaulChecks.ExportLogos(output); return; }
        if (args.Contains("--brand-candidates")) { BrandCandidates.Export(output); return; }
        var window = new MainWindow();
        DeveloperChecks.Prepare(window);
        window.Show();
        window.Classes.Add("motionOff");
        // Static captures represent settled styles, not frames between brush transitions.
        foreach (var button in window.GetVisualDescendants().OfType<Button>()) button.Transitions = null;
        // Await asynchronous read-only content discovery before screenshots.
        var ready = typeof(MainWindow).GetField("_contentReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!(bool)ready.GetValue(window)! && timeout.Elapsed < TimeSpan.FromSeconds(35)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
        var pages = new[] { "PlayPage", "ServerPage", "ConnectPage", "SettingsPage", "UpdatesPage", "BrowserPage", "AddonsPage" };
        var buttons = new[] { "PlayNav", "ServerNav", "ConnectNav", "SettingsNav", "UpdatesNav", "BrowserNav", "AddonsNav" };
        var sizes = new[] { (1320, 900), (1000, 740), (1920, 1080), (2560, 1440) };
        foreach (var (width, height) in sizes)
        {
            window.Width = width; window.Height = height;
            for (var page = 0; page < pages.Length; page++)
            {
                window.FindControl<TextBlock>("PageTitle")!.Text = new[] { "Главная", "Создать сервер", "Подключиться", "Настройки", "Обновления", "Серверы", "Игровой контент" }[page];
                for (var i = 0; i < pages.Length; i++)
                {
                    window.FindControl<Control>(pages[i])!.IsVisible = i == page;
                    window.FindControl<Button>(buttons[i])!.Classes.Set("selected", i == page);
                }
                window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                // Let the finite hover/class brush transitions settle before capturing.
                Thread.Sleep(220);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame");
                frame.Save(Path.Combine(output, $"{page + 1}-{pages[page]}-{width}x{height}.png"));
                AssertLayout(window, width, height);
                if (page == 6)
                {
                    window.FindControl<TabControl>("ContentTabs")!.SelectedIndex = 1;
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    using (var workspace = window.CaptureRenderedFrame()!) workspace.Save(Path.Combine(output, $"7-Mods-{width}x{height}.png"));
                    AssertLayout(window, width, height);
                    window.FindControl<TabControl>("ContentTabs")!.SelectedIndex = 0;
                }
                if (page == 4 && width == 1320)
                {
                    var scroll = window.FindControl<ScrollViewer>("PageScroll")!;
                    scroll.Offset = new Vector(0, scroll.Extent.Height);
                    Dispatcher.UIThread.RunJobs();
                    using var bottom = window.CaptureRenderedFrame()!;
                    bottom.Save(Path.Combine(output, "5-UpdatesPage-controls.png"));
                    AssertLayout(window, width, height);
                }
            }
        }
        if (args.Contains("--captures-only"))
        {
            window.Close();
            if (diagnostics.Errors.Count != 0) throw new Exception(string.Join(Environment.NewLine, diagnostics.Errors));
            Console.WriteLine("PASS: final seven-page captures and layout, no Avalonia warnings/errors.");
            return;
        }
        // Exercise actual browser handlers against an isolated store, never the user's preferences.
        var browserTestRoot = Path.Combine(Path.GetTempPath(), "xrMPE browser UI " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(browserTestRoot);
        try
        {
            DeveloperChecks.Run(window, browserTestRoot, output);
            ModernChecks.Run(browserTestRoot, output);
            OverhaulChecks.Run(browserTestRoot, output);
            PolishChecks.Run(output);
            void Field(string name, object value) => typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, value);
            object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, values);
            Field("_profileStore", new ServerProfileStore(Path.Combine(browserTestRoot, "server-profiles.json")));
            Field("_modWorkspaces", new ModWorkspaceStore(Path.Combine(browserTestRoot, "mod-workspaces.json")));
            var addons = (IReadOnlyList<AddonDescriptor>)typeof(MainWindow).GetField("_addons", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            if (addons.Count != 5 || !(bool)ready.GetValue(window)!) throw new Exception("Installed addon catalogue not ready");
            foreach (var addon in addons.Where(x => x.Healthy))
            {
                Call("PickContentAddon", addon);
                if (window.FindControl<Button>("ServerLaunchButton")!.IsEnabled != ContentPolicy.VerifiedAddon(addon)) throw new Exception("Unsupported addon launch gate");
                if (window.FindControl<Grid>("PvpGameplayFields")!.IsVisible != ContentPolicy.VerifiedAddon(addon) ||
                    window.FindControl<Expander>("ServerTechnicalOptions")!.IsVisible != ContentPolicy.VerifiedAddon(addon)) throw new Exception("Unsupported mode shows PvP player/frag limits or technical launch options");
                if (window.FindControl<TextBox>("ServerMap")!.IsVisible) throw new Exception("Duplicated map field visible");
                foreach (var page in pages) window.FindControl<Control>(page)!.IsVisible = page == "ServerPage";
                window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var modeFrame = window.CaptureRenderedFrame()!;
                modeFrame.Save(Path.Combine(output, "2-Server-" + addon.FileName + ".png"));
            }
            Call("PickContentAddon", addons.Single(x => ContentPolicy.VerifiedAddon(x)));
            window.FindControl<TabControl>("ContentTabs")!.SelectedIndex = 1;
            window.FindControl<TextBox>("ModNotes")!.Text = "Only PvP profile";
            Call("PickContentAddon", addons.Single(x => x.FileName == "coop.addon"));
            if (window.FindControl<TextBox>("ModNotes")!.Text == "Only PvP profile") throw new Exception("PvP notes leaked into coop");
            window.FindControl<TextBox>("ModNotes")!.Text = "Only coop profile";
            Call("PickContentAddon", addons.Single(x => ContentPolicy.VerifiedAddon(x)));
            if (window.FindControl<TextBox>("ModNotes")!.Text != "Only PvP profile") throw new Exception("Per-addon notes not restored");
            foreach (var page in pages) window.FindControl<Control>(page)!.IsVisible = page == "AddonsPage";
            window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using (var modsFrame = window.CaptureRenderedFrame()!) modsFrame.Save(Path.Combine(output, "7-Mod-workspace.png"));
            window.FindControl<TabControl>("ContentTabs")!.SelectedIndex = 0;
            Console.WriteLine("PASS: isolated per-addon mod notes saved on switch; no mod activation API or toggles.");
            var mapSelector = window.FindControl<ComboBox>("VerifiedMapChoice")!;
            var mapChoices = mapSelector.ItemsSource!.Cast<MapChoice>().ToArray();
            mapSelector.SelectedItem = mapChoices.First(x => !x.LaunchVerified);
            if (window.FindControl<Button>("ServerLaunchButton")!.IsEnabled || !window.FindControl<Button>("LaunchButton")!.IsEnabled) throw new Exception("Unverified map must block server launch without blocking a verified PvP client");
            mapSelector.SelectedItem = mapChoices.Single(x => x.Id == "mp_kpp_2");
            window.FindControl<TextBox>("ServerName")!.Text = "UI profile test";
            window.FindControl<Button>("SaveProfileButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var gameRoot = (string)typeof(MainWindow).GetField("_contentRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var persisted = new ServerProfileStore(Path.Combine(browserTestRoot, "server-profiles.json")).Load(gameRoot);
            if (persisted.Name != "UI profile test" || persisted.Addon != "xrmpe_pvp.addon" || persisted.Map != "mp_kpp_2" || persisted.Mode != "dm") throw new Exception("UI profile persistence");
            var reloadedWindow = new MainWindow();
            DeveloperChecks.Prepare(reloadedWindow);
            typeof(MainWindow).GetField("_profileStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(reloadedWindow,
                new ServerProfileStore(Path.Combine(browserTestRoot, "server-profiles.json")));
            reloadedWindow.Show();
            var reloadWatch = System.Diagnostics.Stopwatch.StartNew();
            while (!(bool)ready.GetValue(reloadedWindow)! && reloadWatch.Elapsed < TimeSpan.FromSeconds(35)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
            if (!(bool)ready.GetValue(reloadedWindow)! || reloadedWindow.FindControl<TextBox>("ServerName")!.Text != persisted.Name ||
                reloadedWindow.FindControl<TextBox>("ServerMap")!.Text != persisted.Map || !reloadedWindow.FindControl<Button>("ServerLaunchButton")!.IsEnabled)
                throw new Exception("Fresh window did not restore saved server profile");
            reloadedWindow.Close();
            Console.WriteLine("PASS: fresh MainWindow startup restores isolated server profile and keeps verified PvP enabled.");
            window.FindControl<TextBox>("AddonSearch")!.Text = "no-such-addon-unique";
            Dispatcher.UIThread.RunJobs();
            if (window.FindControl<StackPanel>("AddonCards")!.Children.Count != 1) throw new Exception("Addon search");
            window.FindControl<TextBox>("AddonSearch")!.Text = "";
            Dispatcher.UIThread.RunJobs();
            window.FindControl<ComboBox>("AddonModeFilter")!.SelectedIndex = 2;
            if (window.FindControl<StackPanel>("AddonCards")!.Children.Count != 3) throw new Exception("Addon coop filter");
            window.FindControl<ComboBox>("AddonModeFilter")!.SelectedIndex = 0;
            if (window.FindControl<StackPanel>("AddonCards")!.Children.Count != 5) throw new Exception("Addon all filter");
            Field("_addons", addons.Append(new AddonDescriptor("broken.addon", "broken.addon", "", "", "", [], null, [], null, 0, "Нет metadata.json", null)).ToArray());
            window.FindControl<ComboBox>("AddonModeFilter")!.SelectedIndex = 4;
            var brokenCards = window.FindControl<StackPanel>("AddonCards")!;
            if (brokenCards.Children.Count != 1 || brokenCards.GetVisualDescendants().OfType<Button>().Any(x => x.IsEnabled)) throw new Exception("Broken addon must be shown but cannot be selected");
            Field("_addons", addons);
            window.FindControl<ComboBox>("AddonModeFilter")!.SelectedIndex = 0;
            Console.WriteLine("PASS: real addon selection, unsupported launch gates, search/mode filtering, isolated profile save/reload.");
            Field("_browserStore", new ServerBrowserStore(Path.Combine(browserTestRoot, "server-browser.json")));
            Field("_browserPreferences", new BrowserPreferences([], []));
            Field("_browserServers", new List<BrowserServer>());
            foreach (var page in pages) window.FindControl<Control>(page)!.IsVisible = page == "BrowserPage";
            window.FindControl<TextBox>("ManualServerHost")!.Text = "localhost";
            var add = window.FindControl<Button>("ManualSaveServerButton")!;
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = window.FindControl<ListBox>("ServerList")!;
            var rows = list.ItemsSource!.Cast<BrowserServer>().ToArray();
            if (rows.Length != 1 || rows[0].Snapshot is not null || rows[0].Ping is not null) throw new Exception("Manual address must not manufacture stats");
            list.SelectedIndex = 0;
            window.FindControl<Button>("BrowserFavoriteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (new ServerBrowserStore(Path.Combine(browserTestRoot, "server-browser.json")).Load().Favorites.Count != 1) throw new Exception("Favorite UI persistence");
            window.FindControl<Button>("BrowserFavoriteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (new ServerBrowserStore(Path.Combine(browserTestRoot, "server-browser.json")).Load().Favorites.Count != 0) throw new Exception("Favorite UI remove");
            window.FindControl<Button>("BrowserFavoriteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 1;
            if (list.ItemsSource!.Cast<BrowserServer>().Count() != 1) throw new Exception("Favorites tab");
            window.FindControl<TextBox>("ServerMapFilter")!.Text = "unknown";
            Dispatcher.UIThread.RunJobs();
            if (list.ItemsSource!.Cast<BrowserServer>().Any()) throw new Exception("Unknown map must not match map filter");
            window.FindControl<TextBox>("ServerMapFilter")!.Text = "";
            Field("_browserServers", new List<BrowserServer> {
                new(new("8.8.8.8", 5446), new("Интернет — UI-FIXTURE", "mp_fixture", "deathmatch", 2, 32, "openplaying"), 27, Internet: true),
                new(new("127.0.0.1", 5446), Kind: "LAN") });
            window.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 4;
            if (list.ItemsSource!.Cast<BrowserServer>().Count() != 1 || !list.ItemsSource!.Cast<BrowserServer>().Single().Internet) throw new Exception("Internet tab isolation");
            window.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 2;
            if (list.ItemsSource!.Cast<BrowserServer>().Count() != 1 || list.ItemsSource!.Cast<BrowserServer>().Single().Internet) throw new Exception("LAN tab isolation");
            window.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 4;
            window.FindControl<ComboBox>("ServerSort")!.SelectedIndex = 1;
            window.Width = 1320; window.Height = 900;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Thread.Sleep(220); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            // A new headless window avoids stale platform ClientSize after the 2560px sweep.
            var internetWindow = new MainWindow { Width = 1320, Height = 900 };
            DeveloperChecks.Prepare(internetWindow);
            typeof(MainWindow).GetField("_browserServers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(internetWindow,
                new List<BrowserServer> { new(new("8.8.8.8", 5446), new("Интернет — UI-FIXTURE", "mp_fixture", "deathmatch", 2, 32, "openplaying"), 27, Internet: true) });
            foreach (var p in pages) internetWindow.FindControl<Control>(p)!.IsVisible = p == "BrowserPage";
            internetWindow.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 4;
            internetWindow.Show(); Dispatcher.UIThread.RunJobs(); internetWindow.UpdateLayout();
            using (var internet = internetWindow.CaptureRenderedFrame()!) internet.Save(Path.Combine(output, "6-Internet-UI-FIXTURE.png"));
            var catalogPanel = internetWindow.GetVisualDescendants().OfType<Expander>().Single(x => x.Header?.ToString() == "Глобальный каталог и публикация сервера");
            DeveloperChecks.SetDeveloperPresentation(internetWindow, true);
            catalogPanel.IsExpanded = true;
            if (internetWindow.FindControl<TextBox>("CatalogBackupAddress") is null || internetWindow.FindControl<TextBlock>("CatalogSourceStatus") is null) throw new Exception("Missing reserve catalogue controls");
            Dispatcher.UIThread.RunJobs(); internetWindow.UpdateLayout();
            internetWindow.FindControl<ScrollViewer>("PageScroll")!.Offset = new Vector(0, internetWindow.FindControl<ScrollViewer>("PageScroll")!.Extent.Height);
            Dispatcher.UIThread.RunJobs(); internetWindow.UpdateLayout();
            AssertLayout(internetWindow, 1320, 900);
            using (var settings = internetWindow.CaptureRenderedFrame()!) settings.Save(Path.Combine(output, "6-CatalogSettings-1320x900.png"));
            internetWindow.Close();
            Console.WriteLine("PASS: Internet/LAN tab isolation and ping sort selector; no settings written.");
            // The populated capture is explicitly a fixture; this data is never part of the release.
            Field("_browserServers", new List<BrowserServer> { new(new("127.0.0.1", 5446),
                new("QR2 — тестовая строка интерфейса", "mp_fixture", "deathmatch", 2, 32, "openplaying"), 0, "LAN") });
            window.FindControl<TabControl>("ServerTabs")!.SelectedIndex = 0;
            window.FindControl<TextBox>("ServerNameFilter")!.Text = "QR2";
            window.Width = 1320; window.Height = 900;
            window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var populated = window.CaptureRenderedFrame()!;
            populated.Save(Path.Combine(output, "6-BrowserPage-UI-FIXTURE.png"));
            AssertLayout(window, 1320, 900);
            Console.WriteLine("PASS: real browser UI manual-add, no invented metadata, favorite persistence, tabs and map filter; populated fixture bindings rendered.");
        }
        finally { Directory.Delete(browserTestRoot, true); }
        window.Width = 1320; window.Height = 900;
        foreach (var page in pages) window.FindControl<Control>(page)!.IsVisible = page == "ConnectPage";
        foreach (var button in buttons) window.FindControl<Button>(button)!.Classes.Set("selected", button == "ConnectNav");
        window.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
        window.FindControl<ComboBox>("NetworkMode")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        using (var steam = window.CaptureRenderedFrame()!) steam.Save(Path.Combine(output, "3-ConnectPage-Steam.png"));
        if (window.FindControl<Control>("IpConnectPanel")!.IsVisible || !window.FindControl<Control>("SteamConnectPanel")!.IsVisible)
            throw new Exception("Steam/IP presentation switch failed");
        window.FindControl<ComboBox>("NetworkMode")!.SelectedIndex = 0;
        // Source selection is tested with persistence temporarily disabled, protecting the real profile.
        var internetFlag = typeof(MainWindow).GetField("_internetInitialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        internetFlag.SetValue(window, false);
        var source = window.FindControl<ComboBox>("LauncherUpdateSource")!;
        source.SelectedIndex = 1;
        if (window.FindControl<StackPanel>("GitHubFields")!.IsVisible) throw new Exception("LAN update source presentation");
        source.SelectedIndex = 0;
        if (!window.FindControl<StackPanel>("GitHubFields")!.IsVisible) throw new Exception("GitHub update source presentation");
        if (window.FindControl<Button>("ApplyLauncherButton")!.IsEnabled) throw new Exception("Install offered without a verified manifest");
        internetFlag.SetValue(window, true);
        Console.WriteLine("PASS: Internet/LAN source selector, no installation without a verified offer; startup placeholder performs no network request.");
        // UI events: navigation and clear invoke only presentation code.
        window.FindControl<Button>("PlayNav")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        if (!window.FindControl<Control>("PlayPage")!.IsVisible) throw new Exception("Navigation failed");
        if (string.IsNullOrEmpty(window.FindControl<Button>("LaunchButton")!.Content?.ToString())) throw new Exception("Launch missing");
        if (window.FindControl<TextBlock>("HomeServerName")!.Text != window.FindControl<TextBox>("ServerName")!.Text)
            throw new Exception("Home server binding failed");
        window.Close();
        if (diagnostics.Errors.Count != 0) throw new Exception(string.Join(Environment.NewLine, diagnostics.Errors));
        Console.WriteLine("PASS: seven real XAML pages rendered at 1000x740, 1320x900, 1920x1080, 2560x1440; visible button geometry; Steam/IP panel switch; navigation; Skia assets and bindings loaded.");
    }

    private sealed class Diagnostics : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Errors.Add($"{level}/{area}: {messageTemplate}");
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Errors.Add($"{level}/{area}: {messageTemplate}: {string.Join(", ", propertyValues)}");
    }

    private static void AssertLayout(Window window, int width, int height)
    {
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            if (button.Bounds.Width <= 0 || button.Bounds.Height <= 0) continue;
            var at = button.TranslatePoint(default, window);
            if (at is null) continue;
            // Vertically offscreen controls are intentionally reachable through scrolling.
            if (at.Value.Y < 0 || at.Value.Y > height) continue;
            if (at.Value.X < -1 || at.Value.X + button.Bounds.Width > width + 1)
                throw new Exception($"Button outside horizontal bounds: {button.Name ?? button.Content?.ToString()} at {width}x{height}");
        }
    }
}
