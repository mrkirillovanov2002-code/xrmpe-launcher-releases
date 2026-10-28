using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Content;

internal static class CosmeticDialogChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var owner = new MainWindow();
        owner.Show();
        var create = typeof(MainWindow).GetMethod("CreateCosmeticMismatchDialog", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("Cosmetic warning dialog missing");
        var build = new EmissiveBuildResult(false, "fixture-only", new string('A', 64), false);
        foreach (var (label, expected) in new[]
        {
            ("Продолжить без повязок", CosmeticDecision.Without),
            ("Всё равно включить", CosmeticDecision.Include),
            ("Отмена", CosmeticDecision.Cancel)
        })
        {
            var dialog = (Window)create.Invoke(null, [build])!;
            var task = dialog.ShowDialog<CosmeticDecision>(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs();
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            if (buttons.Length != 3 || !buttons.Select(x => x.Content?.ToString()).SequenceEqual(new[]
                { "Продолжить без повязок", "Всё равно включить", "Отмена" }))
                throw new Exception("Incorrect Russian cosmetic warning buttons");
            var text = string.Join(" ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text));
            if (!text.Contains("на свой риск") || !text.Contains("подписи обновлений") ||
                !text.Contains("SHA-256 пакета:")) throw new Exception("Cosmetic warning text incomplete");
            foreach (var button in buttons)
            {
                var point = button.TranslatePoint(default, dialog) ?? throw new Exception("Button outside dialog visual tree");
                if (point.X < 0 || point.Y < 0 || point.X + button.Bounds.Width > dialog.ClientSize.Width ||
                    point.Y + button.Bounds.Height > dialog.ClientSize.Height)
                    throw new Exception("Cosmetic warning button clipped");
            }
            if (expected == CosmeticDecision.Without)
            {
                using var frame = dialog.CaptureRenderedFrame() ?? throw new Exception("Cosmetic warning did not render");
                frame.Save(Path.Combine(output, "cosmetic-sha-warning.png"));
            }
            buttons.Single(x => x.Content?.ToString() == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            if (!task.IsCompletedSuccessfully || task.Result != expected) throw new Exception("Cosmetic warning decision did not close promptly");
            Console.WriteLine("PASS dialog: " + label);
        }
        owner.Close();
        Console.WriteLine("PASS Russian text, layout, rendering and three responsive modal choices; no game launch");
    }
}
