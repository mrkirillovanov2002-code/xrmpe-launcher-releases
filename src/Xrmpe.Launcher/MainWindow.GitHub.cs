using System.Text.Json;
using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xrmpe.SelfUpdate;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private sealed record InternetPreferences(string Repository, int Source);
    private GitHubOffer? _githubOffer;
    private bool _githubChecking;
    private bool _githubStartupNetworkFailure;
    private bool _internetInitialized;
    private readonly CancellationTokenSource _internetLifetime = new();
    private bool GitHubSelected => LauncherUpdateSource.SelectedIndex != 1;
    private string? _internetPreferencesFile = null;
    private string _approvedRepository = "";
    private string? _pendingRepository;
    private string InternetPreferencesPath => _internetPreferencesFile ?? Path.Combine(_dataFolder, "launcher-internet.json");

    private void InitializeInternetUpdates()
    {
        try
        {
            if (File.Exists(InternetPreferencesPath))
            {
                if (new FileInfo(InternetPreferencesPath).Length > 8192) throw new InvalidDataException("Настройки интернет-канала слишком большие.");
                var settings = JsonSerializer.Deserialize<InternetPreferences>(File.ReadAllText(InternetPreferencesPath));
                if (settings is not null) { GitHubRepository.Text = ReleaseTrust.Repository(settings.Repository); LauncherUpdateSource.SelectedIndex = settings.Source == 1 ? 1 : 0; }
            }
        }
        catch (Exception ex) { AppendLog("Настройки GitHub: " + ex.Message); }
        _approvedRepository = GitHubRepository.Text ?? "";
        _internetInitialized = true;
        UpdateSourcePresentation();
        Opened += async (_, _) =>
        {
            if (GitHubSelected && ReleaseTrust.Configured(GitHubRepository.Text ?? ""))
            {
                await Task.Delay(800);
                await CheckGitHubUpdate(true);
                if (_githubStartupNetworkFailure)
                {
                    try
                    {
                        // One bounded retry for VPN/network initialization after startup.
                        await Task.Delay(TimeSpan.FromSeconds(30), _internetLifetime.Token);
                        if (GitHubSelected) await CheckGitHubUpdate(true);
                    }
                    catch (OperationCanceledException) { }
                }
            }
        };
        Closed += (_, _) => _internetLifetime.Cancel();
    }
    private void UpdateSourcePresentation()
    {
        if (GitHubFields is null) return;
        GitHubFields.IsVisible = GitHubSelected;
        LauncherReleaseNotes.Text = "";
        _githubOffer = null; _launcherRelease = null;
        if (!_internetInitialized) return;
        LauncherUpdateStatus.Text = $"Текущая версия: {CurrentLauncherVersion.ToString(3)}. " +
            (GitHubSelected ? "Проверка GitHub при запуске и по кнопке. Установка — только по вашей команде." : "Проверка доверенного HTTP-хоста LAN/VPN по кнопке. Игровые обновления — отдельно.");
        RefreshLaunchControls();
    }
    private void LauncherSourceChanged(object? sender, SelectionChangedEventArgs e)
    { if (_internetInitialized) GitHubRepository.Text = _approvedRepository; UpdateSourcePresentation(); SaveInternetPreferences(); }
    private void GitHubRepositoryChanged(object? sender, RoutedEventArgs e)
    {
        if (!_internetInitialized) return;
        var proposed = GitHubRepository.Text ?? ""; GitHubRepository.Text = _approvedRepository;
        if (!DeveloperEnabled || proposed == _approvedRepository) return;
        try
        {
            proposed = ReleaseTrust.Repository(proposed);
            if (!ReleaseTrust.Configured(proposed)) throw new ArgumentException("Укажите настоящий публичный репозиторий владелец/репозиторий.");
            _pendingRepository = proposed;
            RepositoryChangeText.Text = $"Изменить источник обновлений?\n{_approvedRepository} → {proposed}\n\nНеверный адрес может нарушить получение обновлений. Проверки подписи и хешей останутся обязательными.";
            RepositoryChangeConfirmation.IsVisible = true;
        }
        catch (Exception ex) { LauncherUpdateStatus.Text = ex.Message; }
    }
    private void CancelRepositoryEdit(object? sender, RoutedEventArgs e)
    { _pendingRepository = null; RepositoryChangeConfirmation.IsVisible = false; if (_internetInitialized) GitHubRepository.Text = _approvedRepository; }
    private void ConfirmRepositoryEdit(object? sender, RoutedEventArgs e)
    {
        if (!DeveloperEnabled || _pendingRepository is not { } proposed) return;
        if (_updateBusy || _githubChecking || _updateHost is not null) { LauncherUpdateStatus.Text = "Дождитесь завершения обновления или остановите раздачу."; return; }
        var previous = _approvedRepository;
        _approvedRepository = proposed; GitHubRepository.Text = proposed;
        if (!SaveInternetPreferences()) { _approvedRepository = previous; GitHubRepository.Text = previous; return; }
        CancelRepositoryEdit(sender, e); UpdateSourcePresentation();
    }
    private bool SaveInternetPreferences()
    {
        if (!_internetInitialized) return false;
        try
        {
            var repository = ReleaseTrust.Repository(_approvedRepository);
            Directory.CreateDirectory(Path.GetDirectoryName(InternetPreferencesPath)!);
            File.WriteAllText(InternetPreferencesPath + ".tmp", JsonSerializer.Serialize(new InternetPreferences(repository, GitHubSelected ? 0 : 1)));
            File.Move(InternetPreferencesPath + ".tmp", InternetPreferencesPath, true);
            return true;
        }
        catch (Exception ex) { LauncherUpdateStatus.Text = "Настройки интернет-канала не сохранены: " + ex.Message; return false; }
    }
    private async Task CheckGitHubUpdate(bool automatic)
    {
        if (_githubChecking) return;
        if (!ReleaseTrust.Configured(GitHubRepository.Text ?? ""))
        { LauncherUpdateStatus.Text = "Укажите настоящий публичный репозиторий вместо OWNER/REPOSITORY. Публикация ещё не настроена."; return; }
        _githubOffer = null; _launcherRelease = null; _githubChecking = true;
        _githubStartupNetworkFailure = false;
        RefreshUpdateControls();
        try
        {
            var repository = ReleaseTrust.Repository(GitHubRepository.Text ?? "");
            LauncherUpdateStatus.Text = "GitHub: проверка опубликованной версии и подписи…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_internetLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = new GitHubReleaseClient();
            var offer = await client.Check(repository, timeout.Token);
            if (!GitHubSelected || repository != ReleaseTrust.Repository(GitHubRepository.Text ?? "")) return;
            var newer = Version.Parse(offer.Manifest.Release.Version) > CurrentLauncherVersion;
            if (newer) _githubOffer = offer;
            LauncherUpdateStatus.Text = newer
                ? $"Доступна новая версия лаунчера: {offer.Manifest.Release.Version} · {offer.Manifest.Archive.Size / 1048576d:F1} МиБ. Подпись издателя проверена."
                : $"Лаунчер актуален: {CurrentLauncherVersion.ToString(3)}. В GitHub: {offer.Manifest.Release.Version}.";
            LauncherReleaseNotes.Text = offer.Manifest.Notes;
            if (automatic && newer && !_running && !_serverRunning && !_updateBusy) Status.Text = $"● Доступна версия лаунчера {offer.Manifest.Release.Version} — раздел «Обновления»";
        }
        catch (Exception ex)
        {
            if (!GitHubSelected) return;
            var category = ex switch
            {
                OperationCanceledException when !_internetLifetime.IsCancellationRequested => "timeout",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "release_not_found",
                HttpRequestException => "network_or_github",
                InvalidDataException or System.Security.Cryptography.CryptographicException => "verification_failed",
                _ => "unexpected"
            };
            _githubStartupNetworkFailure = automatic && category is "timeout" or "network_or_github";
            var explanation = category switch
            {
                "timeout" => "GitHub не ответил за 15 секунд. Проверьте интернет/VPN и повторите проверку кнопкой.",
                "release_not_found" => "GitHub не нашёл опубликованный релиз. Проверьте выбранный репозиторий.",
                "network_or_github" => "Нет связи с GitHub или сервис вернул ошибку. Проверьте интернет/VPN и повторите проверку кнопкой.",
                "verification_failed" => "Проверка подписи или файлов обновления не прошла. Установка заблокирована.",
                _ => "Не удалось проверить обновление. Повторите проверку кнопкой или откройте журнал."
            };
            LauncherUpdateStatus.Text = explanation + " Играть можно без обновления.";
            try
            {
                var report = Path.Combine(_dataFolder, "Logs", "launcher-update-diagnostic.json");
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                var json = JsonSerializer.Serialize(new { schema = 1, category, channel = "github", automatic,
                    current_version = CurrentLauncherVersion.ToString(4), repository = _approvedRepository,
                    http_status = (ex as HttpRequestException)?.StatusCode is { } status ? (int?)status : null });
                File.WriteAllText(report + ".tmp", json);
                File.Move(report + ".tmp", report, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            AppendLog(LauncherUpdateStatus.Text);
        }
        finally { _githubChecking = false; RefreshUpdateControls(); }
    }
}
