using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Presentation;

internal static class DeveloperChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(MainWindow w, string name, object value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(w, value);
    static object? Call(MainWindow w, string name, params object?[] values) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, values);
    static T C<T>(MainWindow w, string name) where T : Control => w.FindControl<T>(name)!;
    static void Assert(bool ok, string label) { if (!ok) throw new Exception("Developer UI: " + label); }
    static void Click(MainWindow w, string name) => C<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    public static void Prepare(MainWindow w)
    {
        Set(w, "_modernReady", false);
        Set(w, "_interfaceStore", new InterfacePreferencesStore(Path.Combine(Path.GetTempPath(), "xrMPE-modern-ui-fixtures", Guid.NewGuid().ToString("N"), "interface.json")));
        C<ComboBox>(w, "NetworkMode").SelectedIndex = 0;
        C<ComboBox>(w, "BrowserTransport").SelectedIndex = 0;
        // No startup HTTP requests, even if the real user's repository is configured.
        Set(w, "_internetInitialized", false);
        C<TextBox>(w, "GitHubRepository").Text = "OWNER/REPOSITORY";
        Set(w, "_approvedRepository", "OWNER/REPOSITORY");
        Set(w, "_internetInitialized", true);
        SetDeveloperPresentation(w, false);
        Set(w, "_modernReady", true);
    }
    public static void SetDeveloperPresentation(MainWindow w, bool value)
    { Set(w, "_interfacePreferences", new InterfacePreferences(1, value, value)); Call(w, "ApplyDeveloperPresentation"); }

    public static void Run(MainWindow w, string root, string output)
    {
        var modePath = Path.Combine(root, "interface.json");
        var store = new InterfacePreferencesStore(modePath);
        Assert(store.Load() == new InterfacePreferences(), "first run normal");
        Assert(InterfacePreferencesStore.Validate(new(1, true, false)).DeveloperMode == false, "warning cannot be skipped");
        Set(w, "_interfaceStore", store);
        Set(w, "_internetPreferencesFile", Path.Combine(root, "internet.json"));
        SetDeveloperPresentation(w, false);
        var toggle = C<ToggleSwitch>(w, "DeveloperModeToggle");
        toggle.IsChecked = true;
        Assert(C<Border>(w, "DeveloperEnableConfirmation").IsVisible && !C<Control>(w, "CatalogSettingsPanel").IsVisible, "unconfirmed enable");
        Click(w, "CancelDeveloperButton");
        Assert(!File.Exists(modePath) && !toggle.IsChecked!.Value, "cancel does not save");
        toggle.IsChecked = true;
        Capture(w, output, "Developer-warning", "SettingsPage");
        Click(w, "ConfirmDeveloperButton");
        Assert(store.Load().DeveloperMode && C<Control>(w, "DeveloperIndicator").IsVisible, "confirmation persisted");
        Assert(new InterfacePreferencesStore(modePath).Load().DeveloperMode, "restart persistence");
        Capture(w, output, "Developer-settings", "SettingsPage");

        var catalogPath = Path.Combine(root, "catalog.json");
        var catalog = new CatalogPreferencesStore(catalogPath);
        var initial = new CatalogPreferences("http://127.0.0.1:17871", true, "http://127.0.0.1:17872");
        catalog.Save(initial);
        Set(w, "_catalogStore", catalog); Set(w, "_catalogSettingsPath", catalogPath); Set(w, "_catalogPreferences", initial);
        string before = File.ReadAllText(catalogPath);
        toggle.IsChecked = false;
        Assert(!C<Control>(w, "CatalogSettingsPanel").IsVisible && catalog.Load() == initial && File.ReadAllText(catalogPath) == before, "off preserves both sources and publication");
        Assert(!store.Load().DeveloperMode && store.Load().DeveloperWarningAccepted, "off persists acceptance");
        toggle.IsChecked = true;
        Assert(!C<Border>(w, "DeveloperEnableConfirmation").IsVisible, "subsequent enable no repeated warning");
        C<TextBox>(w, "CatalogAddress").Text = initial.Address;
        C<TextBox>(w, "CatalogBackupAddress").Text = initial.BackupAddress;
        C<CheckBox>(w, "AdvertiseInternet").IsChecked = true;
        C<TextBox>(w, "CatalogAddress").Text = "https://user:password@example.com";
        Click(w, "CatalogSaveButton");
        Assert(!C<Border>(w, "CatalogChangeConfirmation").IsVisible && catalog.Load() == initial, "reject credentials");
        C<TextBox>(w, "CatalogAddress").Text = "http://127.0.0.1:17873";
        Click(w, "CatalogSaveButton");
        Assert(C<Border>(w, "CatalogChangeConfirmation").IsVisible && catalog.Load() == initial, "staged changes not saved");
        Click(w, "CatalogCancelButton"); Assert(catalog.Load() == initial, "cancel save");
        Click(w, "CatalogSaveButton"); Set(w, "_serverRunning", true); Click(w, "CatalogConfirmButton");
        Assert(catalog.Load() == initial, "running dedicated blocks change"); Set(w, "_serverRunning", false);
        Click(w, "CatalogConfirmButton"); Assert(catalog.Load().Address.EndsWith("17873"), "confirmed save");
        Click(w, "CatalogDefaultsButton");
        Assert(C<TextBlock>(w, "CatalogChangeText").Text!.Contains("17873") && catalog.Load().Publish, "reset shows changes first");
        Click(w, "CatalogCancelButton"); Assert(catalog.Load().Publish, "cancel reset");
        Click(w, "CatalogDefaultsButton"); Click(w, "CatalogConfirmButton");
        Assert(catalog.Load() == CatalogPreferencesStore.NewProfileDefaults() && File.Exists(catalogPath + ".bak"), "explicit reset with backup");
        File.WriteAllText(catalogPath, "{broken");
        Click(w, "CatalogDefaultsButton"); Click(w, "CatalogConfirmButton");
        Assert(catalog.Load() == CatalogPreferencesStore.NewProfileDefaults() && Directory.GetFiles(root, "catalog.json.corrupt-*").Length == 1, "corrupt settings archived on explicit reset");
        C<Expander>(w, "CatalogSettingsPanel").IsExpanded = true;
        Capture(w, output, "Developer-catalog", "BrowserPage");
        C<TextBox>(w, "GitHubRepository").Text = "test-owner/test-releases";
        Call(w, "GitHubRepositoryChanged", null, new RoutedEventArgs());
        Assert(C<Border>(w, "RepositoryChangeConfirmation").IsVisible && C<TextBox>(w, "GitHubRepository").Text == "OWNER/REPOSITORY", "repository staged only");
        Click(w, "CancelRepositoryButton");
        Assert(!File.Exists(Path.Combine(root, "internet.json")), "cancel repository no write");
        C<TextBox>(w, "GitHubRepository").Text = "test-owner/test-releases";
        Call(w, "GitHubRepositoryChanged", null, new RoutedEventArgs());
        Click(w, "ConfirmRepositoryButton");
        Assert(File.ReadAllText(Path.Combine(root, "internet.json")).Contains("test-owner/test-releases"), "repository confirmed without network");
        string export = (string)Call(w, "BuildSafeDiagnostics")!;
        Assert(!export.Contains("https://") && !export.Contains(root) && !export.Contains("test-owner"), "structured export excludes private text");
        string secret = "abcDEF123456789012345";
        string cleaned = DiagnosticPrivacy.RemoveSecrets($"Bearer {secret} psw=hello token=world {secret} https://login:pass@host", secret);
        Assert(!cleaned.Contains(secret) && !cleaned.Contains("hello") && !cleaned.Contains("world") && !cleaned.Contains("login:pass"), "secret masks");
        Assert(!DiagnosticPrivacy.ForPlayer("heartbeat: https://example.com\nОшибка: https://example.com C:\\private\\secret").Contains("example.com"), "normal technical filtering preserves errors without addresses");
        Assert(!DiagnosticPrivacy.Export(new("injected-secret", true, true, true, true, false, false, false, false)).Contains("injected-secret"), "export version allowlist");
        store.Save(new(1, true, true)); store.Save(new(1, false, true)); File.WriteAllText(modePath, "broken");
        Assert(store.Load().DeveloperMode && store.Warning is not null, "interface backup recovery");
        store.Save(new()); Assert(Directory.GetFiles(root, "interface.json.corrupt-*").Length == 1, "corrupt interface preserved");
        File.WriteAllText(catalogPath, "{\"Address\":\"http://127.0.0.1:17871\",\"Publish\":true}");
        Assert(catalog.Load() == new CatalogPreferences(initial.Address, true, ""), "legacy catalog migration preserves custom primary");
        toggle.IsChecked = false;
        Console.WriteLine("PASS: developer first/cancel/confirm/off/restart, source preservation, staged URL/repository edits, idle guard, confirmed reset and corrupt recovery, migration, privacy; no remote requests.");
    }
    static void Capture(MainWindow w, string output, string name, string page)
    {
        C<TextBlock>(w, "PageTitle").Text = page == "SettingsPage" ? "Настройки" : "Серверы";
        foreach (var controlName in new[] { "PlayPage", "ServerPage", "ConnectPage", "SettingsPage", "UpdatesPage", "BrowserPage", "AddonsPage" }) C<Control>(w, controlName).IsVisible = controlName == page;
        foreach (var (width, height) in new[] { (1000, 740), (1320, 900), (1920, 1080), (2560, 1440) })
        {
            w.Width = width; w.Height = height;
            C<ScrollViewer>(w, "PageScroll").Offset = default;
            Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            C<ScrollViewer>(w, "PageScroll").ScrollToEnd();
            Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            C<Control>(w, name == "Developer-warning" ? "DeveloperEnableConfirmation" : name == "Developer-settings" ? "CopyDiagnosticsButton" : "CatalogDefaultsButton").BringIntoView();
            Thread.Sleep(250); Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"UI capture {name} {width}: offset={C<ScrollViewer>(w, "PageScroll").Offset}, extent={C<ScrollViewer>(w, "PageScroll").Extent}");
            using var frame = w.CaptureRenderedFrame() ?? throw new Exception("No developer frame");
            frame.Save(Path.Combine(output, $"{name}-{width}x{height}.png"));
        }
    }
}
