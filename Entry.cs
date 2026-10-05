namespace SpiralArms;

// Found by the mod launcher's injection scan (a public static class named Entry with Init()); see DW2-Mod-Launcher/docs/DLL Injection.md.
public static class Entry
{
    public static void Init() => Bootstrap.Init();
}
