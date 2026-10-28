using Xrmpe.Launcher; using Xrmpe.Launcher.Content;
if (args.Length == 4 && args[0] == "--cosmetic-flow")
{
    var parent = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(parent);
    var testRoot = Path.Combine(parent, "case-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    var count = 0;
    void Pass(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    try
    {
        var referenceDds = Path.GetFullPath(args[3]);
        LocalArmbandsProfiles.Verify(referenceDds);
        Pass(true, "reference package accepted without warning");
        var data = Path.Combine(testRoot, "data");
        var fixtureGame = Path.Combine(testRoot, "fixtureGame");
        Directory.CreateDirectory(Path.Combine(fixtureGame, "addons"));
        Directory.CreateDirectory(Path.Combine(fixtureGame, "bin"));
        Directory.CreateDirectory(Path.Combine(fixtureGame, "bin", "dedicated"));
        File.WriteAllText(Path.Combine(fixtureGame, "addons", "xrmpe_pvp.addon"), "fixture only");
        File.WriteAllText(Path.Combine(fixtureGame, "bin", "xrEngine.exe"), "fixture never execute");
        File.WriteAllText(Path.Combine(fixtureGame, "bin", "dedicated", "xrEngine.exe"), "fixture never execute");
        File.WriteAllText(Path.Combine(fixtureGame, "fsgame_sc.ltx"), "fixture never execute");
        File.WriteAllText(Path.Combine(fixtureGame, "fsgame.ltx"), "$app_data_root$ = true| false| $fs_root$| user\\\n");
        var package = EmissiveWorkshop.InstalledPath(data);
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.Copy(Path.GetFullPath(args[2]), package);
        var digest = LocalArmbandsProfiles.ActualHash(package);
        CosmeticPackageSafety.Verify(package);
        try { LocalArmbandsProfiles.Verify(package, "emissive"); throw new Exception("Mismatch accepted silently"); }
        catch (CosmeticHashMismatchException ex)
        { Pass(ex.ActualHash == digest && ex.Stage == "локальный пакет" && ex.Size > 0, "mismatch records exact digest, stage and size"); }
        try { LocalArmbandsProfiles.Apply(LauncherService.CreateMenuStartInfo(fixtureGame), package, "emissive", digest); throw new Exception("Unproven build accepted"); }
        catch (InvalidDataException) { Pass(true, "force unavailable without trusted builder receipt"); }
        var receipt = Path.Combine(Path.GetDirectoryName(package)!, ".local-build.json");
        File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new EmissiveBuildReceipt(1, EmissiveWorkshop.BuilderZipHash, digest, 33, 34)));
        Pass(EmissiveWorkshop.IsTrustedLocalBuild(package, digest), "cosmetic-only trusted local build recognized");
        var initialChoice = new ModWorkspace("kept", [], true, "", "emissive");
        Pass(CosmeticChoicePolicy.Apply(initialChoice, CosmeticDecision.Cancel, digest) == initialChoice, "cancel keeps current selection");
        var withoutChoice = CosmeticChoicePolicy.Apply(initialChoice, CosmeticDecision.Without, digest);
        Pass(!withoutChoice.ArmbandsEnabled && withoutChoice.ArmbandsApprovedHash == "" && withoutChoice.ArmbandsApprovalBuilderHash == "", "without cosmetics clears override");
        var includeChoice = CosmeticChoicePolicy.Apply(initialChoice, CosmeticDecision.Include, digest);
        Pass(includeChoice.ArmbandsEnabled && includeChoice.ArmbandsApprovedHash == digest && includeChoice.ArmbandsApprovalBuilderHash == EmissiveWorkshop.BuilderZipHash, "force applies only to exact digest and builder version");
        var log = new List<string>();
        var first = LauncherService.CreateMenuStartInfo(fixtureGame);
        LocalArmbandsProfiles.Apply(first, package, "emissive", digest, log.Add);
        Pass(first.ArgumentList.Contains("-fsltx"), "approved cosmetic can be mounted");
        var mounted = Path.Combine(fixtureGame, ".zl-bands", LocalArmbandsProfiles.EmissiveHash[..16], "armbands.db");
        File.WriteAllText(mounted, "stale copy");
        var second = LauncherService.CreateSteamMenuStartInfo(fixtureGame);
        LocalArmbandsProfiles.Apply(second, package, "emissive", digest, log.Add);
        Pass(LocalArmbandsProfiles.ActualHash(mounted) == digest && log.Any(x => x.Contains("восстанавливаем")) && second.ArgumentList.Contains("-steam"), "stale mounted copy repaired; Steam retained");
        var off = new ModWorkspaceStore(Path.Combine(data, "mods.json"));
        off.Save(fixtureGame, "xrmpe_pvp.addon", new("", [], false, "", "emissive"));
        Pass(!off.Load(fixtureGame, "xrmpe_pvp.addon").ArmbandsEnabled && !LauncherService.CreateMenuStartInfo(fixtureGame).ArgumentList.Contains("-fsltx"), "continue without cosmetics preserves original launch");
        off.Save(fixtureGame, "xrmpe_pvp.addon", CosmeticChoicePolicy.Apply(off.Load(fixtureGame, "xrmpe_pvp.addon"), CosmeticDecision.Include, digest));
        Pass(new ModWorkspaceStore(Path.Combine(data, "mods.json")).Load(fixtureGame, "xrmpe_pvp.addon").ArmbandsApprovedHash == digest, "approval persisted for exact digest");
        var bytes = File.ReadAllBytes(package);
        var marker = System.Text.Encoding.ASCII.GetBytes("creator = ZoneLink");
        var index = bytes.AsSpan().IndexOf(marker);
        if (index < 0) throw new Exception("Test DB has no header marker");
        bytes[index + marker.Length - 1] = (byte)'X';
        File.WriteAllBytes(package, bytes);
        var nextDigest = LocalArmbandsProfiles.ActualHash(package);
        File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new EmissiveBuildReceipt(1, EmissiveWorkshop.BuilderZipHash, nextDigest, 33, 34)));
        Pass(nextDigest != digest && EmissiveWorkshop.IsTrustedLocalBuild(package, nextDigest), "new cosmetic revision stays structurally bounded");
        Pass(includeChoice.ArmbandsApprovedHash != nextDigest, "prior decision does not cover new digest");
        try { LocalArmbandsProfiles.Apply(LauncherService.CreateMenuStartInfo(fixtureGame), package, "emissive", digest); throw new Exception("Old approval reused"); }
        catch (InvalidDataException) { Pass(true, "package update resets old approval"); }
        bytes[0] = 0;
        File.WriteAllBytes(package, bytes);
        var unsafeDigest = LocalArmbandsProfiles.ActualHash(package);
        File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new EmissiveBuildReceipt(1, EmissiveWorkshop.BuilderZipHash, unsafeDigest, 33, 34)));
        Pass(!EmissiveWorkshop.IsTrustedLocalBuild(package, unsafeDigest), "suspicious archive rejected despite matching receipt");
        try { LocalArmbandsProfiles.Apply(LauncherService.CreateMenuStartInfo(fixtureGame), package, "emissive", unsafeDigest); throw new Exception("Unsafe archive accepted"); }
        catch (InvalidDataException) { Pass(true, "critical content cannot use cosmetic override"); }
        Console.WriteLine($"TOTAL {count}; no fixtureGame process started");
    }
    finally
    {
        if (Path.GetFullPath(testRoot).StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(testRoot).StartsWith("case-", StringComparison.Ordinal)) Directory.Delete(testRoot, true);
    }
    return;
}
if (args.Length == 1 && args[0] == "--resources")
{
    Console.WriteLine(string.Join("\n", typeof(CosmeticPackageSafety).Assembly.GetManifestResourceNames()));
    return;
}
if (args.Length == 2 && args[0] == "--check-cosmetic-db")
{
    CosmeticPackageSafety.Verify(args[1]);
    Console.WriteLine("PASS cosmetic-only allowlist, bounds, CRC, OGF and DDS headers");
    return;
}
if (args.Length == 3 && args[0] == "--build-local-emissive")
{
    if (Directory.Exists(args[2])) throw new Exception("Clean profile must not exist before test");
    var first = await EmissiveWorkshop.EnsureBuiltAsync(args[1], args[2], null, CancellationToken.None);
    CosmeticPackageSafety.Verify(first.DbPath);
    if (first.ActualHash != LocalArmbandsProfiles.ActualHash(first.DbPath) ||
        (!first.ReferenceHashMatches && !EmissiveWorkshop.IsTrustedLocalBuild(first.DbPath, first.ActualHash)))
        throw new Exception("New build did not pass digest/provenance checks");
    var previews = Path.Combine(Path.GetDirectoryName(first.DbPath)!, "Previews");
    if (Directory.GetFiles(previews, "*.png").Length != 120) throw new Exception("Not all 120 previews were created");
    var second = await EmissiveWorkshop.EnsureBuiltAsync(args[1], args[2], null, CancellationToken.None);
    if (!second.FromCache || second.ActualHash != first.ActualHash) throw new Exception("Validated cache not reused");
    Console.WriteLine($"PASS clean local 33-model build, 120 PNG, verified cache; referenceHash={first.ReferenceHashMatches}; no game process started");
    return;
}
if (args.Length == 3 && args[0] == "--repair-local-emissive")
{
    var package = EmissiveWorkshop.InstalledPath(args[2]);
    var original = LocalArmbandsProfiles.ActualHash(package);
    var prior = await EmissiveWorkshop.EnsureBuiltAsync(args[1], args[2], null, CancellationToken.None);
    if (!prior.FromCache || prior.ActualHash != original) throw new Exception("Existing package is not a valid cache");
    using (var stream = new FileStream(package, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        stream.Position = 64;
        var value = stream.ReadByte();
        if (value < 0) throw new Exception("Package too small for corruption test");
        stream.Position = 64;
        stream.WriteByte((byte)(value ^ 1));
    }
    if (LocalArmbandsProfiles.ActualHash(package) == original) throw new Exception("Corruption fixture did not change the package");
    var repaired = await EmissiveWorkshop.EnsureBuiltAsync(args[1], args[2], null, CancellationToken.None);
    CosmeticPackageSafety.Verify(repaired.DbPath);
    if (repaired.FromCache || repaired.ActualHash != original ||
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(package)!, "Previews"), "*.png").Length != 120)
        throw new Exception("Corrupted local package was not rebuilt exactly");
    Console.WriteLine("PASS damaged local armbands.db rebuilt from verified installed resources; 120 previews retained; no game process started");
    return;
}
var root=Path.GetFullPath(args[0]);var game=Path.Combine(root,"game");
foreach(var d in new[]{"addons","bin/dedicated","mp"})Directory.CreateDirectory(Path.Combine(game,d));
foreach(var f in new[]{"addons/xrmpe_pvp.addon","bin/xrEngine.exe","bin/dedicated/xrEngine.exe","fsgame_sc.ltx"})File.WriteAllText(Path.Combine(game,f),"fixture never execute");
int n=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);n++;Console.WriteLine("PASS "+name);}
var starts=new[]{LauncherService.CreateClientStartInfo(game),LauncherService.CreateRemoteClientStartInfo(game,"127.0.0.1",55446),LauncherService.CreateSteamMenuStartInfo(game),LauncherService.EnableSteam(LauncherService.CreateClientStartInfo(game)),LauncherService.CreateServerStartInfo(game,"mp_agroprom","Friends",10,55446,60)};
foreach(var start in starts){Check(!start.ArgumentList.Any(x=>x.Contains("chevron",StringComparison.OrdinalIgnoreCase)),"no cosmetic args");Check(start.ArgumentList.Count(x=>x.StartsWith("-addon="))==1,"one addon");}
Check(!Directory.Exists(Path.Combine(game,".zonelink-cosmetics")),"no cosmetic files created");Check(typeof(LauncherService).GetProperty("ArmbandsEnabled")!=null,"optional workshop API present; default OFF");
var storePath=Path.Combine(root,"mods.json");var store=new ModWorkspaceStore(storePath);store.Save(game,"xrmpe_pvp.addon",new("kept notes",[]));var json=File.ReadAllText(storePath).Replace("\"Notes\":", "\"ChevronsEnabled\": true, \"Notes\":");File.WriteAllText(storePath,json);Check(store.Load(game,"xrmpe_pvp.addon").Notes=="kept notes","legacy cosmetic preference ignored without losing notes");store.Save(game,"xrmpe_pvp.addon",new("kept notes",[]));Check(!File.ReadAllText(storePath).Contains("Chevrons"),"new settings have no cosmetic property");
var addon=AddonCatalog.ReadLaunchCandidate(args[1]);foreach(var id in ContentPolicy.AcceptedPvpMaps)File.WriteAllText(Path.Combine(game,"mp",id+".db"),"fixture");File.WriteAllText(Path.Combine(game,"mp/mp_unverified.db"),"fixture");var maps=ContentPolicy.Maps(addon,"dm",game);Check(maps.Count(x=>x.LaunchVerified)==39,"39 accepted maps retained");Check(!maps.Single(x=>x.Id=="mp_unverified").LaunchVerified,"unknown map blocked");Check(!ContentPolicy.Maps(addon,"tdm",game).Any(x=>x.LaunchVerified),"TDM not fabricated");var ps=new ServerProfileStore(Path.Combine(root,"servers.json"));var expected=new ServerProfile(Map:"mp_arena",Name:"Friends");ps.Save(game,expected);Check(ps.Load(game)==expected,"server profile saved");Console.WriteLine($"TOTAL {n}; no game launches");

