namespace Xrmpe.Launcher.Presentation;

public static class ModernDefaults
{
    public static int Transport(int? saved, bool freshProfile, bool steamApiPresent) => saved ?? (freshProfile && steamApiPresent ? 1 : 0);
}
