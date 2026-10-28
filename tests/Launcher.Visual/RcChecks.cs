using System.Reflection;
using Avalonia.Controls; using Avalonia.Headless; using Avalonia.Threading; using Xrmpe.Launcher; using Xrmpe.Launcher.Content;
internal static class RcChecks {
 public static void Run(string output,string package) {
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  var w=new MainWindow();
  void Set(string k,object v)=>typeof(MainWindow).GetField(k,flags)!.SetValue(w,v);
  object? Call(string k,params object[] args)=>typeof(MainWindow).GetMethod(k,flags)!.Invoke(w,args);
  var data=Environment.GetEnvironmentVariable("ZONELINK_DATA_HOME")!;
  var game=@"C:\Games\STALKER\X-Ray Multiplayer Extension\game";
  var addon=AddonCatalog.ReadLaunchCandidate(Path.Combine(game,"addons","xrmpe_pvp.addon"));
  Set("_contentClosed",true);Set("_contentRoot",game);Set("_addons",new[]{"xrmpe_pvp.addon","coop.addon","xrmpe_callofpripyat.addon","xrmpe_new_year_event.addon","xrmpe_dmr_pack.addon"}.Select(id=>AddonCatalog.ReadLaunchCandidate(Path.Combine(game,"addons",id))).ToArray());Set("_contentReady",true);
  Call("ApplyContentProfile",new ServerProfile());Call("UpdateContentAvailability");
  if(w.FindControl<CheckBox>("ChevronsToggle") is not null)throw new Exception("Removed control still present");
  Call("PickContentAddon",addon);
  w.Show();w.Classes.Add("motionOff");
  if(w.FindControl<ComboBox>("NetworkMode")!.SelectedIndex != 1 || w.FindControl<Control>("IpChoice")!.IsVisible || w.FindControl<Control>("ManualIpExpander")!.IsVisible) throw new Exception("IP enabled by default");
  w.FindControl<CheckBox>("AdvancedIpToggle")!.IsChecked=true;
  if(!w.FindControl<Control>("IpChoice")!.IsVisible)throw new Exception("IP opt-in missing");
  w.FindControl<ComboBox>("NetworkMode")!.SelectedIndex=0;
  if(!w.FindControl<Control>("IpConnectPanel")!.IsVisible)throw new Exception("IP panel missing");
  w.FindControl<CheckBox>("AdvancedIpToggle")!.IsChecked=false;
  if(w.FindControl<ComboBox>("NetworkMode")!.SelectedIndex!=1 || w.FindControl<Control>("IpConnectPanel")!.IsVisible)throw new Exception("IP opt-out failed");
  Console.WriteLine("PASS Steam default; IP opt-in/out persistence; no game launch");
  foreach(var size in new[]{(1320,900),(1000,740)})foreach(var page in new[]{"PlayPage","ServerPage","ConnectPage","SettingsPage","UpdatesPage","BrowserPage","AddonsPage","WorkshopPage"}) {
   w.Width=size.Item1;w.Height=size.Item2;
   foreach(var name in new[]{"PlayPage","ServerPage","ConnectPage","SettingsPage","UpdatesPage","BrowserPage","AddonsPage","WorkshopPage"})w.FindControl<Control>(name)!.IsVisible=name==page;
   w.FindControl<TabControl>("ContentTabs")!.SelectedIndex=1;
   Dispatcher.UIThread.RunJobs();w.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);Dispatcher.UIThread.RunJobs();
   using var frame=w.CaptureRenderedFrame()??throw new Exception("No UI frame");frame.Save(Path.Combine(output,$"RC-{page}-{size.Item1}.png"));
  }
  foreach(var id in new[]{"coop.addon","xrmpe_callofpripyat.addon","xrmpe_new_year_event.addon","xrmpe_dmr_pack.addon","xrmpe_pvp.addon"}) {
   var a=AddonCatalog.ReadLaunchCandidate(Path.Combine(game,"addons",id));Call("PickContentAddon",a);
   if(!(bool)typeof(MainWindow).GetProperty("ContentServerAllowed",flags)!.GetValue(w)!)throw new Exception("UI blocks "+id+": "+w.FindControl<TextBlock>("ContentAvailability")!.Text);
   if(w.FindControl<Control>("PvpFragFields")!.IsVisible!=(a.Modes[0]=="dm"))throw new Exception("Fraglimit shown for non-DM");
   foreach(var page in new[]{"PlayPage","ServerPage","ConnectPage","SettingsPage","UpdatesPage","BrowserPage","AddonsPage","WorkshopPage"})w.FindControl<Control>(page)!.IsVisible=page=="ServerPage";
   w.Width=1000;w.Height=740;Dispatcher.UIThread.RunJobs();w.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);Dispatcher.UIThread.RunJobs();
   using var frame=w.CaptureRenderedFrame()??throw new Exception("No addon UI frame");frame.Save(Path.Combine(output,"addon-"+id+".png"));
   Console.WriteLine("PASS UI scenario "+id);
  }
  if(File.Exists(package)) {
   var installed=EmissiveWorkshop.InstalledPath(data);Directory.CreateDirectory(Path.GetDirectoryName(installed)!);File.Copy(package,installed,true);
   var store=new ModWorkspaceStore(Path.Combine(data,"mod-workspaces.json"));
   store.Save(game,"xrmpe_pvp.addon",new ModWorkspace("",[],true,"","dds"));
   Call("RefreshArmbands");
   var toggle=w.FindControl<ToggleSwitch>("ArmbandsToggle")!;
   if(!toggle.IsEnabled || toggle.IsChecked==true)throw new Exception("Old DDS preference must migrate to OFF");
   if(w.FindControl<Button>("ArmbandsInstall") is not null || w.FindControl<ComboBox>("ArmbandsStyle") is not null)throw new Exception("Legacy workshop UI remains");
   toggle.IsChecked=true;Dispatcher.UIThread.RunJobs();
   if(!store.Load(game,"xrmpe_pvp.addon").ArmbandsEnabled || store.Load(game,"xrmpe_pvp.addon").ArmbandsVariant!="emissive")throw new Exception("3D ON not saved");
   Call("PickContentAddon",AddonCatalog.ReadLaunchCandidate(Path.Combine(game,"addons","coop.addon")));
   if(toggle.IsEnabled)throw new Exception("Cosmetics allowed in coop");
   Call("PickContentAddon",addon);if(toggle.IsChecked!=true)throw new Exception("ON not restored after addon switch");
   foreach(var name in new[]{"PlayPage","ServerPage","ConnectPage","SettingsPage","UpdatesPage","BrowserPage","AddonsPage","WorkshopPage"})w.FindControl<Control>(name)!.IsVisible=name=="WorkshopPage";
   Call("Navigate",w.FindControl<Button>("WorkshopNav")!,new Avalonia.Interactivity.RoutedEventArgs());
   Dispatcher.UIThread.RunJobs();w.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);Dispatcher.UIThread.RunJobs();
   using(var frame=w.CaptureRenderedFrame()!)frame.Save(Path.Combine(output,"Workshop-3D-on.png"));
   w.Width=1320;w.Height=900;w.FindControl<ScrollViewer>("PageScroll")!.Offset=new Avalonia.Vector(0,430);
   Dispatcher.UIThread.RunJobs();w.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);Dispatcher.UIThread.RunJobs();
   using(var frame=w.CaptureRenderedFrame()!)frame.Save(Path.Combine(output,"Workshop-gallery.png"));
   toggle.IsChecked=false;Dispatcher.UIThread.RunJobs();
   if(toggle.IsChecked==true || store.Load(game,"xrmpe_pvp.addon").ArmbandsEnabled)throw new Exception("OFF not saved");
   Call("SelectWorkshopCostume","exo_outfit");
   w.FindControl<ScrollViewer>("PageScroll")!.Offset=new Avalonia.Vector(0,420);
   Dispatcher.UIThread.RunJobs();w.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);Dispatcher.UIThread.RunJobs();
   if(w.FindControl<Image>("WorkshopPreview")!.Source is null)throw new Exception("3D preview missing");
   using(var frame=w.CaptureRenderedFrame()!)frame.Save(Path.Combine(output,"Workshop-3D-exo.png"));
   Console.WriteLine("PASS Workshop 3D-only ON/OFF, migration, persistence and preview; game never started");
  }
  w.Close();Console.WriteLine("PASS RC UI: legacy chevron control absent; addon selection and return to PvP; server/content pages at two resolutions. No game started.");
 }
}


