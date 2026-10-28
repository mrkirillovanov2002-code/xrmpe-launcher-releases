using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

internal static class BrandCandidates
{
    record Layer(string Data, string? Fill = null, string? Stroke = null, double Width = 3);
    record Candidate(string Id, string Title, string Description, Layer[] Layers);
    static readonly Candidate[] Designs = [
        new("anomaly", "01 · Аномалия", "Разомкнутое кольцо / энергия", [
            new("M16 6H48L58 16V48L48 58H16L6 48V16Z", "#141A22", "#65717A", 2),
            new("M19 15A23 23 0 0 1 49 24 M50 39A23 23 0 0 1 20 50 M14 39A23 23 0 0 1 15 22", null, "#FF8A3C", 5),
            new("M32 22L42 32L32 42L22 32Z", "#FFB16E"),
            new("M31 3H36V10H31Z M54 30H61V35H54Z M28 54H33V61H28Z", "#9BA9B7")]),
        new("watchtower", "02 · Вышка", "Наблюдение / индустриальная Зона", [
            new("M10 4H54V48L32 61L10 48Z", "#141A22", "#65717A", 2),
            new("M16 20L32 9L48 20Z", "#FF8A3C"),
            new("M20 23H44V35H20Z", "#AEBBC7"),
            new("M25 25H30V32H25Z M34 25H39V32H34Z", "#141A22"),
            new("M16 35H48V40H16Z M23 40H29L25 53H19Z M35 40H41L45 53H39Z", "#FF8A3C")]),
        new("signal", "03 · Сигнал", "Радиосвязь / обнаружение", [
            new("M32 3L58 18V46L32 61L6 46V18Z", "#141A22", "#65717A", 2),
            new("M17 27A17 17 0 0 1 47 27 M23 31A10 10 0 0 1 41 31", null, "#FF8A3C", 4),
            new("M28 34A4 4 0 1 1 36 34A4 4 0 1 1 28 34Z", "#E8EDF2"),
            new("M29 40H35V51H29Z M20 51H44V55H20Z", "#FF8A3C"),
            new("M10 33H15V39H10Z M49 33H54V39H49Z", "#9BA9B7")])
    ];
    static Control Mark(Candidate design, double size)
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        foreach (var layer in design.Layers) canvas.Children.Add(new ShapePath { Data = Geometry.Parse(layer.Data), Fill = layer.Fill is null ? null : Brush.Parse(layer.Fill), Stroke = layer.Stroke is null ? null : Brush.Parse(layer.Stroke), StrokeThickness = layer.Width, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
    static void Save(Control content, int width, int height, string path)
    {
        var window = new Window { Width = width, Height = height, Background = Brushes.Transparent, Content = content };
        window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!; frame.Save(path); window.Close();
    }
    public static void Export(string output)
    {
        Directory.CreateDirectory(output);
        var comparison = new Grid { ColumnDefinitions = new("*,*,*"), Background = Brush.Parse("#0B0F14"), Margin = new Thickness(0) };
        for (int i = 0; i < Designs.Length; i++)
        {
            var d = Designs[i];
            string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"512\" height=\"512\" viewBox=\"0 0 64 64\">" + string.Concat(d.Layers.Select(l => $"<path d=\"{l.Data}\" fill=\"{l.Fill ?? "none"}\" stroke=\"{l.Stroke ?? "none"}\" stroke-width=\"{l.Width}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>")) + "</svg>";
            File.WriteAllText(Path.Combine(output, $"zonelink-{d.Id}.svg"), svg);
            foreach (int size in new[] { 16, 32, 48, 64, 256, 512 }) Save(Mark(d, size), size, size, Path.Combine(output, $"zonelink-{d.Id}-{size}.png"));
            var small = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            foreach (int size in new[] { 16, 32, 64 }) small.Children.Add(Mark(d, size));
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 24, Children = { new TextBlock { Text = d.Title, FontSize = 25, FontWeight = FontWeight.SemiBold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }, Mark(d, 220), new TextBlock { Text = d.Description, FontSize = 15, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }, small, new TextBlock { Text = "16 / 32 / 64 px · без букв", FontSize = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } } };
            Grid.SetColumn(panel, i); comparison.Children.Add(panel);
        }
        Save(comparison, 1170, 480, Path.Combine(output, "ZoneLink-emblems-comparison.png"));
        Console.WriteLine("PASS: three distinct original no-letter emblems exported to SVG and 16/32/64/512 PNG; no application identity changed.");
    }
}
