using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// Extra conditions on the site of a capital, on top of the game's own (CheckCanCreateColonyAtOrb: inside the search radius, far
// enough from other empires):
//   - Home Neighbours / Neighbour Range: at least N other stars within a chosen distance, so nobody starts on a lonely straggler;
//   - the site must lie inside the empire's region, when RegionPlacement has assigned one.
// A rejected site is simply a failed attempt, so the game's own retry loop (new random point, growing radius) finds another. The
// conditions are only enforced for the first attempts of each placement, so a sparse galaxy can never make it impossible to place
// an empire.
// Capital placement is Galaxy.GenerateColonyForEmpire (the overload taking GameStartSettings, which only GenerateEmpire calls).
internal static class HomeSites
{
    public static readonly Option[] Counts = { new("Off", 0), new("2", 2), new("3", 3), new("4", 4), new("5", 5), new("7", 7), new("10", 10) };
    public static int CountIndex;
    // Galaxy coordinates are raw units; the game shows them in millions.
    public static readonly Option[] Ranges = { new("75M", 75e6f), new("100M", 100e6f), new("150M", 150e6f), new("200M", 200e6f), new("300M", 300e6f) };
    public static int RangeIndex = 2;

    private const int EnforcedAttempts = 60;

    private static bool _inCapital;
    private static int _rejections;
    private static int _logged;

    public static void Install(Harmony harmony)
    {
        MethodInfo? check = AccessTools.Method(typeof(Galaxy), "CheckCanCreateColonyAtOrb");
        MethodInfo? generate = AccessTools.Method(typeof(Galaxy), "Generate");
        MethodInfo? colony = null;
        foreach (MethodInfo m in typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == "GenerateColonyForEmpire" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(GameStartSettings))
            {
                colony = m;
            }
        }
        if (check == null || generate == null || colony == null)
        {
            Bootstrap.Log("HomeSites: CheckCanCreateColonyAtOrb / GenerateColonyForEmpire / Generate not found; the home-site conditions are disabled.");
            return;
        }
        harmony.Patch(check, postfix: Own(nameof(CheckPostfix)));
        harmony.Patch(colony, prefix: Own(nameof(ColonyPrefix)), finalizer: Own(nameof(ColonyFinalizer)));
        harmony.Patch(generate, prefix: Own(nameof(GeneratePrefix)));
    }

    private static HarmonyMethod Own(string name) => new(typeof(HomeSites).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    private static void GeneratePrefix()
    {
        _logged = 0;
    }

    private static void ColonyPrefix()
    {
        _inCapital = true;
        _rejections = 0;
    }

    private static Exception? ColonyFinalizer(Exception? __exception)
    {
        _inCapital = false;
        return __exception;
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void CheckPostfix(Galaxy __instance, Orb orb, ref bool __result, ref bool tooCloseToOtherColonies)
    {
        if (!_inCapital || !__result || _rejections >= EnforcedAttempts)
        {
            return;
        }
        try
        {
            string? reason = RejectionReason(__instance, orb);
            if (reason == null)
            {
                return;
            }
            _rejections++;
            __result = false;
            tooCloseToOtherColonies = false;
            if (++_logged <= 40)
            {
                Bootstrap.Log($"HomeSites: rejected a capital site: {reason}; rejection {_rejections}.");
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("HomeSites.CheckPostfix: " + ex.Message);
        }
    }

    private static string? RejectionReason(Galaxy galaxy, Orb orb)
    {
        if (RegionPlacement.RejectsOrb(galaxy, orb))
        {
            return "outside the empire's region";
        }
        int required = (int)Counts[CountIndex].Value;
        if (required <= 0)
        {
            return null;
        }
        StarSystem? home = RegionPlacement.SystemOf(galaxy, orb);
        if (home == null)
        {
            return null;
        }
        float radius = Ranges[RangeIndex].Value;
        float radius2 = radius * radius;
        int neighbours = 0;
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            StarSystem? s = entry.Value;
            if (s == null || s.SystemId == home.SystemId)
            {
                continue;
            }
            float dx = s.GalaxyX - home.GalaxyX, dy = s.GalaxyY - home.GalaxyY;
            if (dx * dx + dy * dy <= radius2 && ++neighbours >= required)
            {
                return null;
            }
        }
        return $"only {neighbours} stars within {radius / 1e6f:0}M (need {required})";
    }
}
