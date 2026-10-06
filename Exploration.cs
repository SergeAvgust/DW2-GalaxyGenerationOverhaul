using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// "Galaxy Knowledge" row of the starting-situation step: lets the player's empire start with every star system explored.
//
// Vanilla explores only the systems near the capital (Galaxy.SetEmpireExploration marks the N systems nearest by hyperdrive path
// as explored; for the usual expansion settings N is a handful) and gives every empire the position of every star. For each
// system it explores, the game runs
//     ExplorationMap.SetSystemExploration(galaxy, empire, system.Star, systemLevel, empire.VisibilityMap)
//     VisibilityMap.SetSystemExplored(systemId); VisibilityMap.SetSystemBasesKnown(galaxy, empire, systemId)
// and finishes with ExplorationMap.UpdateLowestLevelForAllSystems. This does the same for every system and then raises the
// exploration level of every orb (planets, moons, asteroids, stars) to the maximum. That level is also the survey depth: resources,
// bonuses and facilities have a DiscoveryLevel and stay hidden until the orb's level reaches it, so at the maximum nothing needs a
// survey ship any more (ruins still have to be investigated).
// It runs once per generated game, right before Generate's final UpdateAllVisibilityMapKnownData, when all empires exist.
internal static class Exploration
{
    public static readonly Option[] Modes = { new("Standard", 0), new("Explored", 1), new("Surveyed", 2) };
    public static int ModeIndex;

    private static bool _pending;

    public static void Install(Harmony harmony)
    {
        MethodInfo? generate = AccessTools.Method(typeof(Galaxy), "Generate");
        MethodInfo? update = AccessTools.Method(typeof(Galaxy), "UpdateAllVisibilityMapKnownData");
        if (generate == null || update == null)
        {
            Bootstrap.Log("Exploration: Galaxy.Generate / UpdateAllVisibilityMapKnownData not found; the explored-galaxy option is disabled.");
            return;
        }
        harmony.Patch(generate, prefix: Own(nameof(GeneratePrefix)), finalizer: Own(nameof(GenerateFinalizer)));
        harmony.Patch(update, prefix: Own(nameof(UpdatePrefix)));
    }

    private static HarmonyMethod Own(string name) => new(typeof(Exploration).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void GeneratePrefix(GameStartSettings settings, bool previewMode, bool isBackgroundGalaxy)
    {
        // The dialog's preview galaxy and the main-menu background have no player.
        _pending = ModeIndex > 0 && !previewMode && !isBackgroundGalaxy && settings.StarCount > 0;
    }

    private static Exception? GenerateFinalizer(Exception? __exception)
    {
        _pending = false;
        return __exception;
    }

    private static void UpdatePrefix(Galaxy __instance)
    {
        if (!_pending)
        {
            return;
        }
        _pending = false;
        try
        {
            ExploreEverything(__instance);
        }
        catch (Exception ex)
        {
            Bootstrap.Log("Exploration: failed: " + ex);
        }
    }

    private static void ExploreEverything(Galaxy galaxy)
    {
        Empire? player = null;
        foreach (Empire empire in galaxy.Empires)
        {
            if (empire.IsPlayer)
            {
                player = empire;
                break;
            }
        }
        if (player?.ExplorationMap == null || player.VisibilityMap == null)
        {
            Bootstrap.Log("Exploration: no player empire (or no exploration map) found.");
            return;
        }

        // Explored: the depth the game gives to the systems around a capital; Surveyed: the maximum.
        byte level = ModeIndex == 2 ? byte.MaxValue : (byte)2;
        int unknownBefore = CountSystemsWithUnknownItems(galaxy, player);

        int count = 0;
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            StarSystem? system = entry.Value;
            if (system?.Star == null)
            {
                continue;
            }
            player.ExplorationMap.SetSystemExploration(galaxy, player, system.Star, level, player.VisibilityMap);
            player.VisibilityMap.SetSystemExplored(system.SystemId);
            player.VisibilityMap.SetSystemBasesKnown(galaxy, player, system.SystemId);
            count++;
        }

        // SetSystemExploration may not reach every orb of a system, so set each one as well.
        int orbs = 0;
        foreach (Orb orb in galaxy.Orbs)
        {
            if (orb != null)
            {
                player.ExplorationMap.SetExplorationLevel(orb, level, true);
                orbs++;
            }
        }
        player.ExplorationMap.UpdateLowestLevelForAllSystems(galaxy, player);

        int unknownAfter = CountSystemsWithUnknownItems(galaxy, player);
        Bootstrap.Log($"Exploration: explored {count} systems and {orbs} orbs for {player.Name} at level {level}; "
            + $"systems with items still unknown to the empire: {unknownBefore} -> {unknownAfter}.");
    }

    private static int CountSystemsWithUnknownItems(Galaxy galaxy, Empire empire)
    {
        int n = 0;
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            if (entry.Value != null && empire.ExplorationMap.CheckSystemHasUnknownItems(entry.Value.SystemId))
            {
                n++;
            }
        }
        return n;
    }
}
