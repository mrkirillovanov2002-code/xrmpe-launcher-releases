using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xrmpe.SelfUpdate;
using Xrmpe.Launcher.Updates;

var project = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var installedMode = args.Length == 5 && args[1] == "--installed";
var releaseMode = args.Length >= 4 && args[1] == "--releases";
var currentHelper = args.Contains("--current-helper");
var release0 = installedMode || releaseMode ? Path.GetFullPath(args[2]) : Path.Combine(project, "artifacts/self-update-preview/Bootstrap/Payload");
var release1 = installedMode || releaseMode ? Path.GetFullPath(args[3]) : Path.Combine(project, "artifacts/self-update-preview/Release-0.3.1");
var work = Path.Combine(project, "artifacts/self-update-test-evidence/run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Copy(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var path in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
    {
        var to = Path.Combine(target,Path.GetRelativePath(source,path)); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(path,to);
    }
}
async Task Throws(Func<Task> action, string label)
{
    try { await action(); } catch { Console.WriteLine("PASS: rejected " + label); return; }
    throw new Exception("Expected rejection: " + label);
}
var old = ReleasePolicy.Read(release0); var newer = ReleasePolicy.Read(release1);
Assert(Version.Parse(newer.Version) > Version.Parse(old.Version), "Actual test versions");
string FourPart(string version) { var v = Version.Parse(version); return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString(4); }
Assert(FileVersionInfo.GetVersionInfo(Path.Combine(release0,"Xrmpe.Launcher.dll")).FileVersion == FourPart(old.Version), "Old file version");
Assert(FileVersionInfo.GetVersionInfo(Path.Combine(release1,"Xrmpe.Launcher.dll")).FileVersion == FourPart(newer.Version), "New file version");
var game = Path.Combine(work,"Игра отдельно"); Directory.CreateDirectory(Path.Combine(game,"bin"));
await File.WriteAllTextAsync(Path.Combine(game,"bin/game.dll"),"game stays unchanged");
var gameHash = await ReleasePolicy.Hash(Path.Combine(game,"bin/game.dll"));
var token = UpdateHost.GenerateToken();
await using var host = new UpdateHost();
await host.StartAsync(game,IPAddress.Loopback,installedMode ? 8787 : 0,token,1048576,false,_=>{},default,()=>{},release1);
using var client = new LauncherUpdateClient(host.Address!,token);
var remote = await client.Check(default);
Assert(remote.Version == newer.Version, "HTTP release version");
using (var wrong = new LauncherUpdateClient(host.Address!,new string('a',32))) await Throws(async()=>{await wrong.Check(default);},"wrong token");
var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xrmpe.CustomLauncher","SelfUpdate",Guid.NewGuid().ToString("N"));
await client.Download(remote,stage,(_,_,_)=>{},default);
await ReleasePolicy.Verify(stage,newer);
await Throws(()=>ReleasePolicy.Verify(stage,newer with {Version="0.9.0"}),"manifest/assembly version mismatch");
Console.WriteLine("PASS: authenticated download over existing HTTP host, sizes and SHA-256, independent game/launcher manifests.");
foreach (var bad in new[] { "../xrEngine.exe", "bin/xrEngine.exe", "C:/evil.exe", "user-settings.json", "saves/test.sav", "Xrmpe.Launcher.dll:ads" })
    await Throws(()=>{ReleasePolicy.ValidateName(bad);return Task.CompletedTask;},"manifest path "+bad);
