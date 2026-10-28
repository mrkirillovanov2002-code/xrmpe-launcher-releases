using Xrmpe.Launcher;

internal static class PortabilityChecks
{
    public static void Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "xrMPE перенос " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            string Fixture(string relative)
            {
                var game = Path.Combine(sandbox, relative);
                foreach (var file in GameInstallation.RequiredFiles)
                {
                    var path = Path.Combine(game, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "fixture only; not a game");
                }
                Directory.CreateDirectory(Path.Combine(game, "addons"));
                File.WriteAllText(Path.Combine(game, "addons", "xrmpe_pvp.addon"), "fixture only");
                return game;
            }
            var gameA = Fixture(Path.Combine("Другая установка", "game"));
            var gameB = Fixture(Path.Combine("Произвольная папка", "xrMPE"));
            var rootA = Path.GetDirectoryName(gameA)!;
            var nestedLauncher = Path.Combine(rootA, "tools", "launcher", "publish");
            Directory.CreateDirectory(nestedLauncher);
            if (GameInstallation.FindNearby(nestedLauncher) != gameA || GameInstallation.FindNearby(rootA) != gameA ||
                GameInstallation.FindNearby(gameA) != gameA || GameInstallation.Resolve(rootA) != gameA ||
                GameInstallation.Resolve(gameB) != gameB || GameInstallation.Resolve(Path.Combine(gameA, "bin")) != gameA)
                throw new Exception("Portable discovery/manual folder selection failed");
            var unrelated = Path.Combine(sandbox, "отдельный лаунчер");
            Directory.CreateDirectory(unrelated);
            // The launcher does not recursively inspect unrelated sibling installations.
            if (GameInstallation.FindNearby(unrelated) is not null) throw new Exception("Unexpected unrelated installation discovered");
            var settings = Path.Combine(sandbox, "profile");
            var pcA = new GamePathSettings(settings, "PC-A");
            var pcB = new GamePathSettings(settings, "PC-B");
            pcA.Save(gameA);
            if (pcB.Load() is not null) throw new Exception("Second computer inherited first computer's setting");
            pcB.Save(gameB);
            if (pcA.Load() != gameA || pcB.Load() != gameB) throw new Exception("Per-computer path persistence failed");
            var missingOldPath = Path.Combine(sandbox, "несуществующий старый ПК", "game");
            try { GameInstallation.Resolve(missingOldPath); throw new Exception("Missing saved path accepted"); }
            catch (FileNotFoundException) { }
            if (GameInstallation.FindNearby(nestedLauncher) != gameA) throw new Exception("Discovery depends on old path");
            foreach (var required in GameInstallation.RequiredFiles)
            {
                var path = Path.Combine(gameB, required);
                File.Delete(path);
                try { GameInstallation.Resolve(gameB); throw new Exception("Incomplete game accepted: " + required); }
                catch (FileNotFoundException ex)
                {
                    if (!ex.Message.Contains(required) || !ex.Message.Contains("Выбрать папку игры")) throw new Exception("Missing-file message is unclear");
                }
                File.WriteAllText(path, "fixture only");
            }
            var client = LauncherService.CreateClientStartInfo(gameB);
            var server = LauncherService.CreateServerStartInfo(gameB, "mp_kpp_2", "name", 32, 5446, 60);
            if (client.WorkingDirectory != gameB || server.WorkingDirectory != Path.Combine(gameB, "bin"))
                throw new Exception("Launch working directories did not relocate");
            if (!LauncherService.EnableSteam(client).ArgumentList.Contains("-steam")) throw new Exception("Steam flag lost after relocation");
            Console.WriteLine("PASS: arbitrary temp installations, EXE/ancestor discovery, unrelated manual location, absent old path, per-PC settings, each missing file, relocated client/server/Steam commands.");
        }
        finally
        {
            // Explicitly verify the target before removing only this generated fixture tree.
            var absolute = Path.GetFullPath(sandbox);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("xrMPE перенос "))
                throw new Exception("Unsafe fixture cleanup target");
            Directory.Delete(absolute, recursive: true);
        }
    }
}

