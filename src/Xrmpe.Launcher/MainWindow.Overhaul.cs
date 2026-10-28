using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Diagnostics;
using Xrmpe.Launcher.Presentation;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private bool _serverStarting;
    private string _lastServerFailureTechnical = "";
    private string? _richLogText;
    private sealed record JournalRow(string Text, IBrush Color);
    private static readonly IBrush JournalNormal = new SolidColorBrush(Color.Parse("#B8C4CE"));
    private static readonly IBrush JournalWarning = new SolidColorBrush(Color.Parse("#FFBA76"));
    private static readonly IBrush JournalError = new SolidColorBrush(Color.Parse("#F45C5C"));
    private void ShowServerFailure(ServerFailure failure)
    {
        _serverStarting = false;
        ServerStatus.Text = "Сервер не работает";
        ServerFailurePanel.IsVisible = true;
        ServerFailureReason.Text = failure.Reason;
        OpenSteamAction.IsVisible = failure.CanOpenSteam;
        _lastServerFailureTechnical = DiagnosticPrivacy.RemoveSecrets(failure.Technical, _sensitiveUpdateToken);
        ServerFailureTechnical.IsVisible = false;
        SteamOpenStatus.Text = "";
        AppendLog("Ошибка сервера: " + _lastServerFailureTechnical);
    }
    private void ClearServerFailure()
    { ServerFailurePanel.IsVisible = false; ServerFailureTechnical.IsVisible = false; _lastServerFailureTechnical = ""; SteamOpenStatus.Text = ""; }
    private void ShowServerFailureDetails(object? sender, RoutedEventArgs e)
    {
        ServerFailureTechnical.Text = DeveloperEnabled ? _lastServerFailureTechnical : DiagnosticPrivacy.ForPlayer(_lastServerFailureTechnical);
        ServerFailureTechnical.IsVisible = !ServerFailureTechnical.IsVisible;
        RichLogList.IsVisible = true; JournalToggle.Content = "Свернуть журнал"; RenderVisibleLog();
    }
    private void OpenSteamFromFailure(object? sender, RoutedEventArgs e)
    {
        if (!OpenSteamAction.IsVisible) return;
        try { Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); SteamOpenStatus.Text = "Запрос открытия Steam передан Windows. Войдите в аккаунт, затем повторите запуск."; }
        catch (Exception ex) { SteamOpenStatus.Text = "Не удалось открыть Steam. Запустите его вручную."; AppendLog("Открытие Steam: " + ex.Message); }
    }
    private void UpdateServerScenario()
    {
        if (ServerScenarioHelp is null) return;
        bool steam = _serverRunning ? _activeServerSteam : NetworkMode.SelectedIndex == 1;
        ServerScenarioHelp.Text = steam
            ? "Сценарий Steam: Steam должен быть запущен на этом ПК. Друзья входят через приглашение внутри игры. Автоматического вступления из списка нет; доступность между сетями требует проверки."
            : "Сценарий IP: запуск без Steam-флага. Друзья используют доступный адрес и игровой порт. Может потребоваться настройка сети хоста; порты автоматически не открываются.";
    }
    private void RenderRichJournal(string text)
    {
        RichLogList.Height = DeveloperEnabled ? 180 : 120;
        if (!RichLogList.IsVisible || _richLogText == text) return;
        _richLogText = text;
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(1000).Select(line =>
        {
            var color = System.Text.RegularExpressions.Regex.IsMatch(line, @"(?i)ошибка|ошибки|ошибке|\berror\b|exception|fatal|не удалось|код выхода: -\d") ? JournalError :
                System.Text.RegularExpressions.Regex.IsMatch(line, @"(?i)предупрежд|\bwarning\b|не проверен|не подтвержд|неизвест") ? JournalWarning : JournalNormal;
            return new JournalRow(line.TrimEnd('\r'), color);
        }).ToArray();
        RichLogList.ItemsSource = rows;
        if (rows.Length != 0) RichLogList.ScrollIntoView(rows[^1]);
    }
    private async void CopyJournal(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is null) return; await Clipboard.SetTextAsync(LogBox.Text ?? ""); }
        catch (Exception ex) { AppendLog("Не удалось скопировать журнал: " + ex.Message); }
    }
}
