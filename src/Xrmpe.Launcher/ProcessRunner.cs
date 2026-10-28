using System.Diagnostics;
using System.Text;

namespace Xrmpe.Launcher;

public sealed class ProcessRunner
{
    private readonly object _gate = new();
    private Process? _process;
    private bool _reserved;
    private bool _stopRequested;
    private Task<int>? _completion;
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }

    public Task<int> RunAsync(ProcessStartInfo start, string label, Action<string> log)
    {
        lock (_gate)
        {
            if (_reserved) throw new InvalidOperationException("Этот процесс уже запущен.");
            _reserved = true;
            _stopRequested = false;
        // Buffered ReadAsync can complete synchronously forever during a flood.
        // Start AND consume on a worker, never on Avalonia's synchronization context.
        return _completion = Task.Run(async () =>
        {
            using var process = new Process { StartInfo = start };
            try
            {
                lock (_gate)
                {
                    if (!process.Start()) throw new InvalidOperationException($"Не удалось запустить {label}.");
                    _process = process;
                    if (_stopRequested) process.Kill(entireProcessTree: true);
                }
                log($"{label} запущен (PID {process.Id}). Готовность сети ещё не подтверждена.");
                using var drain = new CancellationTokenSource();
                var readers = Task.WhenAll(
                    Task.Run(() => ReadOutputAsync(process.StandardOutput, $"[{label}] ", log, drain.Token)),
                    Task.Run(() => ReadOutputAsync(process.StandardError, $"[{label}, stderr] ", log, drain.Token)));
                await process.WaitForExitAsync().ConfigureAwait(false);
                try { await readers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    drain.Cancel();
                    try { await readers.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    log($"{label}: чтение оставшихся потоков прекращено после завершения процесса.");
                }
                return process.ExitCode;
            }
            finally { lock (_gate) { _process = null; _reserved = false; } }
        });
        }
    }

    public async Task StopAsync()
    {
        Task<int>? completion;
        lock (_gate)
        {
            _stopRequested = true;
            completion = _completion;
            var process = _process;
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Exit raced with Stop. */ }
            }
        }
        // Await the owner's completion, not a Process which it may already have disposed.
        if (completion is not null) await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public static async Task ReadOutputAsync(StreamReader reader, string prefix, Action<string> log, CancellationToken cancellationToken = default)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(BufferedLog.MaxLineLength);
        var truncated = false;
        var previousCr = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '\n' && previousCr) { previousCr = false; continue; }
                previousCr = c == '\r';
                if (c is '\r' or '\n')
                {
                    log(prefix + line + (truncated ? " …[строка сокращена]" : ""));
                    line.Clear(); truncated = false;
                }
                else if (line.Length < BufferedLog.MaxLineLength) line.Append(c);
                else truncated = true;
            }
        }
        if (line.Length > 0 || truncated) log(prefix + line + (truncated ? " …[строка сокращена]" : ""));
    }
}
