using System.Reflection;
using DistantWorlds.UI;
using HarmonyLib;

namespace SpiralArms;

// The new-game dialog restricts the galaxy size by the star count (StartNewGameDialog.SetGalaxySizeLimits): below 700 stars only the
// smallest size is allowed (and a larger one is switched back), below 1000 the two smallest. For 700 stars or fewer every size
// stays available and the player's choice is left alone.
internal static class SizeLimits
{
    private const int LimitedBelow = 701;

    private static FieldInfo? _starAmount;
    private static MethodInfo? _resolveStars;
    private static MethodInfo? _enableButtons;

    public static void Install(Harmony harmony)
    {
        MethodInfo? limits = AccessTools.Method(typeof(StartNewGameDialog), "SetGalaxySizeLimits");
        _starAmount = AccessTools.Field(typeof(StartNewGameDialog), "GalaxyStarAmount");
        _resolveStars = AccessTools.Method(typeof(StartNewGameDialog), "ResolveGalaxyStarAmount");
        _enableButtons = AccessTools.Method(typeof(StartNewGameDialog), "SetGalaxyDimensionsEnabledButtons");
        if (limits == null || _starAmount == null || _resolveStars == null || _enableButtons == null)
        {
            Bootstrap.Log("SizeLimits: dialog members not found; the size limits for small galaxies stay.");
            return;
        }
        harmony.Patch(limits, prefix: new HarmonyMethod(typeof(SizeLimits).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
    }

    private static bool Prefix(StartNewGameDialog __instance)
    {
        try
        {
            var group = (ToggleButtonGroup?)_starAmount!.GetValue(__instance);
            if (group == null)
            {
                return true;
            }
            int stars = (int)_resolveStars!.Invoke(__instance, new object[] { group.ToggledButtonIndex })!;
            if (stars >= LimitedBelow)
            {
                return true;
            }
            _enableButtons!.Invoke(__instance, new object[] { stars, true, true, true, true });
            return false;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("SizeLimits: " + ex.Message);
            return true;
        }
    }
}
