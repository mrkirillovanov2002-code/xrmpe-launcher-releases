using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xrmpe.Launcher;
using Xrmpe.Launcher.Content;

internal static class MapAudit
{
 static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
 public static async Task Run(string[] args)
 {
  var game=Path.GetFullPath("backups/modes-ui-before-20261017/isolated-game");
  var output=Path.GetFullPath("artifacts/pvp-maps-runtime-test-20261017"); Directory.CreateDirectory(output);
  if(Process.GetProcessesByName("xrEngine").Length!=0)throw new Exception("Existing game process; stop audit without touching it");
  foreach(var port in new[]{5445,55446,5450}){using var check=new UdpClient(new IPEndPoint(IPAddress.Any,port));}
  ContentPaths.NoLinks(game);
  if(Directory.EnumerateFileSystemEntries(game,"*",SearchOption.AllDirectories).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))throw new Exception("Linked isolated game");
  var addon=AddonCatalog.Read(Path.Combine(game,"addons/xrmpe_pvp.addon"));if(!addon.Healthy||addon.Version!="1.4.7")throw new Exception("Require PvP 1.4.7");
  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var enc=Encoding.GetEncoding(1251);
  var console=args.Contains("--audit-console");var rotation=args.Contains("--audit-rotation")||console;
  var clientAt=Array.IndexOf(args,"--audit-client"); var clientMode=clientAt>=0||rotation;
  var maps=rotation?new[]{"mp_agroprom"}:clientMode?new[]{args[clientAt+1]}:Directory.GetFiles(Path.Combine(game,"mp"),"mp_*.db").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
  foreach(var map in maps)
  {
   if(map is null||!Regex.IsMatch(map,@"\Amp_[a-z0-9_]+\z"))throw new Exception("Invalid map");
   var folder=Path.Combine(output,console?"console":rotation?"rotation":map+(clientMode?"-client":""));Directory.CreateDirectory(folder);
   var appName="_runtime_pvp_"+map+(console?"_console":rotation?"_rotation":clientMode?"_clienttest":"");var app=Path.Combine(game,appName);Directory.CreateDirectory(app);
   var cfgName="fsgame_runtime_"+map+(console?"_console":rotation?"_rotation":clientMode?"_clienttest":"")+".ltx";
   string MakeConfig(string template,string name,string dataName){var cfg=Regex.Replace(File.ReadAllText(Path.Combine(game,template)),@"(?m)^\$app_data_root\$[^\r\n]*",_=>"$app_data_root$ = true| false| $fs_root$| "+dataName+"\\");var path=Path.Combine(game,name);if(File.Exists(path)&&File.ReadAllText(path)!=cfg)throw new Exception("Refuse overwrite config");File.WriteAllText(path,cfg);return path;}
   MakeConfig("fsgame_sc.ltx",cfgName,appName);
   var si=LauncherService.CreateServerStartInfo(game,map,"ZoneLink_map_audit",32,55446,60);si.ArgumentList[4]="..\\"+cfgName;si.WindowStyle=ProcessWindowStyle.Hidden;
   if(rotation){File.WriteAllText(Path.Combine(app,"maprot_list.ltx"),"sv_addmap mp_agroprom/ver=1.0\r\nsv_addmap mp_arena/ver=1.0\r\n");si.ArgumentList[6]=si.ArgumentList[6].Replace("timelimit=61","timelimit=1");}
   if(console)si.WindowStyle=ProcessWindowStyle.Normal;
   var processes=new List<Process>();var drains=new List<Task>();var commands=new List<object>();
   async Task Drain(StreamReader r,string target){using var w=new StreamWriter(Path.Combine(folder,target));var b=new char[8192];long written=0;int n;while((n=await r.ReadAsync(b))>0)if(written<4*1024*1024){await w.WriteAsync(b.AsMemory(0,n));written+=n;}}
   Process Start(ProcessStartInfo info,string label){info.StandardOutputEncoding=info.StandardErrorEncoding=enc;var p=Process.Start(info)??throw new Exception("No process");processes.Add(p);commands.Add(new{Label=label,Pid=p.Id,StartUtc=p.StartTime.ToUniversalTime(),Exe=info.FileName,Cwd=info.WorkingDirectory,Args=info.ArgumentList.ToArray()});File.WriteAllText(Path.Combine(folder,"commands.json"),JsonSerializer.Serialize(commands,Json));drains.Add(Drain(p.StandardOutput,label+"-stdout.log"));drains.Add(Drain(p.StandardError,label+"-stderr.log"));return p;}
   string ReadLog(string dir,string name){var logs=Path.Combine(dir,"logs");var f=Directory.Exists(logs)?Directory.GetFiles(logs,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault():null;if(f is null)return "";try{using var s=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var r=new StreamReader(s,enc,true);var t=r.ReadToEnd();File.WriteAllText(Path.Combine(folder,name),t);return t;}catch(IOException){return "";}}
   bool qr=false,repeat=false;object? snapshot=null;string? error=null;string slog="",clog="";string? capp=null;bool? exited=null;int? exitCode=null;
   try
   {
    var server=Start(si,"server");var watch=Stopwatch.StartNew();
    while(!server.HasExited&&watch.Elapsed.TotalSeconds<35){await Task.Delay(700);var reply=await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1",55446,5445)),CancellationToken.None);if(reply.Snapshot is {} snap&&snap.Map==map&&snap.Name.Equals("ZoneLink_map_audit",StringComparison.OrdinalIgnoreCase)){snapshot=snap;qr=true;break;}}
    if(qr){await Task.Delay(1800);var r=await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1",55446,5445)),CancellationToken.None);repeat=!server.HasExited&&r.Snapshot?.Map==map;}
    slog=ReadLog(app,"server-engine.log");
    Console.WriteLine($"{map}: QR2={qr} repeated={repeat} serverExited={server.HasExited}");
    if(clientMode&&qr&&repeat){
     capp=Path.Combine(game,appName+"_player");Directory.CreateDirectory(capp);var user=Path.Combine(capp,"user.ltx");if(!File.Exists(user))File.Copy(Path.Combine(game,"_appdata_/user.ltx"),user);
     var cCfg="fsgame_runtime_player_"+map+(console?"_console":rotation?"_rotation":"")+".ltx";MakeConfig("fsgame.ltx",cCfg,appName+"_player");
     var ci=LauncherService.CreateRemoteClientStartInfo(game,"127.0.0.1",55446);ci.ArgumentList.Add("-fsltx");ci.ArgumentList.Add(cCfg);ci.WindowStyle=ProcessWindowStyle.Normal;
     var client=Start(ci,"client");watch.Restart();Console.WriteLine("CLIENT READY FOR VISUAL CHECK: "+map+" PID="+client.Id);
     var observations=new List<object>();
     while(!client.HasExited&&!server.HasExited&&watch.Elapsed.TotalSeconds<(console?600:180)&&!File.Exists(Path.Combine(folder,"stop.flag"))){await Task.Delay(1000);clog=ReadLog(capp,"client-engine.log");if(rotation&&(int)watch.Elapsed.TotalSeconds%3==0){var probe=await Qr2Query.ProbeAsync(new BrowserServer(new("127.0.0.1",55446,5445)),CancellationToken.None);observations.Add(new{Utc=DateTime.UtcNow,Elapsed=watch.Elapsed.TotalSeconds,Snapshot=probe.Snapshot});File.WriteAllText(Path.Combine(folder,"rotation-observations.json"),JsonSerializer.Serialize(observations,Json));if(!console)Console.WriteLine("ROTATION "+JsonSerializer.Serialize(probe.Snapshot));}if(clog.Contains("FATAL ERROR"))break;}
    }
    exited=server.HasExited;exitCode=server.HasExited?server.ExitCode:null;
    if(!qr)error=server.HasExited?"Server exited before matching QR2":"No matching QR2 within timeout";
   }
   catch(Exception ex){error=ex.ToString();Console.WriteLine(error);}
   finally
   {
    foreach(var p in processes.AsEnumerable().Reverse()){if(!p.HasExited){if(!p.CloseMainWindow()||!p.WaitForExit(1500)){p.Kill(true);await p.WaitForExitAsync();}}}await Task.WhenAll(drains);
    slog=ReadLog(app,"server-engine.log");if(capp!=null)clog=ReadLog(capp,"client-engine.log");
    var fatal=slog.Contains("FATAL ERROR")||clog.Contains("FATAL ERROR");
    File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new{Map=map,ResourceBytes=new FileInfo(Path.Combine(game,"mp",map+".db")).Length,Addon="1.4.7",Mode="dm",Qr2=qr,RepeatedQr2=repeat,Snapshot=snapshot,ServerExitedBeforeCleanup=exited,ServerExitCodeBeforeCleanup=exitCode,Error=error,FatalMarker=fatal,ClientChecked=clientMode,ClientFullSyncLog=clog.Contains("End of synchronization"),ClientLevelLoaded="Requires independent visual evidence; absence of buffered log marker is not failure",Stopped=processes.Select(p=>new{p.Id,p.HasExited,p.ExitCode})},Json));
    foreach(var p in processes)p.Dispose();
   }
   if(clientMode)break;
  }
 }
}
