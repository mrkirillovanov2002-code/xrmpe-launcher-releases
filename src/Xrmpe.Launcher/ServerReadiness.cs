using System.Net;
using System.Net.Sockets;

namespace Xrmpe.Launcher;

public static class ServerReadiness
{
    public static async Task EnsureFreeAsync(BrowserEndpoint endpoint, CancellationToken token)
    {
        // Reserve nothing permanently: the engine needs these ports itself.
        foreach (var port in new[] { endpoint.Port, endpoint.QueryPort }.Distinct())
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (SocketException ex) { throw new IOException($"Порт UDP {port} занят. Остановите прежний сервер или выберите свободный порт.", ex); }
        }
        var existing = await Qr2Query.ProbeAsync(new(endpoint), token);
        if (existing.Snapshot is not null) throw new IOException("По этому адресу уже отвечает сервер. Второй процесс не запущен.");
    }

    public static async Task<ServerSnapshot> WaitAsync(BrowserEndpoint endpoint, string name, string? map,
        Task<int> process, Func<bool> isRunning, CancellationToken token, TimeSpan? limit = null,
        Func<BrowserServer, CancellationToken, Task<BrowserServer>>? probe = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit ?? TimeSpan.FromMinutes(2));
        probe ??= Qr2Query.ProbeAsync;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.IsCompleted) throw new IOException($"Сервер завершился до готовности. Код: {await process}.");
                if (isRunning())
                {
                    var response = await probe(new(endpoint), timeout.Token);
                    if (!process.IsCompleted && isRunning() && response.Snapshot is { } snapshot &&
                        snapshot.Name == name.Trim() && (map is null || snapshot.Map.Equals(map, StringComparison.OrdinalIgnoreCase)))
                        return snapshot;
                }
                await Task.Delay(400, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Сервер не подтвердил готовность через QR2. Клиент не запущен. Проверьте журнал, query-порт и поддержку запроса выбранным режимом."); }
    }
}
