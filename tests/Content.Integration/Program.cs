using System.Text;
using Xrmpe.Launcher.Content;

var temp = Path.Combine(Path.GetTempPath(), "xrMPE контент " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int checks = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Console.WriteLine("PASS: " + text); }
byte[] Tar(params (string Name, byte[] Data, byte Type)[] entries)
{
    using var output = new MemoryStream();
    foreach (var entry in entries)
    {
        var h = new byte[512]; Encoding.UTF8.GetBytes(entry.Name).CopyTo(h, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(entry.Data.Length, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
        Array.Fill(h, (byte)' ', 148, 8); h[156] = entry.Type;
        Encoding.ASCII.GetBytes(Convert.ToString(h.Sum(x => (int)x), 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
        output.Write(h); output.Write(entry.Data); output.Write(new byte[(512 - entry.Data.Length % 512) % 512]);
    }
    output.Write(new byte[1024]); return output.ToArray();
}
var json = Encoding.UTF8.GetBytes("""{"name":{"ru":"Тест"},"description":{"ru":"Описание"},"author":"Автор","addonVersion":"1.4.7","modes":["dm"]}""");
byte[] Package(byte[] metadata) => Tar(("metadata.json", metadata, 0), ("resources.db", [1], 0));
AddonDescriptor Read(byte[] bytes) { var f = Path.Combine(temp, "xrmpe_pvp.addon"); File.WriteAllBytes(f, bytes); return AddonCatalog.Read(f); }
try
{
    var good = Read(Package(json));
    Check(good.Healthy && good.Name == "Тест" && good.Author == "Автор" && good.Icon is null, "V7 TAR, реальные поля и отсутствие иконки");
    Check(!Read(Tar(("resources.db", [1], 0))).Healthy, "Нет metadata.json");
    Check(!Read(Package(Encoding.UTF8.GetBytes("{bad"))).Healthy, "Повреждённый JSON");
    Check(!Read(Package(Encoding.UTF8.GetBytes("null"))).Healthy, "Неверный тип JSON");
    Check(!Read(Package(new byte[65537])).Healthy, "Ограничение размера метаданных");
    Check(!Read(Package(json)[..600]).Healthy, "Обрезанный контейнер");
    var corrupt = Package(json); corrupt[1] ^= 1;
    Check(!Read(corrupt).Healthy, "Контрольная сумма заголовка");
    Check(!Read(Tar(("../outside", [1], 0), ("metadata.json", json, 0), ("x.db", [1], 0))).Healthy, "Выход за каталог запрещён");
    Check(!Read(Tar(("metadata.json", json, 0), ("metadata.json", json, 0), ("x.db", [1], 0))).Healthy, "Повторяющиеся записи запрещены");
    Check(!Read(Tar(("metadata.json", json, 0), ("x.db", [1], (byte)'2'))).Healthy, "Ссылки TAR запрещены");
    Check(!Read(Tar(("metadata.json", json, 0))).Healthy, "Пакет без ресурсных DB не считается здоровым");
    Check(Read(Tar(("metadata.json", json, 0), ("x.db", [1], 0), ("icon.webp", [1,2,3], 0))).Healthy, "Плохая иконка не ломает метаданные");
    Directory.CreateDirectory(Path.Combine(temp, "mp")); File.WriteAllText(Path.Combine(temp, "mp", "mp_kpp_2.db"), "fixture");
    File.WriteAllText(Path.Combine(temp, "mp", "unknown.db"), "fixture");
    Check(ContentPolicy.Maps(good, "dm", temp).Single().LaunchVerified, "Разрешён только прежний сценарий PvP 1.4.7 / dm / mp_kpp_2");
    Check(ContentPolicy.Maps(good, "coop", temp).Count == 0, "Фильтр режима не добавляет несуществующие карты");
    var control = good with { FileName = "zonestrike.addon", Version = "local-control-1", PackageSha256 = ContentPolicy.LocalControlSha256 };
    File.WriteAllText(Path.Combine(temp, "mp", "mp_agroprom.db"), "fixture");
    Check(ContentPolicy.CanLaunch(good, true), "Обычный PvP остаётся Steam-вариантом");
    Check(ContentPolicy.CanLaunch(control, false) && !ContentPolicy.CanLaunch(control, true), "Контрольный пакет разрешён по IP, не выдан за Steam-совместимый");
    Check(!ContentPolicy.CanLaunch(control with { PackageSha256 = "changed" }, false), "Изменённый контрольный пакет не наследует разрешение");
    Check(!ContentPolicy.CanLaunch(control with { FileName = "ZoneStrike.addon" }, false), "Верхний регистр не объявляется совместимым");
    Check(!ContentPolicy.CanLaunch(control with { Modes = ["tdm"] }, false), "Метаданные TDM не включают поддержку режима");
    Check(ContentPolicy.Maps(control, "dm", temp).Single().LaunchVerified && ContentPolicy.Maps(control, "tdm", temp).Count == 0, "Только подтверждённая карта контрольного пакета");
    File.Delete(Path.Combine(temp, "mp", "mp_agroprom.db"));
    Check(!ContentPolicy.Maps(good with { Version = "999" }, "dm", temp).Single().LaunchVerified, "Новая версия аддона не считается проверенной");
    File.Delete(Path.Combine(temp, "mp", "mp_kpp_2.db"));
    Check(!ContentPolicy.Maps(good, "dm", temp).Single().LaunchVerified, "Отсутствие ресурса блокирует запуск");
    var profileFile = Path.Combine(temp, "profiles.json"); var store = new ServerProfileStore(profileFile);
    var a = new ServerProfile(Name: "Мой сервер", Port: 5448, Kills: 25);
    store.Save(Path.Combine(temp, "game one"), a);
    Check(new ServerProfileStore(profileFile).Load(Path.Combine(temp, "game one")) == a, "Профиль переживает перезапуск");
    var b = a with { Addon = "coop.addon", Mode = "coop", Map = "coop_l01_escape" };
    store.Save(Path.Combine(temp, "game two"), b);
    Check(store.Load(Path.Combine(temp, "game one")) == a && store.Load(Path.Combine(temp, "game two")) == b, "Профили разных папок независимы");
    File.WriteAllText(profileFile, "corrupt");
    Check(store.Load(Path.Combine(temp, "game one")) == a && store.RecoveryMessage is not null, "Восстановление из .bak");
    store.Save(Path.Combine(temp, "game one"), a with { Players = 16 });
    Check(Directory.GetFiles(temp, "profiles.json.corrupt-*").Length == 1 && File.Exists(profileFile + ".bak"), "Повреждённый файл и резервная копия сохранены");
    foreach (var bad in new[] { a with { Addon = "../x.addon" }, a with { Addon = null! }, a with { Mode = null! }, a with { Port = 5450 }, a with { Players = 100 }, a with { Map = "x)/evil" } })
    {
        bool rejected = false; try { store.Save(temp, bad); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Неверный профиль отклонён");
    }
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    bool cancelled = false; try { await AddonCatalog.ScanAsync(temp, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Сканирование отменяется");
    var modFile = Path.Combine(temp, "mod-workspaces.json"); var mods = new ModWorkspaceStore(modFile);
    var gameA = Path.Combine(temp, "game A"); var gameB = Path.Combine(temp, "game B");
    mods.Save(gameA, "xrmpe_pvp.addon", new("PvP notes", ["planned_texture"]));
    mods.Save(gameA, "coop.addon", new("Coop notes", ["planned_sound"]));
    Check(new ModWorkspaceStore(modFile).Load(gameA, "xrmpe_pvp.addon").Notes == "PvP notes" && mods.Load(gameA, "coop.addon").Notes == "Coop notes", "Планы модов независимы по аддонам и переживают перезапуск");
    Check(mods.Load(gameB, "xrmpe_pvp.addon").PlannedModIds.Length == 0, "Планы модов не переносятся на другую папку игры");
    File.WriteAllText(modFile, "broken");
    Check(mods.Load(gameA, "xrmpe_pvp.addon").Notes == "PvP notes" && mods.Warning is not null, "Восстановление планов модов из .bak");
    mods.Save(gameA, "coop.addon", new("restored", []));
    Check(Directory.GetFiles(temp, "mod-workspaces.json.corrupt-*").Length == 1, "Повреждённый план сохранён отдельно");
    bool unsafeMod = false; try { mods.Save(gameA, "../bad.addon", ModWorkspace.Empty); } catch (InvalidDataException) { unsafeMod = true; }
    Check(unsafeMod, "Небезопасный ключ аддона отклонён");
    if (args.FirstOrDefault() is { } game)
    {
        // Only read headers/metadata; no engine execution or archive extraction.
        var before = Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories).ToDictionary(x => x, x => (new FileInfo(x).Length, File.GetLastWriteTimeUtc(x)));
        var real = await AddonCatalog.ScanAsync(game, CancellationToken.None);
        Check(real.Count == 5 && real.All(x => x.Healthy), "Все пять установленных .addon прочитаны (включая >4 ГБ)");
        foreach (var addon in real) Console.WriteLine($"REAL: {addon.FileName}; {addon.Version}; {string.Join(',', addon.Modes)}; maps={addon.Maps.Length}; icon={addon.Icon?.Length ?? 0}");
        var pvp = real.Single(x => x.FileName == "xrmpe_pvp.addon");
        Check(pvp.Icon is not null && pvp.IconPixels > 0 && ContentPolicy.VerifiedAddon(pvp), "Иконка и метаданные установленного PvP");
        Check(ContentPolicy.Maps(pvp, "dm", game).Count(x => x.LaunchVerified) == 1, "Из всех игровых DB разрешена ровно одна проверенная карта");
        var dmr = real.Single(x => x.FileName == "xrmpe_dmr_pack.addon");
        Check(ContentPolicy.Maps(dmr, "defence", game).Count(x => x.Source.StartsWith("metadata.json")) == 10 && ContentPolicy.Maps(dmr, "defence", game).All(x => !x.LaunchVerified), "10 деклараций DMR и отдельные ресурсы — без разрешения запуска");
        Check(ContentPolicy.Maps(pvp, "dm", game).Count > 1 && ContentPolicy.Maps(pvp, "dm", game).Where(x => x.Id != "mp_kpp_2").All(x => !x.LaunchVerified), "Обнаруженные PvP-ресурсы не становятся проверенными картами");
        Check(new ModeChoice("coop").ToString().Contains("Кооператив") && new ModeChoice("defence").ToString().Contains("Оборона"), "Понятные названия режимов без изменения кодов движка");
        Check(ContentPolicy.Maps(dmr, "dm", game).Count == 0, "Карты DMR не попадают в DM");
        Check(real.Where(x => x.Modes.Contains("coop")).All(x => ContentPolicy.Maps(x, "coop", game).Count == 1 && !ContentPolicy.VerifiedAddon(x)), "Стартовые карты coop отделены от рабочего PvP");
        Check(before.All(x => File.Exists(x.Key) && (new FileInfo(x.Key).Length, File.GetLastWriteTimeUtc(x.Key)) == x.Value), "Размеры и время изменения всех игровых файлов сохранены");
    }
    Console.WriteLine($"PASS: {checks} content checks");
}
finally { Directory.Delete(temp, true); }
