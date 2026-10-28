namespace Xrmpe.Launcher.Content;

public static class DetroitWorkshop
{
    public static string InstalledPath(string data) =>
        Path.Combine(data, "LocalMods", "Detroit", LocalArmbandsProfiles.PackageHash[..16], "armbands.db");

    public static void Install(string source, string data)
    {
        LocalArmbandsProfiles.Verify(source);
        var target = InstalledPath(data);
        ContentPaths.NoLinks(target);
        if (File.Exists(target)) { LocalArmbandsProfiles.Verify(target); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            LocalArmbandsProfiles.Verify(temporary);
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
