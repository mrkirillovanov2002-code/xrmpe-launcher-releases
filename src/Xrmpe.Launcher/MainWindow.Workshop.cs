using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private bool _workshopReady;
    private bool _workshopPreviewLoading;
    private readonly CancellationTokenSource _workshopBuildStop = new();
    private string _workshopCategory = "Все", _workshopSelected = "", _workshopAngle = "front";
    private readonly Dictionary<string, Bitmap> _workshopImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Button Card, TextBlock State)> _workshopCardViews = new();
    private static readonly (string Id, string Label)[] WorkshopViews =
        [("front", "Спереди"), ("right", "Справа"), ("back", "Сзади"), ("left", "Слева")];

    private void InitializeWorkshopGallery()
    {
        // Images are loaded only on visiting the workshop, never during game startup.
        Closed += (_, _) => { _workshopBuildStop.Cancel(); foreach (var bitmap in _workshopImages.Values) bitmap.Dispose(); _workshopImages.Clear(); };
    }

    private void OpenWorkshopGallery()
    {
        if (!_workshopReady)
        {
            _workshopReady = true;
            foreach (var category in new[] { "Все" }.Concat(WorkshopCatalog.Items.Select(x => x.Family).Distinct()))
            {
                var button = new Button { Content = category, Tag = category, Margin = new Thickness(0, 0, 8, 6) };
                button.Classes.Add("workshopFilter");
                button.Click += (_, _) =>
                {
                    _workshopCategory = category;
                    RenderWorkshopCards();
                };
                WorkshopFilters.Children.Add(button);
            }
            RenderWorkshopCards();
        }
        if (_contentRoot is not null)
        {
            var remembered = _modWorkspaces.Load(_contentRoot, LauncherService.DefaultAddon).ArmbandsPreviewId;
            if (WorkshopCatalog.Items.Any(x => x.Id == remembered)) _workshopSelected = remembered;
        }
        RefreshWorkshopCardStates();
        if (!_workshopPreviewLoading && _contentRoot is not null)
            _ = LoadLocalWorkshopPreviews();
    }

    private async Task LoadLocalWorkshopPreviews()
    {
        if (_workshopPreviewLoading || _contentRoot is null) return;
        _workshopPreviewLoading = true;
        try
        {
            var progress = new Progress<EmissiveBuildProgress>(SetArmbandsProgress);
            var build = await EmissiveWorkshop.EnsureBuiltAsync(_contentRoot, _dataFolder, progress, _workshopBuildStop.Token);
            ReloadWorkshopImages();
            SetArmbandsReady(build);
        }
        catch (OperationCanceledException) when (_workshopBuildStop.IsCancellationRequested) { }
        catch (Exception ex) { SetArmbandsFailure(ex); NotifyWorkshop("Не удалось подготовить предпросмотр из установленной игры: " + ex.Message, true); }
        finally { _workshopPreviewLoading = false; }
    }

    private void ReloadWorkshopImages()
    {
        foreach (var bitmap in _workshopImages.Values) bitmap.Dispose();
        _workshopImages.Clear();
        if (_workshopReady) RenderWorkshopCards();
        if (WorkshopDetailPanel.IsVisible && !string.IsNullOrEmpty(_workshopSelected))
            ShowWorkshopAngle(_workshopAngle);
    }

    private Bitmap? WorkshopImage(string id, string angle, int width)
    {
        var key = $"{id}-{angle}-{width}";
        if (_workshopImages.TryGetValue(key, out var cached)) return cached;
        // Only catalogue identifiers and four fixed views can reach the file loader.
        if (!WorkshopCatalog.Items.Any(x => x.Id == id) || !WorkshopViews.Any(x => x.Id == angle)) return null;
        try
        {
            var path = EmissiveWorkshop.PreviewPath(_dataFolder, id, angle);
            if (!File.Exists(path)) return null;
            ContentPaths.NoLinks(path);
            using var input = File.OpenRead(path);
            var image = Bitmap.DecodeToWidth(input, width);
            // At most 30 small covers + four thumbnails + one full preview are retained.
            _workshopImages[key] = image;
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppendLog("Предпросмотр повязок недоступен: " + ex.Message);
            return null;
        }
    }

    private void ReleaseWorkshopDetailImages()
    {
        WorkshopPreview.Source = null;
        WorkshopAngles.Children.Clear();
        foreach (var key in _workshopImages.Keys.Where(x => !x.EndsWith("-190", StringComparison.Ordinal)).ToArray())
        {
            _workshopImages.Remove(key, out var image);
            image!.Dispose();
        }
    }

    private Control WorkshopCover(string id, string angle, int width, double height)
    {
        var source = WorkshopImage(id, angle, width);
        var panel = new Grid { Height = height, Background = Brush.Parse("#111A23"), ClipToBounds = true };
        if (source is not null) panel.Children.Add(new Image { Source = source, Stretch = Stretch.Uniform });
        else
        {
            panel.Children.Add(new TextBlock { Text = "ПРЕДПРОСМОТР", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#B6FF00"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -18, 0, 0) });
            panel.Children.Add(new TextBlock { Text = _contentRoot is null ? "Выберите папку xrMPE" : "Готовим из установленной игры…", FontSize = 12, Foreground = Brush.Parse("#AEBBC7"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16) });
        }
        return panel;
    }

    private void FilterWorkshop(object? sender, TextChangedEventArgs e)
    {
        if (_workshopReady) RenderWorkshopCards();
    }

    private void RenderWorkshopCards()
    {
        WorkshopCards.Children.Clear(); _workshopCardViews.Clear();
        var query = WorkshopSearch.Text?.Trim() ?? "";
        var items = WorkshopCatalog.Items.Where(x => (_workshopCategory == "Все" || x.Family == _workshopCategory) &&
            (x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        WorkshopCount.Text = $"{items.Length} / 30";
        WorkshopEmpty.IsVisible = items.Length == 0;
        foreach (var filter in WorkshopFilters.Children.OfType<Button>())
            filter.Classes.Set("selected", Equals(filter.Tag, _workshopCategory));
        foreach (var item in items)
        {
            var state = new TextBlock { FontSize = 12, Foreground = Brush.Parse("#B6FF00") };
            var caption = new StackPanel { Margin = new Thickness(12, 10, 12, 12), Spacing = 5 };
            caption.Children.Add(new TextBlock { Text = item.Family, FontSize = 11, Foreground = Brush.Parse("#AEBBC7") });
            caption.Children.Add(new TextBlock { Text = item.Name, FontSize = 15, FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap, Height = 44 });
            caption.Children.Add(state);
            var content = new StackPanel();
            content.Children.Add(WorkshopCover(item.Id, "front", 190, 190));
            content.Children.Add(caption);
            var card = new Button { Content = content, Width = 198, Padding = new Thickness(0), Margin = new Thickness(0, 0, 12, 12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = item.Id, ClipToBounds = true };
            card.Classes.Add("workshopCard");
            Avalonia.Automation.AutomationProperties.SetName(card, item.Name + ". Открыть четыре ракурса");
            card.Click += (_, _) => SelectWorkshopCostume(item.Id);
            WorkshopCards.Children.Add(card);
            _workshopCardViews[item.Id] = (card, state);
        }
        RefreshWorkshopCardStates();
        if (_modernReady) ApplyModernMotion();
    }

    private string WorkshopStateText()
    {
        if (!_contentReady || !ContentPolicy.VerifiedAddon(SelectedAddon)) return "Только xrMPE PvP";
        return ArmbandsToggle.IsChecked == true ? "● Включено" : "Выключено · соберётся при включении";
    }

    private void RefreshWorkshopCardStates()
    {
        if (!_workshopReady) return;
        var text = WorkshopStateText();
        foreach (var (id, view) in _workshopCardViews)
        {
            view.State.Text = text;
            view.Card.Classes.Set("selected", id == _workshopSelected);
        }
        WorkshopDetailState.Text = text;
    }

    private void SelectWorkshopCostume(string id)
    {
        var item = WorkshopCatalog.Items.FirstOrDefault(x => x.Id == id);
        if (item is null) return;
        ReleaseWorkshopDetailImages();
        _workshopSelected = id; _workshopAngle = "front";
        if (_contentRoot is not null && ContentPolicy.VerifiedAddon(SelectedAddon))
        {
            try
            {
                SaveModNotesIfChanged();
                var next = _modWorkspaces.Load(_contentRoot, LauncherService.DefaultAddon) with { ArmbandsPreviewId = id };
                _modWorkspaces.Save(_contentRoot, LauncherService.DefaultAddon, next);
                _visibleModWorkspace = next;
            }
            catch (Exception ex) { NotifyWorkshop("Не удалось сохранить выбранный костюм: " + ex.Message, true); }
        }
        WorkshopDetailTitle.Text = item.Name;
        WorkshopDetailFamily.Text = item.Family.ToUpperInvariant();
        WorkshopDetailNote.Text = "3D-повязка на обеих руках. Локальный статический рендер модели; игровое освещение и bloom могут отличаться.";
        foreach (var view in WorkshopViews)
        {
            var contents = new StackPanel { Spacing = 6 };
            contents.Children.Add(WorkshopCover(id, view.Id, 110, 96));
            contents.Children.Add(new TextBlock { Text = view.Label, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = contents, Tag = view.Id, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Classes.Add("workshopAngle");
            button.Click += (_, _) => ShowWorkshopAngle(view.Id);
            WorkshopAngles.Children.Add(button);
        }
        WorkshopCatalogPanel.IsVisible = false; WorkshopDetailPanel.IsVisible = true;
        ShowWorkshopAngle("front"); RefreshWorkshopCardStates();
        PageScroll.Offset = new Vector(0, 0);
        _ = AnimateModern(WorkshopDetailPanel);
    }

    private void ShowWorkshopAngle(string angle)
    {
        if (!WorkshopViews.Any(x => x.Id == angle)) return;
        // Dispose the previous large frame only after detaching it from the view.
        WorkshopPreview.Source = null;
        foreach (var key in _workshopImages.Keys.Where(x => x.EndsWith("-660", StringComparison.Ordinal)).ToArray())
        {
            _workshopImages.Remove(key, out var image); image!.Dispose();
        }
        _workshopAngle = angle;
        var source = WorkshopImage(_workshopSelected, angle, 660);
        WorkshopPreview.Source = source; WorkshopPreviewMissing.IsVisible = source is null;
        WorkshopAngleLabel.Text = WorkshopViews.First(x => x.Id == angle).Label + " · локальный рендер, не игровой скриншот";
        foreach (var button in WorkshopAngles.Children.OfType<Button>()) button.Classes.Set("selected", Equals(button.Tag, angle));
    }

    private void BackToWorkshop(object? sender, RoutedEventArgs e)
    {
        ReleaseWorkshopDetailImages();
        WorkshopDetailPanel.IsVisible = false; WorkshopCatalogPanel.IsVisible = true;
        RefreshWorkshopCardStates(); PageScroll.Offset = default;
        _ = AnimateModern(WorkshopCatalogPanel);
    }
    private void PreviousWorkshop(object? sender, RoutedEventArgs e) => StepWorkshop(-1);
    private void NextWorkshop(object? sender, RoutedEventArgs e) => StepWorkshop(1);
    private void StepWorkshop(int direction)
    {
        var index = Array.FindIndex(WorkshopCatalog.Items, x => x.Id == _workshopSelected);
        SelectWorkshopCostume(WorkshopCatalog.Items[(index + direction + WorkshopCatalog.Items.Length) % WorkshopCatalog.Items.Length].Id);
    }
    private void NotifyWorkshop(string text, bool error = false)
    {
        WorkshopNotification.IsVisible = true;
        WorkshopNotificationText.Text = text;
        WorkshopNotification.BorderBrush = Brush.Parse(error ? "#E77878" : "#718748");
        _ = AnimateModern(WorkshopNotification);
    }
}
