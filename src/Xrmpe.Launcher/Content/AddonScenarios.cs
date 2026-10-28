using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace Xrmpe.Launcher.Content;
public sealed record CampaignSave(string Name, bool Official) { public override string ToString() => Name.Length == 0 ? "Новая кампания" : Name + (Official ? " · оригинальный лаунчер (будет скопировано)" : " · ZoneLink"); }
public static class AddonScenarios
{
    public static string? ClientReason(AddonDescriptor? a)
    {
        if(a is null)return "Аддон отсутствует. Обновите каталог или установите его официальным лаунчером.";
        if(!a.Healthy)return a.Error;
        if(!Regex.IsMatch(a.FileName,@"\A[a-z0-9][a-z0-9_-]{0,79}\.addon\z"))return "Движку требуется имя .addon в нижнем регистре. Файл автоматически не переименовывается.";
        if(!a.Modes.Any(m=>m is "dm" or "coop" or "defence"))return "Нет поддерживаемого оригинальным сценарием режима в метаданных.";
        if((a.StartupArgs??[]).Any(x=>x is not ("-use_all_spawn" or "-winter" or "-noprefetch")))return "Метаданные содержат неизвестные флаги запуска. Требуется уточнение у автора аддона; флаги не исполняются вслепую.";
        return null;
    }
    public static bool ValidSaveName(string? name,bool empty=false)=>name is not null && (empty && name.Length==0 || Regex.IsMatch(name,@"\A[\p{L}\p{N}_-]{1,80}\z"));
    public static string DataRoot(string game,string addon) {
        if(!Regex.IsMatch(addon,@"\A[a-z0-9][a-z0-9_-]{0,79}\.addon\z"))throw new InvalidDataException("Небезопасный аддон");
        return Path.Combine(game,"_zl_s",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addon)))[..10]);
    }
    public static string SaveFolder(string game,string addon,bool official=false)=>Path.Combine(official?Path.Combine(game,"_appdata_server"):DataRoot(game,addon),"savedgames",Path.GetFileNameWithoutExtension(addon));
    public static CampaignSave[] Saves(string game,string addon) {
        var rows=new List<CampaignSave>{new("",false)};
        foreach(bool original in new[]{false,true})try {
            var folder=SaveFolder(game,addon,original);ContentPaths.NoLinks(folder);
            if(Directory.Exists(folder))rows.AddRange(Directory.EnumerateFiles(folder,"*.scop").Take(128).Where(f=>ValidSaveName(Path.GetFileNameWithoutExtension(f))).Select(f=>new CampaignSave(Path.GetFileNameWithoutExtension(f),original)));
        }catch(IOException){}
        return rows.ToArray();
    }
    public static string? ServerReason(AddonDescriptor? a,ServerProfile p,string game,bool steam) {
        if(!ContentPolicy.CanLaunch(a,steam))return ClientReason(a)??"Контрольный аддон не разрешён для этого транспорта.";
        if(a is null || !a.Modes.Contains(p.Mode))return "Режим не объявлен выбранным аддоном.";
        if(p.Mode=="dm")return ContentPolicy.Maps(a,p.Mode,game).Any(m=>m.Id==p.Map && m.LaunchVerified)?null:"Нет доступной карты из разрешённого списка.";
        if(!File.Exists(Path.Combine(game,"fsgame_s.ltx")))return "Нет штатного fsgame_s.ltx. Восстановите установку официальным лаунчером.";
        if(p.Mode=="coop") {
            if(a.StartMap is null)return "Автор не указал coopStartMap; создание новой кампании недоступно.";
            if(p.CoopSave.Length==0)return p.Map==a.StartMap?null:"Стартовая локация не соответствует метаданным кампании.";
            var save=Path.Combine(SaveFolder(game,a.FileName,p.OfficialSave),p.CoopSave+".scop");ContentPaths.NoLinks(save);
            return File.Exists(save)?null:"Выбранное сохранение отсутствует. Обновите список сохранений.";
        }
        if(p.Mode=="defence") {
            var rotation=p.Rotation??[];
            if(rotation.Length==0)return "Добавьте карту обороны в ротацию.";
            if(rotation[0]!=p.Map)return "Первая карта ротации должна совпадать с выбранной картой.";
            return rotation.All(id=>a.Maps.Any(m=>m.Id==id && m.Modes.Contains("df")))?null:"Ротация содержит карту, не объявленную выбранным аддоном.";
        }
        return "Нет штатного сценария для выбранного режима.";
    }
    // Called only immediately before an approved game launch, never during discovery or tests of command construction.
    public static string PrepareServerData(string game,AddonDescriptor a,ServerProfile p,bool steam) {
        ServerProfileStore.Validate(p);var reason=ServerReason(a,p,game,steam);if(reason is not null)throw new InvalidOperationException(reason);
        var source=Path.Combine(game,"fsgame_s.ltx");ContentPaths.NoLinks(source);
        if(new FileInfo(source).Length>65536)throw new InvalidDataException("Слишком большой fsgame_s.ltx");
        var text=File.ReadAllText(source);
        var dir=DataRoot(game,a.FileName);ContentPaths.NoLinks(dir);Directory.CreateDirectory(dir);
        var relative=Path.GetRelativePath(game,dir)+"\\";var pattern=@"(?m)^\$app_data_root\$[^\r\n]*";
        if(Regex.Matches(text,pattern).Count!=1)throw new InvalidDataException("Неоднозначный appdata в fsgame_s.ltx.");
        text=Regex.Replace(text,pattern,_=>"$app_data_root$ = true| false| $fs_root$| "+relative);
        var profile=Path.Combine(game,"_zl_server_"+Path.GetFileName(dir)+".ltx");ContentPaths.NoLinks(profile);ContentPaths.NoLinks(profile+".tmp");ContentPaths.NoLinks(profile+".bak");
        if(File.Exists(profile) && File.ReadAllText(profile)!=text)File.Copy(profile,profile+".bak",true);
        File.WriteAllText(profile+".tmp",text,new UTF8Encoding(false));File.Move(profile+".tmp",profile,true);
        void Backup(string file) {ContentPaths.NoLinks(file);if(File.Exists(file)){var b=Path.Combine(dir,"backup");ContentPaths.NoLinks(b);Directory.CreateDirectory(b);File.Move(file,Path.Combine(b,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+"-"+Path.GetFileName(file)));}}
        if(p.Mode=="defence") {
            var maprot=Path.Combine(dir,"maprot_list.ltx");Backup(maprot);
            // Original launcher writes rotation only for >1 maps; isolated one-map profile must not retain an old rotation.
            if(p.Rotation is {Length:>1})File.WriteAllLines(maprot,p.Rotation.Select(id=>"sv_addmap "+id+"/ver=1.0"),new UTF8Encoding(false));
        }
        if(p.Mode=="coop") {
            var saves=SaveFolder(game,a.FileName);ContentPaths.NoLinks(saves);Directory.CreateDirectory(saves);
            if(p.CoopSave.Length==0)Backup(Path.Combine(saves,a.StartMap+".scop"));
            else if(p.OfficialSave) {
                var from=Path.Combine(SaveFolder(game,a.FileName,true),p.CoopSave+".scop");ContentPaths.NoLinks(from);
                if(new FileInfo(from).Length>256L*1024*1024)throw new IOException("Сохранение слишком большое");
                var target=Path.Combine(saves,p.CoopSave+".scop");Backup(target);File.Copy(from,target,false);
            }
        }
        return profile;
    }
}