var target = installedMode ? Path.GetFullPath(args[4]) : Path.Combine(work,"Получатель пробелы","app");
if (!installedMode) Copy(release0,target);
await ReleasePolicy.Verify(target,old);
var uninstallBefore = Directory.GetFiles(target,"unins*").ToDictionary(p=>p,p=>File.ReadAllBytes(p));
if (installedMode) Assert(uninstallBefore.Count >= 2, "Real installed uninstaller exists");
if (installedMode)
{
    using var installedRollback = new Installer(target);
    var failAt = newer.Files.FindIndex(f=>f.Path=="Xrmpe.Launcher.dll")+1;
    await Throws(()=>installedRollback.Apply(stage,newer,n=>{if(n==failAt)throw new IOException("Installed rollback fixture");}),"partial replacement of installed version");
    await ReleasePolicy.Verify(target,old);
    foreach(var item in uninstallBefore) Assert(File.ReadAllBytes(item.Key).SequenceEqual(item.Value),"Uninstaller unchanged after installed rollback");
    Console.WriteLine("PASS: rollback of real installed version before successful self-update.");
}
await File.WriteAllTextAsync(Path.Combine(target,"user-settings.json"),"keep local settings");
Directory.CreateDirectory(Path.Combine(target,"saves")); await File.WriteAllTextAsync(Path.Combine(target,"saves/keep.sav"),"save");
var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xrmpe.CustomLauncher");
var settingsBefore = Directory.Exists(profile) ? Directory.GetFiles(profile,"*.json").Where(p=>Path.GetFileName(p)!="self-update-result.json").ToDictionary(p=>p,p=>File.ReadAllBytes(p)) : new Dictionary<string,byte[]>();
var oldExe = Path.GetFullPath(Path.Combine(target,ReleasePolicy.Executable));
using var parent = Process.Start(new ProcessStartInfo(oldExe){UseShellExecute=false,WorkingDirectory=target})!;
var wait = Stopwatch.StartNew();
while(parent.MainWindowHandle==0 && !parent.HasExited && wait.Elapsed<TimeSpan.FromSeconds(20)){ await Task.Delay(100); parent.Refresh(); }
Assert(!parent.HasExited && parent.MainWindowHandle!=0,"Old real launcher opened");
var helper = Path.Combine(stage,"updater-helper.exe"); File.Copy(Path.Combine(currentHelper ? release1 : release0,"Xrmpe.Updater.exe"),helper);
Console.WriteLine("Helper scope: " + (currentHelper ? "current preview helper; not proof of legacy installed-helper compatibility" : "original installed-version helper"));
var jobPath = Path.Combine(stage,"job.json");
await File.WriteAllTextAsync(jobPath,JsonSerializer.Serialize(new InstallJob(target,stage,parent.Id,parent.StartTime.ToUniversalTime().Ticks)));
var info = new ProcessStartInfo(helper){WorkingDirectory=stage,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}; info.ArgumentList.Add(jobPath);
using var updater = Process.Start(info)!;
var stdout = updater.StandardOutput.ReadToEndAsync(); var stderr = updater.StandardError.ReadToEndAsync();
wait.Restart();
while(!File.Exists(Path.Combine(stage,"updater.ready")) && !updater.HasExited && wait.Elapsed<TimeSpan.FromSeconds(30)) await Task.Delay(100);
if (!File.Exists(Path.Combine(stage,"updater.ready"))) { Console.WriteLine(await stdout); Console.WriteLine(await stderr); parent.CloseMainWindow(); throw new Exception("Updater ready"); }
await ReleasePolicy.Verify(target,old);
Assert(!updater.HasExited,"Updater waits for old EXE instead of overwriting it");
Assert(parent.CloseMainWindow(),"Close old real launcher");
await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
if (updater.ExitCode != 0) { Console.WriteLine(await stdout); Console.WriteLine(await stderr); }
Assert(updater.ExitCode==0,"Updater success");
await ReleasePolicy.Verify(target,newer);
Assert(Directory.GetFiles(stage,"health-*").Length==1,"New real launcher confirmed window open");
var restarted = Process.GetProcessesByName("Xrmpe.Launcher").Where(p=> {try{return string.Equals(p.MainModule?.FileName,oldExe,StringComparison.OrdinalIgnoreCase);}catch{return false;}}).ToList();
Assert(restarted.Count==1,"Exactly one restarted launcher");
foreach(var process in restarted){process.CloseMainWindow();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));process.Dispose();}
Console.WriteLine(await stdout); Console.WriteLine(await stderr);
Assert(await File.ReadAllTextAsync(Path.Combine(target,"user-settings.json"))=="keep local settings","Local settings survived");
Assert(await File.ReadAllTextAsync(Path.Combine(target,"saves/keep.sav"))=="save","Save survived");
foreach(var item in settingsBefore) Assert(File.ReadAllBytes(item.Key).SequenceEqual(item.Value),"Actual profile settings/history unchanged: "+item.Key);
Assert(await ReleasePolicy.Hash(Path.Combine(game,"bin/game.dll"))==gameHash,"Game unchanged");
foreach(var item in uninstallBefore) Assert(File.ReadAllBytes(item.Key).SequenceEqual(item.Value),"Uninstaller preserved: "+item.Key);
using(var gameClient = new UpdateClient(Path.Combine(work,"Игра получателя"),host.Address!,token,()=>{}))
{
    Directory.CreateDirectory(Path.Combine(work,"Игра получателя"));
    var plan=await gameClient.CheckAsync(_=>{},default);
    Assert(plan.Count==1,"Independent game update plan");
    await gameClient.ApplyAsync(plan,_=>{},_=>{},default);
    Assert(await ReleasePolicy.Hash(Path.Combine(work,"Игра получателя/bin/game.dll"))==gameHash,"Independent game update SHA-256");
}
Console.WriteLine($"PASS: real {old.Version} EXE exit -> separate updater -> file replacement -> real {newer.Version} window restart, settings/history/saves/game/uninstaller preserved; independent game update via same HTTP host {host.Address}.");
var rollback = Path.Combine(work,"Откат"); Copy(release0,rollback);
var install = new Installer(rollback);
var faultAfter = newer.Files.FindIndex(f=>f.Path=="Xrmpe.Launcher.dll") + 1;
try { await Throws(()=>install.Apply(stage,newer,n=>{if(n==faultAfter)throw new IOException("Injected replacement failure after changed launcher DLL");}),"replacement fault after changed launcher DLL"); }
finally { install.Dispose(); }
await ReleasePolicy.Verify(rollback,old);
Console.WriteLine($"PASS: complete rollback to {old.Version} after partial replacement.");
var corrupt = Path.Combine(work,"Повреждённый пакет"); Copy(stage,corrupt);
var damaged = Path.Combine(corrupt,"Xrmpe.Launcher.dll");
using(var stream = new FileStream(damaged,FileMode.Open,FileAccess.Write)){stream.WriteByte(0xFF);}
var safe = new Installer(rollback);
try {await Throws(()=>safe.Apply(corrupt,newer),"corrupted SHA-256 before replacement");}finally{safe.Dispose();}
await ReleasePolicy.Verify(rollback,old);
// The HTTP host freezes hashes at startup; corrupt a served fixture afterwards to test client verification.
var badHostRoot = Path.Combine(work,"Источник повреждения"); Copy(release1,badHostRoot);
await using(var badHost=new UpdateHost())
{
    await badHost.StartAsync(game,IPAddress.Loopback,0,token,1048576,false,_=>{},default,()=>{},badHostRoot);
    var badDll = Path.Combine(badHostRoot,"Xrmpe.Launcher.dll");
    using(var stream=new FileStream(badDll,FileMode.Open,FileAccess.Write)){stream.WriteByte(0xFF);}
    using var badClient=new LauncherUpdateClient(badHost.Address!,token);
    await Throws(()=>badClient.Download(newer,Path.Combine(work,"Загрузка брака"),(_,_,_)=>{},default),"damaged HTTP payload");
}
Console.WriteLine("PASS: corrupt download and corrupt staged file rejected; previous version remains intact.");
// A valid-hash but broken EXE must trigger rollback after the actual helper tries restart.
var brokenStage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xrmpe.CustomLauncher","SelfUpdate",Guid.NewGuid().ToString("N"));
Copy(stage, brokenStage);
var brokenExe = Path.Combine(brokenStage,ReleasePolicy.Executable);
await File.WriteAllTextAsync(brokenExe,"Not a Windows executable");
var brokenRelease = newer with { Files = newer.Files.Select(f=>f.Path==ReleasePolicy.Executable ? f with { Size=new FileInfo(brokenExe).Length, Sha256=ReleasePolicy.Hash(brokenExe).GetAwaiter().GetResult() } : f).ToList() };
await File.WriteAllTextAsync(Path.Combine(brokenStage,ReleasePolicy.ManifestName),JsonSerializer.Serialize(brokenRelease));
var brokenJob = Path.Combine(brokenStage,"broken-job.json");
await File.WriteAllTextAsync(brokenJob,JsonSerializer.Serialize(new InstallJob(rollback,brokenStage,0,0)));
var brokenInfo = new ProcessStartInfo(Path.Combine(brokenStage,"updater-helper.exe")){UseShellExecute=false}; brokenInfo.ArgumentList.Add(brokenJob);
using(var brokenUpdater=Process.Start(brokenInfo)!){await brokenUpdater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));Assert(brokenUpdater.ExitCode==1,"Broken restart fails");}
await ReleasePolicy.Verify(rollback,old);
var rollbackExe=Path.GetFullPath(Path.Combine(rollback,ReleasePolicy.Executable));
var restoredProcesses=Process.GetProcessesByName("Xrmpe.Launcher").Where(p=>{try{return string.Equals(p.MainModule?.FileName,rollbackExe,StringComparison.OrdinalIgnoreCase);}catch{return false;}}).ToList();
Assert(restoredProcesses.Count==1,"Old version restarted after failed new EXE");
foreach(var process in restoredProcesses){for(var n=0;n<50&&process.MainWindowHandle==0;n++){await Task.Delay(100);process.Refresh();}Assert(process.CloseMainWindow(),"Close restored version");await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));process.Dispose();}
Console.WriteLine($"PASS: actual updater rolls back and restarts {old.Version} when valid-hash new EXE cannot start.");
if(OperatingSystem.IsWindows())
{
    var denied=Path.Combine(work,"Нет прав записи");Directory.CreateDirectory(denied);
    var infoDir=new DirectoryInfo(denied);
    var original=System.IO.FileSystemAclExtensions.GetAccessControl(infoDir);
    var acl=System.IO.FileSystemAclExtensions.GetAccessControl(infoDir);
    var sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!;
    acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,System.Security.AccessControl.FileSystemRights.Write,System.Security.AccessControl.AccessControlType.Deny));
    System.IO.FileSystemAclExtensions.SetAccessControl(infoDir,acl);
    try{await Throws(()=>{ReleasePolicy.EnsureWritable(denied);return Task.CompletedTask;},"real Windows ACL write denial (Program Files handling)");}
    finally{System.IO.FileSystemAclExtensions.SetAccessControl(infoDir,original);}
}
Console.WriteLine("Evidence: "+work);
