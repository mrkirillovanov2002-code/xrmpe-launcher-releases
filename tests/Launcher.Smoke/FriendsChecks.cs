using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xrmpe.Launcher;

internal static class FriendsChecks
{
    public static async Task Run()
    {
        ProcessStartInfo Start(string arg) => new(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { arg }
        };
        var runner = new ProcessRunner();
        await runner.StopAsync();
        var childId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync(Start("--child-parent"), "fixture", line => {
            var i = line.IndexOf("CHILD=");
            if (i >= 0 && int.TryParse(line[(i+6)..], out var id)) childId.TrySetResult(id);
        });
        Process? child = null;
        try {
            child = Process.GetProcessById(await childId.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.WhenAll(runner.StopAsync(), runner.StopAsync());
            await run;
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (runner.IsRunning) throw new Exception("Runner still active after stop");
            Console.WriteLine("PASS owned tree stopped; concurrent Stop idempotent");
        }
        finally { await runner.StopAsync(); if (child is not null) { if (!child.HasExited) child.Kill(); child.Dispose(); } }
        for (int i = 0; i < 12; i++) {
            var next = runner.RunAsync(Start("--exit-now"), "fixture", _=>{});
            await Task.WhenAll(next, runner.StopAsync(), runner.StopAsync());
        }
        await runner.RunAsync(Start("--exit-now"), "fixture", _=>{});
        await runner.StopAsync();
        Console.WriteLine("PASS exit/stop race x12, restart after stop, already exited");
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        try { await ServerReadiness.EnsureFreeAsync(new("127.0.0.1",port,port),default); throw new Exception("Occupied UDP accepted"); }
        catch (IOException) { Console.WriteLine("PASS occupied server port blocked"); }
        var life = new TaskCompletionSource<int>();
        var endpoint = new BrowserEndpoint("127.0.0.1",55446,55445,true);
        int probes = 0;
        var ready = await ServerReadiness.WaitAsync(endpoint,"Friends","mp_agroprom",life.Task,()=>true,default,
            TimeSpan.FromSeconds(5), (s,t) => Task.FromResult(++probes == 1 ? s : s with { Snapshot = new("Friends","mp_agroprom","dm",0,8,"open") }));
        if(probes != 2 || ready.Map != "mp_agroprom") throw new Exception("Readiness detection failed");
        Console.WriteLine("PASS readiness after actual probe success, not process appearance");
        try { await ServerReadiness.WaitAsync(endpoint,"Friends","mp_agroprom",Task.FromResult(3),()=>false,default); throw new Exception("Early exit accepted"); }
        catch (IOException) { Console.WriteLine("PASS premature exit blocked"); }
        try {
            await ServerReadiness.WaitAsync(endpoint,"Friends","mp_agroprom",life.Task,()=>true,default,
                TimeSpan.FromMilliseconds(100), (s,t)=>Task.FromResult(s with { Snapshot = new("Other","mp_arena","dm",0,8,"open") }));
            throw new Exception("Wrong server accepted");
        } catch(TimeoutException) { Console.WriteLine("PASS wrong map/name rejected and timeout bounded"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await ServerReadiness.WaitAsync(endpoint,"Friends",null,life.Task,()=>true,cancel.Token); throw new Exception("Cancellation ignored"); }
        catch(OperationCanceledException) { Console.WriteLine("PASS stop cancels readiness"); }
    }
}