if(!args.Contains("--workshop-only")) {
var realGame=Path.GetDirectoryName(Path.GetDirectoryName(args[1]))!;
foreach(var id in new[]{"coop.addon","xrmpe_callofpripyat.addon","xrmpe_new_year_event.addon","xrmpe_dmr_pack.addon"}) {
 var a=AddonCatalog.ReadLaunchCandidate(Path.Combine(realGame,"addons",id));
 Check(a.Healthy && ContentPolicy.CanLaunch(a,false) && ContentPolicy.CanLaunch(a,true),id+" metadata and IP/Steam gate");
 var mode=a.Modes[0]; var map=ContentPolicy.Maps(a,mode,realGame).First(x=>x.Id==a.StartMap).Id;
 var p=new ServerProfile(Addon:id,Mode:mode,Map:map,Name:"Friends",Players:4,Port:55446,Rotation:mode=="defence"?[map]:null);
 foreach(var steam in new[]{false,true}) {
  var info=LauncherService.CreateAddonServerStartInfo(realGame,a,p,steam);var command=info.ArgumentList[info.ArgumentList.IndexOf("-start")+1];
  Check(command.StartsWith("server("+map+"/"+(mode=="defence"?"df":"coop")+"/") && !command.Contains("fraglimit"),id+" correct server scenario");
  Check(command.Contains("/public="+(steam?0:1)) && info.ArgumentList.Contains("-steam")==steam,id+" transport");
  foreach(var flag in a.StartupArgs??[])Check(info.ArgumentList.Contains(flag),id+" server flag "+flag);
 }
 var client=LauncherService.CreateRemoteClientStartInfo(realGame,"127.0.0.1",55446,id);
 Check(client.ArgumentList.Contains("client(127.0.0.1/port=55446)"),id+" remote address with flags");
 foreach(var info in new[]{client,LauncherService.CreateMenuStartInfo(realGame,id),LauncherService.CreateSteamMenuStartInfo(realGame,id)}) {
  Check(info.ArgumentList.Count(x=>x.StartsWith("-addon="))==1,id+" exactly one addon");
  foreach(var flag in a.StartupArgs??[])Check(info.ArgumentList.Contains(flag),id+" client flag "+flag);
 }
 ps.Save(game,p);Check(ps.LoadForAddon(game,id)?.Map==map,id+" independent profile saved");
 Check(AddonScenarios.ClientReason(a with {StartupArgs=["-unknown"]}) is not null,"unknown flags rejected");
 if(mode=="coop")Check(AddonScenarios.ServerReason(a,p with {CoopSave="missing"},realGame,false) is not null,"missing save rejected");
 else Check(AddonScenarios.ServerReason(a,p with {Rotation=["df_fake"]},realGame,false) is not null,"unknown defence map rejected");
}
Check(ps.LoadForAddon(game,"xrmpe_pvp.addon")?.Map=="mp_arena","PvP settings retained after switching campaigns");
// All file-writing tests use a tiny synthetic fixture; installed game is read-only.
File.WriteAllText(Path.Combine(game,"fsgame_s.ltx"),"$app_data_root$ = true| false| $fs_root$| _appdata_server\n$game_data$ = false| true| $fs_root$| gamedata\n");
var coop=AddonCatalog.ReadLaunchCandidate(Path.Combine(realGame,"addons","xrmpe_callofpripyat.addon"));
var cp=new ServerProfile(Addon:coop.FileName,Mode:"coop",Map:coop.StartMap!,Name:"Friends",Players:4);
var originalSaves=AddonScenarios.SaveFolder(game,coop.FileName,true);Directory.CreateDirectory(originalSaves);File.WriteAllText(Path.Combine(originalSaves,"manual.scop"),"preserve");
var originalProfile=File.ReadAllBytes(Path.Combine(game,"fsgame_s.ltx"));
AddonScenarios.PrepareServerData(game,coop,cp,false);
var ownSaves=AddonScenarios.SaveFolder(game,coop.FileName);File.WriteAllText(Path.Combine(ownSaves,coop.StartMap+".scop"),"old start");
var profile=AddonScenarios.PrepareServerData(game,coop,cp,false);
Check(!File.Exists(Path.Combine(ownSaves,coop.StartMap+".scop")) && Directory.EnumerateFiles(Path.Combine(AddonScenarios.DataRoot(game,coop.FileName),"backup")).Any(),"new campaign preserves old local start in backup");
AddonScenarios.PrepareServerData(game,coop,cp with {CoopSave="manual",OfficialSave=true},false);
Check(File.ReadAllText(Path.Combine(ownSaves,"manual.scop"))=="preserve" && File.ReadAllText(Path.Combine(originalSaves,"manual.scop"))=="preserve","official save copied read-only");
Check(originalProfile.SequenceEqual(File.ReadAllBytes(Path.Combine(game,"fsgame_s.ltx"))),"stock fsltx unchanged");
Check(File.ReadAllText(profile).Contains(Path.GetRelativePath(game,AddonScenarios.DataRoot(game,coop.FileName))),"appdata isolated per addon");
var df=AddonCatalog.ReadLaunchCandidate(Path.Combine(realGame,"addons","xrmpe_dmr_pack.addon"));var rotation=df.Maps.Take(2).Select(x=>x.Id).ToArray();var dp=new ServerProfile(Addon:df.FileName,Mode:"defence",Map:rotation[0],Rotation:rotation);
AddonScenarios.PrepareServerData(game,df,dp,false);var rot=Path.Combine(AddonScenarios.DataRoot(game,df.FileName),"maprot_list.ltx");
Check(File.ReadAllLines(rot).SequenceEqual(rotation.Select(x=>"sv_addmap "+x+"/ver=1.0")),"official defence maprot format");
AddonScenarios.PrepareServerData(game,df,dp with {Rotation=[rotation[0]]},false);Check(!File.Exists(rot),"single map does not inherit stale rotation");
Check(AddonScenarios.DataRoot(game,df.FileName)!=AddonScenarios.DataRoot(game,coop.FileName),"addon data cannot mix");
Console.WriteLine($"TOTAL {n}; no game launches");
}

