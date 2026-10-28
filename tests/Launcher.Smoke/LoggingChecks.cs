using System.Diagnostics;
using System.Text;
using Xrmpe.Launcher;

internal static class LoggingChecks
{
    public static async Task RunAsync()
    {
        var framed = new List<string>();
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("one\r\ntwo\rthree\n" + new string('x', 100000) + "\nlast"))))
            await ProcessRunner.ReadOutputAsync(reader, "", framed.Add);
        if (framed.Count != 5 || framed[0] != "one" || framed[1] != "two" || framed[2] != "three" || framed[4] != "last" || framed[3].Length > 600)
            throw new Exception("Output line framing/truncation failed");

        await using var journal = new BufferedLog();
        var clock = Stopwatch.StartNew();
        Parallel.For(0, 100000, i => journal.Append("line " + i));
        await Task.Delay(500);
        long version = -1;
        var snapshot = journal.Snapshot(ref version) ?? "";
        if (snapshot.Length > BufferedLog.MaxTextLength || snapshot.Split(Environment.NewLine).Length > 1000 || !snapshot.Contains("пропущено"))
            throw new Exception("Flood bounds/drop notification failed");
        journal.Clear();
        if (journal.Snapshot(ref version) != "") throw new Exception("Clear did not reset queues");
        await Task.Delay(300);
        if (journal.Snapshot(ref version) is string stale && stale.Length > 0) throw new Exception("Clear replayed stale output");
        journal.Append("AFTER_CLEAR");
        await Task.Delay(300);
        if (!(journal.Snapshot(ref version) ?? "").Contains("AFTER_CLEAR")) throw new Exception("Logging did not recover after clear");

        var runner = new ProcessRunner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int launches = 0, stdout = 0, stderr = 0;
        void Log(string message)
        {
            if (message.Contains("запущен (PID")) { Interlocked.Increment(ref launches); started.TrySetResult(); }
            if (message.Contains("stdout flood")) Interlocked.Increment(ref stdout);
            if (message.Contains("stderr flood")) Interlocked.Increment(ref stderr);
            journal.Append(message);
        }
        var exe = Environment.ProcessPath ?? throw new Exception("Missing test executable");
        ProcessStartInfo Start(string arg) => new(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { arg }
        };
        using var independent = Process.Start(Start("--idle")) ?? throw new Exception("Cannot start independent test process");
        var run = runner.RunAsync(Start("--log-flood"), "flood fixture", Log);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { _ = runner.RunAsync(Start("--log-flood"), "duplicate", Log); throw new Exception("Duplicate process accepted"); }
            catch (InvalidOperationException) { }
            var heartbeat = 0;
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(100);
                heartbeat++;
                snapshot = journal.Snapshot(ref version) ?? snapshot;
                if (snapshot.Length > BufferedLog.MaxTextLength || snapshot.Split(Environment.NewLine).Length > 1000)
                    throw new Exception("Live process flood exceeded log budgets");
            }
            if (heartbeat != 10 || launches != 1 || stdout == 0 || stderr == 0) throw new Exception("Concurrent stdout/stderr or process guard failed");
            await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            if (independent.HasExited) throw new Exception("Stop killed independent client");
            await Task.Delay(250);
            if (launches != 1) throw new Exception("Unexpected process restart");
            Console.WriteLine($"PASS: 100000 buffered lines, <=1000 retained, <=128k chars, clear, framing, stdout/stderr flood, heartbeat, duplicate guard, targeted stop ({clock.ElapsedMilliseconds} ms).");
        }
        finally
        {
            await runner.StopAsync();
            if (!independent.HasExited) { independent.Kill(); await independent.WaitForExitAsync(); }
        }
    }
}
