using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Content;

internal static class LocalSession
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task Run(string[] args)
    {
        var game = Path.GetFullPath(args[0]);
        var expected = Path.GetFullPath("backups/modes-ui-before-20261017/isolated-game");
        if (!game.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new Exception("Only existing isolated game permitted");
        ContentPaths.NoLinks(game);
        if (Directory.EnumerateFileSystemEntries(game,"*",SearchOption.AllDirectories).Any(p => (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)) throw new Exception("No links");
        var addon=AddonCatalog.Read(Path.Combine(game,"addons/xrmpe_pvp.addon"));
        if(!addon.Healthy || addon.Version!="1.4.7") throw new Exception("PvP 1.4.7 required");
        bool steam=args.Contains("--steam-session");
        if(steam && Process.GetProcessesByName("steam").Length==0) throw new Exception("Steam not running; no account/login automation");
        var evidence=Path.GetFullPath(Path.Combine("artifacts/local-multiplayer-test-20261017",DateTime.Now.ToString("HHmmss")+(steam?"-steam":"-ip")));
        Directory.CreateDirectory(evidence);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding=Encoding.GetEncoding(1251);
        var owned=new List<Process>(); var drains=new List<Task>(); var commands=new List<object>(); var events=new List<object>();
        var isolatedBefore=Directory.EnumerateFiles(game,"*",SearchOption.AllDirectories).Select(p=>new {Path=p,Size=new FileInfo(p).Length,ModifiedUtc=File.GetLastWriteTimeUtc(p)}).ToArray();
        File.WriteAllText(Path.Combine(evidence,"isolated-inventory-before.json"),JsonSerializer.Serialize(isolatedBefore,Json));
        foreach(var dir in new[]{"_appdata_","_appdata_server_custom"})
        {
            var logs=Path.Combine(game,dir,"logs");
            if(Directory.Exists(logs)) foreach(var file in Directory.EnumerateFiles(logs,"*.log")) File.Copy(file,Path.Combine(evidence,"before-"+dir+"-"+Path.GetFileName(file)));
        }
        void Event(string name, object data) { events.Add(new {Utc=DateTime.UtcNow,Name=name,Data=data}); File.WriteAllText(Path.Combine(evidence,"events.json"),JsonSerializer.Serialize(events,Json)); }
        async Task Drain(StreamReader reader,string name)
        {
            await using var writer=new StreamWriter(Path.Combine(evidence,name),false,new UTF8Encoding(false));
            var buffer=new char[8192]; long written=0; int count;
            while((count=await reader.ReadAsync(buffer))>0) if(written<16*1024*1024){await writer.WriteAsync(buffer.AsMemory(0,count));written+=count;await writer.FlushAsync();}
        }
        Process Start(ProcessStartInfo info,string name)
        {
            if(!Path.GetFullPath(info.FileName).StartsWith(game+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Non-isolated executable");
            info.CreateNoWindow=false; info.WindowStyle=ProcessWindowStyle.Normal;
            info.StandardOutputEncoding=info.StandardErrorEncoding=encoding;
            var process=Process.Start(info)??throw new Exception("No process"); owned.Add(process);
            commands.Add(new{Name=name,Pid=process.Id,StartUtc=process.StartTime.ToUniversalTime(),Executable=info.FileName,Cwd=info.WorkingDirectory,Args=info.ArgumentList.ToArray()});
            File.WriteAllText(Path.Combine(evidence,"owned-processes-and-commands.json"),JsonSerializer.Serialize(commands,Json));
            drains.Add(Drain(process.StandardOutput,name+"-stdout.log")); drains.Add(Drain(process.StandardError,name+"-stderr.log"));
            Console.WriteLine($"START {name} PID={process.Id}; evidence={evidence}"); return process;
        }
        string ReadLog(string folder,string target)
        {
            var dir=Path.Combine(game,folder,"logs"); if(!Directory.Exists(dir))return "";
            var latest=Directory.EnumerateFiles(dir,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); if(latest is null)return "";
            try { using var stream=new FileStream(latest,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); using var reader=new StreamReader(stream,encoding,true); var text=reader.ReadToEnd(); File.WriteAllText(Path.Combine(evidence,target),text,new UTF8Encoding(false)); return text; } catch(IOException){return "";}
        }
        bool qr2=false, loaded=false; int? players=null; Process? client=null; string serverLog="",clientLog="";
        try
        {
            var info=LauncherService.CreateServerStartInfo(game,"mp_kpp_2","ZoneLink_local_test",32,5446,60);
            if(steam)LauncherService.EnableSteam(info);
            var server=Start(info,"server"); var clock=Stopwatch.StartNew();
            while(!server.HasExited&&clock.Elapsed.TotalSeconds<60)
            {
                await Task.Delay(1000); var reply=await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1",5446,5445)),CancellationToken.None);
                if(reply.Snapshot is {} snap && snap.Map=="mp_kpp_2" && snap.Name.Equals("ZoneLink_local_test",StringComparison.OrdinalIgnoreCase)) { qr2=true;players=snap.Players;Event("QR2 ready",snap);Console.WriteLine($"QR2 actual: {snap.Name} {snap.Map} {snap.Mode} {snap.Players}/{snap.MaxPlayers}");break; }
            }
            if(!qr2)throw new Exception("No confirmed QR2 from our dedicated");
            var ci=LauncherService.CreateClientStartInfo(game,5446); if(steam)LauncherService.EnableSteam(ci);
            client=Start(ci,"client1"); clock.Restart(); bool announced=false;
            while(!server.HasExited&&!client.HasExited&&clock.Elapsed.TotalSeconds<(args.Contains("--manual-assist")?900:300)&&!File.Exists(Path.Combine(evidence,"stop.flag")))
            {
                await Task.Delay(2000); serverLog=ReadLog("_appdata_server_custom","server-engine.log");clientLog=ReadLog("_appdata_","client1-engine.log");
                loaded=clientLog.Contains("net_start_client6")&&clientLog.Contains("End of synchronization")&&clientLog.Contains("mp_kpp_2");
                if(loaded&&!announced){Event("Client1 full synchronization",new{Elapsed=clock.Elapsed.TotalSeconds});Console.WriteLine("CLIENT1 MAP/SYNC MARKERS CONFIRMED — inspect actual game window; second client not started automatically.");announced=true;}
                if(((int)clock.Elapsed.TotalSeconds/2)%5==0)
                {
                    var reply=await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1",5446,5445)),CancellationToken.None);players=reply.Snapshot?.Players;
                    client.Refresh();
                    var state=new{ElapsedSeconds=clock.Elapsed.TotalSeconds,Mode=steam?"Steam":"IP",ServerPid=server.Id,ClientPid=client.Id,LogFullSyncMarkersSeen=loaded,Qr2Players=players,ClientLogBytes=clientLog.Length,ServerLogBytes=serverLog.Length,ClientWorkingSetMiB=client.WorkingSet64/1048576.0};
                    File.WriteAllText(Path.Combine(evidence,"live-status.json"),JsonSerializer.Serialize(state,Json)); Console.WriteLine(JsonSerializer.Serialize(state));
                }
                if(clientLog.Contains("FATAL ERROR")){Event("Client fatal",new{Elapsed=clock.Elapsed.TotalSeconds});break;}
            }
            serverLog=ReadLog("_appdata_server_custom","server-engine.log");clientLog=ReadLog("_appdata_","client1-engine.log");
            Event("Result",new{Qr2=qr2,Client1FullSynchronizationMarkersSeen=loaded,Qr2Players=players,ClientExited=client.HasExited,ClientExitCode=client.HasExited?(int?)client.ExitCode:null,ServerExited=server.HasExited,SecondClientManagedBySeparateController=true});
        }
        catch(Exception ex){Event("Exception",ex.ToString());Console.WriteLine(ex);}
        finally
        {
            ReadLog("_appdata_server_custom","server-engine.log");ReadLog("_appdata_","client1-engine.log");
            foreach(var process in owned.AsEnumerable().Reverse())
            {
                if(!process.HasExited){if(!process.CloseMainWindow()||!process.WaitForExit(5000)){process.Kill(true);await process.WaitForExitAsync();}}
                Event("Owned process stopped",new{Pid=process.Id,ExitCode=process.ExitCode});process.Dispose();
            }
            await Task.WhenAll(drains); ReadLog("_appdata_server_custom","server-engine.log");ReadLog("_appdata_","client1-engine.log");
            Console.WriteLine("ONLY CREATED PROCESSES STOPPED. EVIDENCE="+evidence);
        }
    }
}