if(args.Length > 2) {
 var package=Path.GetFullPath(args[2]); var data=Path.Combine(root,"workshop-data");
 LocalArmbandsProfiles.Verify(package); DetroitWorkshop.Install(package,data); DetroitWorkshop.Install(package,data);
 var installed=DetroitWorkshop.InstalledPath(data);Check(File.Exists(installed),"workshop install and repeat install");
 var stock=@"$app_data_root$ = true| false| $fs_root$| user\\
$game_data$ = true| true| $fs_root$| gamedata\\
";
 File.WriteAllText(Path.Combine(game,"fsgame.ltx"),stock);
 foreach(var steam in new[]{false,true}) {
  var c=steam?LauncherService.CreateSteamMenuStartInfo(game):LauncherService.CreateMenuStartInfo(game);
  LocalArmbandsProfiles.Apply(c,installed);Check(c.ArgumentList.Contains("-fsltx"),"ON VFS profile");
  Check(c.ArgumentList.Contains("-steam")==steam,"Steam flag retained with cosmetics");
  var cosmeticProfile=File.ReadAllText(Path.Combine(game,c.ArgumentList[c.ArgumentList.IndexOf("-fsltx")+1]));
  Check(cosmeticProfile.StartsWith(stock) && cosmeticProfile.Contains("$zonelink_armbands$"),"stock appdata and aliases preserved");
  Check(c.ArgumentList.Count(x=>x.StartsWith("-addon="))==1,"workshop mounts one original addon");
 }
 Check(File.ReadAllText(Path.Combine(game,"fsgame.ltx"))==stock,"original fsgame byte-identical");
 var modstore=new ModWorkspaceStore(Path.Combine(data,"mods.json"));modstore.Save(game,"xrmpe_pvp.addon",new("notes",[],true));
 Check(new ModWorkspaceStore(Path.Combine(data,"mods.json")).Load(game,"xrmpe_pvp.addon").ArmbandsEnabled,"ON survives restart");
 Check(!modstore.Load(game,"coop.addon").ArmbandsEnabled,"other addons unaffected");
 modstore.Save(game,"xrmpe_pvp.addon",modstore.Load(game,"xrmpe_pvp.addon") with {ArmbandsEnabled=false});
 Check(!modstore.Load(game,"xrmpe_pvp.addon").ArmbandsEnabled && !LauncherService.CreateSteamMenuStartInfo(game).ArgumentList.Contains("-fsltx"),"restore OFF uses original VFS with Steam");
 try { LocalArmbandsProfiles.Apply(LauncherService.CreateServerStartInfo(game,"mp_agroprom","Friends",8,55446,60),installed);throw new Exception("Dedicated cosmetics accepted"); }catch(InvalidOperationException){Check(true,"dedicated rejects cosmetics");}
 var corrupt=Path.Combine(root,"corrupt.db");File.WriteAllText(corrupt,"bad package");
 try {DetroitWorkshop.Install(corrupt,Path.Combine(root,"bad-data"));throw new Exception("Bad package accepted");}catch(InvalidDataException){Check(!Directory.Exists(Path.Combine(root,"bad-data")),"tampered package refused before install");}
 Console.WriteLine("TOTAL "+n+" with Workshop; no game launches");
}

