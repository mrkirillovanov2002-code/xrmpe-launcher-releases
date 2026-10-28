using System.Text;

namespace Xrmpe.Launcher;

// Producers never access UI controls or the disk. Both queues have fixed budgets.
public sealed class BufferedLog : IAsyncDisposable
{
    public const int MaxLines = 1000;
    public const int MaxLineLength = 512;
    public const int MaxTextLength = 128000;
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _visible = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private int _characters;
    private long _dropped;
    private long _version;
    private string? _file;
    public BufferedLog() => _pump = Task.Run(PumpAsync);
    public void SetFile(string path) { lock (_gate) _file = path; }

    public void Append(string message)
    {
        // Also bounds application-generated messages; process output is framed separately.
        using var reader = new StringReader(message);
        string? line;
        var count = 0;
        lock (_gate)
        {
            while ((line = reader.ReadLine()) is not null && count++ < MaxLines)
            {
                if (_pending.Count == MaxLines) { _pending.Dequeue(); _dropped++; }
                _pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] " + (line.Length > MaxLineLength ? line[..MaxLineLength] + " …" : line));
            }
        }
    }

    public void Clear()
    {
        lock (_gate) { _pending.Clear(); _visible.Clear(); _characters = 0; _dropped = 0; _version++; }
    }

    public string? Snapshot(ref long lastVersion)
    {
        lock (_gate)
        {
            if (lastVersion == _version) return null;
            lastVersion = _version;
            return string.Join(Environment.NewLine, _visible);
        }
    }

    private void AddVisible(string line)
    {
        _visible.Enqueue(line);
        _characters += line.Length + Environment.NewLine.Length;
        while (_visible.Count > MaxLines || _characters > MaxTextLength)
            _characters -= _visible.Dequeue().Length + Environment.NewLine.Length;
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                string batch;
                string? file;
                lock (_gate)
                {
                    if (_pending.Count == 0 && _dropped == 0) continue;
                    var lines = new List<string>(201);
                    if (_dropped > 0) { lines.Add($"[Ограничение потока: пропущено {_dropped} строк]"); _dropped = 0; }
                    while (_pending.Count > 0 && lines.Count < 200) lines.Add(_pending.Dequeue());
                    foreach (var line in lines) AddVisible(line);
                    _version++;
                    batch = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                    file = _file;
                }
                if (file is null) continue;
                try
                {
                    // Two files, each roughly <= 4 MiB; no unbounded disk growth.
                    if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(batch) > 4 * 1024 * 1024)
                        File.Move(file, file + ".1", overwrite: true);
                    await File.AppendAllTextAsync(file, batch, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    lock (_gate) { _file = null; AddVisible($"[Ошибка файла журнала: {ex.Message}]"); _version++; }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _pump.ConfigureAwait(false);
        _stop.Dispose();
    }
}
