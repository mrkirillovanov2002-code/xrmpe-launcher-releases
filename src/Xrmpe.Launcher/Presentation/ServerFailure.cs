namespace Xrmpe.Launcher.Presentation;

public sealed record ServerFailure(string Reason, bool CanOpenSteam, string Technical)
{
    public static ServerFailure FromException(Exception exception)
    {
        // Match the actual existing engine preflight diagnostic, never an exit code.
        bool noSteam = exception is InvalidOperationException && exception.Message == "Сначала запустите Steam и войдите в свой аккаунт.";
        var reason = noSteam ? "Запущенный Steam не найден. Откройте Steam, войдите в аккаунт и повторите запуск." :
            exception is UnauthorizedAccessException ? "Недостаточно прав для запуска или чтения необходимых файлов. Подробности доступны в журнале." :
            exception is FileNotFoundException ? "Не найден необходимый файл: " + DiagnosticPrivacy.ForPlayer(exception.Message) :
            "Не удалось запустить сервер: " + DiagnosticPrivacy.ForPlayer(exception.Message);
        return new(reason, noSteam, exception.ToString());
    }
    public static ServerFailure FromExit(int code) => new(
        $"Процесс сервера завершился с кодом {code}. Причина не определена; подробности доступны в журнале.", false, $"Dedicated exit code: {code}. Код выхода не определяет причину и не доказывает ошибку Steam.");
}