if(args.Length > 3) {
 var source=Path.GetFullPath(args[3]); var data=Path.Combine(root,"emissive-data");
 LocalArmbandsProfiles.Verify(source,"emissive"); EmissiveWorkshop.Install(source,data); EmissiveWorkshop.Install(source,data);
 var installed=EmissiveWorkshop.InstalledPath(data);
 Check(File.Exists(installed) && !installed.Equals(DetroitWorkshop.InstalledPath(data),StringComparison.OrdinalIgnoreCase),"3D package separate from DDS");
 var stock=File.ReadAllBytes(Path.Combine(game,"fsgame.ltx"));
 foreach(var steam in new[]{false,true}) {
  var c=steam?LauncherService.CreateSteamMenuStartInfo(game):LauncherService.CreateMenuStartInfo(game);
  LocalArmbandsProfiles.Apply(c,installed,"emissive");
  Check(c.ArgumentList.Contains("-fsltx") && c.ArgumentList.Contains("-steam")==steam,"3D ON normal/Steam");
  Check(c.ArgumentList.Count(x=>x.StartsWith("-addon="))==1,"3D keeps one PvP addon");
  var emissiveProfile=File.ReadAllText(Path.Combine(game,c.ArgumentList[c.ArgumentList.IndexOf("-fsltx")+1]));
  Check(emissiveProfile.Contains(LocalArmbandsProfiles.EmissiveHash[..16]) && !emissiveProfile.Contains(LocalArmbandsProfiles.PackageHash[..16]),"3D and DDS never mixed");
 }
 Check(stock.SequenceEqual(File.ReadAllBytes(Path.Combine(game,"fsgame.ltx"))),"3D preserves original VFS profile");
 var mods=new ModWorkspaceStore(Path.Combine(data,"mods.json"));
 mods.Save(game,"xrmpe_pvp.addon",new("notes",[],true,"exo_outfit","emissive"));
 Check(new ModWorkspaceStore(Path.Combine(data,"mods.json")).Load(game,"xrmpe_pvp.addon").ArmbandsVariant=="emissive","3D choice survives restart");
 Check(mods.Load(game,"coop.addon").ArmbandsVariant=="dds","other addon preference independent");
 try { LocalArmbandsProfiles.Apply(LauncherService.CreateServerStartInfo(game,"mp_agroprom","Friends",8,55446,60),installed,"emissive"); throw new Exception("3D dedicated cosmetics accepted"); }
 catch(InvalidOperationException){Check(true,"3D dedicated rejects cosmetics");}
 Check(!LauncherService.CreateSteamMenuStartInfo(game).ArgumentList.Contains("-fsltx"),"3D OFF is stock Steam launch");
 Console.WriteLine("TOTAL "+n+" with isolated emissive package; no game launches");
}


