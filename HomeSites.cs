using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// "Home Surroundings" row of the other-empires step: capitals must have some stars around them.
//
// Vanilla picks an empire's capital with Galaxy.GenerateColonyForEmpire: it looks for a native-type planet near a random point and
// accepts it if CheckCanCreateColonyAtOrb says so (inside the search radius, far enough from other empires). Nothing looks at what
// surrounds the planet, so capitals sometimes land on stragglers with no neighbours. This adds one more condition to that check:
// at least N other stars within a chosen distance. A rejected site is simply a failed attempt, so the game's own retry loop
// (new random point, growing radius) finds another. The requirement is only enforced for the first attempts of each placement, so
// a sparse galaxy can never make it impossible to place an empire.
internal static class HomeSites
{
    public static readonly Option[] Counts = { new("Off", 0), new("2", 2), new("3", 3), new("4", 4), new("5", 5), new("7", 7), new("10", 10) };
    public static int CountIndex;
    // Galaxy coordinates are raw units; the game shows them in millions.
    public static readonly Option[] Ranges = { new("75M", 75e6f), new("100M", 100e6f), new("150M", 150e6f), new("200M", 200e6f), new("300M", 300e6f) };
    public static int RangeIndex = 2;

    private const int EnforcedAttempts = 60;

    private static int _rejections;
    private static int _calls;

    public static void Install(Harmony harmony)
    {
        MethodInfo? check = AccessTools.Method(typeof(Galaxy), "CheckCanCreateColonyAtOrb");
        MethodInfo? generate = AccessTools.Method(typeof(Galaxy), "Generate");
        MethodInfo? colony = null;
        foreach (MethodInfo m in typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == "GenerateColonyForEmpire" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Empire))
            {
                colony = m;
            }
        }
        if (check == null || generate == null || colony == null)
        {
            Bootstrap.Log("HomeSites: CheckCanCreateColonyAtOrb / GenerateColonyForEmpire / Generate not found; the home-surroundings option is disabled.");
            return;
        }
        harmony.Patch(check, postfix: Own(nameof(CheckPostfix)));
        harmony.Patch(colony, prefix: Own(nameof(ColonyPrefix)));
        harmony.Patch(generate, prefix: Own(nameof(GeneratePrefix)));
    }

    private static HarmonyMethod Own(string name) => new(typeof(HomeSites).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    private static void GeneratePrefix()
    {
        _rejections = 0;
        _calls = 0;
    }

    // Overload 2 of GenerateColonyForEmpire (the one that loops over attempts) starts a fresh budget.
    private static void ColonyPrefix()
    {
        _rejections = 0;
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void CheckPostfix(Galaxy __instance, Orb orb, float minimumDistanceToOtherEmpireColonies, ref bool __result, ref bool tooCloseToOtherColonies)
    {
        int required = (int)Counts[CountIndex].Value;
        if (required <= 0 || !__result || minimumDistanceToOtherEmpireColonies <= 0f || _rejections >= EnforcedAttempts)
        {
            return;
        }
        try
        {
            float radius = Ranges[RangeIndex].Value;
            float radius2 = radius * radius;
            int neighbours = 0;
            foreach (KeyValuePair<int, StarSystem> entry in __instance.Systems)
            {
                StarSystem? s = entry.Value;
                if (s == null)
                {
                    continue;
                }
                float dx = s.GalaxyX - orb.GalaxyX, dy = s.GalaxyY - orb.GalaxyY;
                float d2 = dx * dx + dy * dy;
                if (d2 > 0f && d2 <= radius2 && ++neighbours >= required)
                {
                    return;
                }
            }
            _rejections++;
            __result = false;
            tooCloseToOtherColonies = false;
            if (++_calls <= 30)
            {
                Bootstrap.Log($"HomeSites: rejected a capital site with only {neighbours} stars nearby (need {required} within {radius / 1e6f:0}M); rejection {_rejections}.");
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("HomeSites.CheckPostfix: " + ex.Message);
        }
    }
}
