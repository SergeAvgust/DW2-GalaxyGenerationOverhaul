using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// Balanced-regions placement of the empire capitals (see Regions for how the galaxy is split).
//
// Vanilla looks for a native-type planet near a random point (Galaxy.GenerateColonyForEmpire, the overload taking GameStartSettings).
// Here, when the first capital is placed, the galaxy is split into one region per empire, and each capital gets a region:
//   - the player's region follows the start location chosen in the dialog (a point on the map, or a ring: deep core / core / rim);
//     "random" picks any region;
//   - AI empires take the remaining regions in random order; empires placed relative to the player take the region at the distance
//     they ask for;
//   - the search then starts at a star of that region that has a habitable planet, and HomeSites rejects every site outside the
//     region (the game retries, as it does for any rejected site). The minimum distance between empires is lowered, because regions
//     of equal star count can be close together where the stars are dense.
// If anything is missing (no plan, no free region), the vanilla search is left alone.
internal static class RegionPlacement
{
    public static readonly Option[] Methods = { new("Standard", 0), new("Balanced regions", 1) };
    public static int MethodIndex = 1;
    // Which stars may hold a capital: the density classes of Regions (3 dense, 2 moderate, 1 sparse, 0 void).
    public static readonly Option[] StarClasses = { new("Dense only", 3), new("Dense + moderate", 2), new("All but voids", 1) };
    public static int StarClassIndex = 1;

    public static int MinDensity => (int)StarClasses[StarClassIndex].Value;

    private static bool _game;
    private static bool _planTried;
    private static Regions.Plan? _plan;
    private static bool[] _taken = Array.Empty<bool>();
    private static int _playerRegion = -1;
    private static readonly Dictionary<short, int> _assigned = new();
    private static Random _rnd = new();
    private static GameStartSettingsEmpire? _empireStart;
    private static GameStartSettingsEmpireList? _empires;   // everyone being generated: the player and the AI empires
    private static int _activeRegion = -1;
    private static Dictionary<int, int> _orbSystem = new();   // orb id -> system id, built with the plan
    private static StarSystem? _activeSite;
    private static int _rejectionLogs;

    public static void Install(Harmony harmony)
    {
        MethodInfo? generate = AccessTools.Method(typeof(Galaxy), "Generate");
        MethodInfo? colony = null, empire = null;
        foreach (MethodInfo m in typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == "GenerateColonyForEmpire" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(GameStartSettings))
            {
                colony = m;
            }
            if (m.Name == "GenerateEmpire" && m.GetParameters().Any(p => p.Name == "empireStart" && p.ParameterType == typeof(GameStartSettingsEmpire)))
            {
                empire = m;
            }
        }
        if (generate == null || colony == null || empire == null)
        {
            Bootstrap.Log("RegionPlacement: Generate / GenerateColonyForEmpire / GenerateEmpire not found; balanced regions are disabled.");
            return;
        }
        harmony.Patch(generate, prefix: Own(nameof(GeneratePrefix)), finalizer: Own(nameof(GenerateFinalizer)));
        harmony.Patch(empire, prefix: Own(nameof(EmpirePrefix)));
        harmony.Patch(colony, prefix: Own(nameof(ColonyPrefix)), finalizer: Own(nameof(ColonyFinalizer)));

        // The search takes the native planet nearest by travel time; systems outside the region are made far away (see TimesPostfix).
        int times = 0;
        foreach (MethodInfo m in typeof(StarSystemListExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == "CalculateSystemTimes" && m.ReturnType == typeof(Dictionary<int, float>))
            {
                harmony.Patch(m, postfix: Own(nameof(TimesPostfix)));
                times++;
            }
        }
        if (times == 0)
        {
            Bootstrap.Log("RegionPlacement: StarSystemListExtensions.CalculateSystemTimes not found; sites outside a region can only be rejected.");
        }
    }

    // Far beyond any real travel time, so a planet outside the region is only found when the region has no native planet at all.
    private const float OutsidePenalty = 1e9f;

    private static void TimesPostfix(ref Dictionary<int, float> __result)
    {
        if (_activeRegion < 0 || _plan == null || __result == null)
        {
            return;
        }
        try
        {
            int pushed = 0, total = 0;
            foreach (int id in __result.Keys.ToList())
            {
                total++;
                if (!_plan.RegionOfSystem.TryGetValue(id, out int region) || region != _activeRegion)
                {
                    __result[id] += OutsidePenalty;
                    pushed++;
                }
            }
            Bootstrap.Log($"RegionPlacement: travel times from the search start: {pushed} of {total} systems pushed out of reach (region {_activeRegion}).");
        }
        catch (Exception ex)
        {
            Bootstrap.Log("RegionPlacement.TimesPostfix: " + ex.Message);
        }
    }

    private static HarmonyMethod Own(string name) => new(typeof(RegionPlacement).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void GeneratePrefix(GameStartSettings settings, bool previewMode, bool isBackgroundGalaxy)
    {
        _game = MethodIndex == 1 && !previewMode && !isBackgroundGalaxy && settings.StarCount > 0;
        _planTried = false;
        _plan = null;
        _playerRegion = -1;
        _assigned.Clear();
        _empireStart = null;
        _empires = null;
        _activeRegion = -1;
        _orbSystem = new Dictionary<int, int>();
        _rnd = new Random();
    }

    private static Exception? GenerateFinalizer(Exception? __exception)
    {
        _game = false;
        _plan = null;
        _activeRegion = -1;
        return __exception;
    }

    // Only the start settings are read, so the method's by-reference parameter is left alone.
    private static void EmpirePrefix(GameStartSettingsEmpire empireStart, GameStartSettingsEmpireList empiresToGenerate)
    {
        _empireStart = empireStart;
        _empires = empiresToGenerate;
    }

    private static Galaxy? _galaxy;

    private static void galaxyLookup(int systemId, out int x, out int y)
    {
        x = y = 0;
        if (_galaxy != null && _galaxy.Systems.TryGetValue(systemId, out StarSystem? system) && system != null)
        {
            x = system.GalaxyX;
            y = system.GalaxyY;
        }
    }

    private static void ColonyPrefix(Galaxy __instance, GameStartSettings settings, Empire empire, Race race, ref Vector2 searchCenter, ref float minimumDistanceToOtherEmpireColonies)
    {
        _activeRegion = -1;
        _galaxy = __instance;
        if (!_game || empire.IsPirate)
        {
            return;
        }
        try
        {
            if (!_planTried)
            {
                _planTried = true;
                BuildPlan(__instance, settings);
            }
            if (_plan == null)
            {
                return;
            }
            int region = RegionFor(settings, empire);
            if (region < 0)
            {
                Bootstrap.Log($"RegionPlacement: {empire.Name} has no region (all taken); vanilla search.");
                return;
            }
            StarSystem? site = ChooseSite(__instance, region, empire, race);
            if (site == null)
            {
                Bootstrap.Log($"RegionPlacement: region {region} has no site for {empire.Name}; vanilla search.");
                return;
            }
            searchCenter = new Vector2(site.GalaxyX, site.GalaxyY);
            // Regions have equal star counts, not equal areas: in dense parts neighbouring capitals can be close.
            float floor = 0.1f * settings.Dimensions / (float)Math.Sqrt(Math.Max(1, _plan.K));
            if (minimumDistanceToOtherEmpireColonies > floor)
            {
                minimumDistanceToOtherEmpireColonies = floor;
            }
            _activeRegion = region;
            _activeSite = site;
            _rejectionLogs = 0;
            Bootstrap.Log($"RegionPlacement: {empire.Name}{(IsPlayer(empire) ? " (player)" : "")} -> region {region} ({_plan.Sizes[region]} stars), search starts at system {site.SystemId} ({site.GalaxyX / 1e6f:0}M, {site.GalaxyY / 1e6f:0}M).");
        }
        catch (Exception ex)
        {
            _activeRegion = -1;
            Bootstrap.Log("RegionPlacement.ColonyPrefix: " + ex);
        }
    }

    // A capital that could not be placed is thrown away and generated again with another race; give its region back.
    private static Exception? ColonyFinalizer(Empire empire, Colony? __result, Exception? __exception)
    {
        if (_activeRegion >= 0 && __result != null && _activeSite != null)
        {
            try
            {
                int sys = _orbSystem.TryGetValue(__result.OrbId, out int mapped) ? mapped : -1;
                bool known = _plan!.RegionOfSystem.TryGetValue(sys, out int placedRegion);
                bool inside = known && placedRegion == _activeRegion;
                galaxyLookup(sys, out int sx, out int sy);
                float dx = sx - _activeSite.GalaxyX, dy = sy - _activeSite.GalaxyY;
                Bootstrap.Log($"RegionPlacement: RESULT {empire.Name}: capital in system {sys}, {(inside ? "INSIDE" : "OUTSIDE")} region {_activeRegion}"
                    + $" (actually region {(known ? placedRegion.ToString() : "none")}), {Math.Sqrt(dx * dx + dy * dy) / 1e6f:0}M from the search start.");
            }
            catch (Exception ex)
            {
                Bootstrap.Log("RegionPlacement outcome: " + ex.Message);
            }
        }
        if (_activeRegion >= 0 && __result == null && __exception == null && !IsPlayer(empire) && _activeRegion < _taken.Length)
        {
            Bootstrap.Log($"RegionPlacement: no capital could be placed for {empire.Name} in region {_activeRegion}; the region is free again.");
            _taken[_activeRegion] = false;
            _assigned.Remove(empire.EmpireId);
        }
        _activeRegion = -1;
        return __exception;
    }

    private static void BuildPlan(Galaxy galaxy, GameStartSettings settings)
    {
        GameStartSettingsEmpireList empires = _empires ?? settings.Empires;
        int k = empires.Count;
        Bootstrap.Log($"RegionPlacement: {empires.Count} empires to place (settings list: {settings.Empires.Count}).");
        _plan = Regions.Build(galaxy, k, MinDensity);
        if (_plan == null)
        {
            Bootstrap.Log($"RegionPlacement: no plan ({galaxy.Systems.Count} stars, {k} empires); vanilla placement.");
            return;
        }
        _taken = new bool[k];
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            if (entry.Value == null)
            {
                continue;
            }
            foreach (Orb orb in entry.Value.GetOrbs())
            {
                _orbSystem[orb.OrbId] = entry.Key;
            }
        }
        Bootstrap.Log($"RegionPlacement: {k} regions of {_plan.Sizes.Min()}..{_plan.Sizes.Max()} stars ({_plan.Dropped} candidate stars left out).");

        GameStartSettingsEmpire? player = null;
        foreach (GameStartSettingsEmpire e in empires)
        {
            if (e.IsPlayer)
            {
                player = e;
                break;
            }
        }
        if (player != null)
        {
            _playerRegion = ChoosePlayerRegion(settings, player);
            if (_playerRegion >= 0)
            {
                _taken[_playerRegion] = true;
            }
        }
    }

    // The player's start location: a point on the map, a ring (min/max radius as fractions of the galaxy radius), or anywhere.
    private static int ChoosePlayerRegion(GameStartSettings settings, GameStartSettingsEmpire player)
    {
        Regions.Plan plan = _plan!;
        float dim = settings.Dimensions / 1e6f;
        Vector2 sector = player.StartLocationSector;
        if (sector.X >= 0f && sector.Y >= 0f && (sector.X > 0f || sector.Y > 0f))
        {
            return RegionOfNearest(plan, sector.X * dim, sector.Y * dim);
        }
        float min = player.StartLocationMinimumRadius, max = player.StartLocationMaximumRadius;
        if (min == 0f && max == 1f)
        {
            return RandomFreeRegion();
        }
        // Band: fractions of the galaxy radius from the centre (deep core 0-0.2, outer core 0.2-0.52, and so on, per galaxy shape).
        // The region with the largest share of its stars inside the band wins; ties go to the one closest to the middle of the band.
        float mid = 0.5f * (min + max);
        float half = dim / 2;
        int best = -1;
        float bestShare = -1f, bestDiff = float.MaxValue;
        for (int r = 0; r < plan.K; r++)
        {
            List<Regions.Star> members = plan.Members[r];
            if (members.Count == 0)
            {
                continue;
            }
            int inside = 0;
            foreach (Regions.Star star in members)
            {
                float f = (float)Math.Sqrt((star.X - half) * (star.X - half) + (star.Y - half) * (star.Y - half)) / half;
                if (f >= min && f <= max)
                {
                    inside++;
                }
            }
            float share = inside / (float)members.Count;
            (float cx, float cy) = Centroid(plan, r);
            float diff = Math.Abs((float)Math.Sqrt((cx - half) * (cx - half) + (cy - half) * (cy - half)) / half - mid);
            if (share > bestShare + 0.05f || (Math.Abs(share - bestShare) <= 0.05f && diff < bestDiff))
            {
                bestShare = Math.Max(share, bestShare);
                bestDiff = diff;
                best = r;
            }
        }
        Bootstrap.Log($"RegionPlacement: player start band {min:0.##}-{max:0.##} of the galaxy radius -> region {best} ({bestShare * 100:0}% of its stars inside the band).");
        return best;
    }

    // Empire.IsPlayer is not set yet while the capital is placed; the start settings handed to GenerateEmpire say who the player is.
    private static bool IsPlayer(Empire empire) => empire.IsPlayer || (_empireStart != null && _empireStart.IsPlayer);

    private static int RegionFor(GameStartSettings settings, Empire empire)
    {
        if (_assigned.TryGetValue(empire.EmpireId, out int known))
        {
            return known;
        }
        Regions.Plan plan = _plan!;
        int region;
        if (IsPlayer(empire))
        {
            region = _playerRegion;
        }
        else if (_empireStart != null && _empireStart.StartLocationRelativeToPlayer && _playerRegion >= 0)
        {
            // Ring around the player: the asked-for radius is a fraction of half the galaxy size.
            float dim = settings.Dimensions / 1e6f;
            float target = 0.5f * (_empireStart.StartLocationMinimumRadius + _empireStart.StartLocationMaximumRadius) * 0.5f * dim;
            (float px, float py) = Centroid(plan, _playerRegion);
            region = -1;
            float bestDiff = float.MaxValue;
            for (int r = 0; r < plan.K; r++)
            {
                if (_taken[r] || plan.Members[r].Count == 0)
                {
                    continue;
                }
                (float cx, float cy) = Centroid(plan, r);
                float diff = Math.Abs((float)Math.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py)) - target);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    region = r;
                }
            }
        }
        else
        {
            region = RandomFreeRegion();
        }
        if (region >= 0)
        {
            _taken[region] = true;
            _assigned[empire.EmpireId] = region;
        }
        return region;
    }

    private static int RandomFreeRegion()
    {
        var free = new List<int>();
        for (int r = 0; r < _taken.Length; r++)
        {
            if (!_taken[r] && _plan!.Members[r].Count > 0)
            {
                free.Add(r);
            }
        }
        return free.Count == 0 ? -1 : free[_rnd.Next(free.Count)];
    }

    private static (float X, float Y) Centroid(Regions.Plan plan, int region)
    {
        float x = 0, y = 0;
        foreach (Regions.Star s in plan.Members[region])
        {
            x += s.X;
            y += s.Y;
        }
        int n = Math.Max(1, plan.Members[region].Count);
        return (x / n, y / n);
    }

    private static int RegionOfNearest(Regions.Plan plan, float x, float y)
    {
        Regions.Star? best = null;
        float bestD = float.MaxValue;
        foreach (Regions.Star s in plan.Candidates)
        {
            float d = (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best?.Region ?? -1;
    }

    // The capital goes to the star of the region closest to the region's site (fewest jumps, then shortest distance) that has a
    // planet of a type this race can use as a home. If the region has none, a planet of the star at the site is turned into one.
    private static StarSystem? ChooseSite(Galaxy galaxy, int region, Empire empire, Race race)
    {
        Regions.Plan plan = _plan!;
        List<byte> natives = NativeTypes(galaxy, empire, race);
        Dictionary<int, int> hops = HopsFromSite(plan, region);
        Regions.Star middle = plan.Sites[region]!;
        List<StarSystem> ordered = plan.Members[region]
            .OrderBy(m => hops.TryGetValue(m.Id, out int h) ? h : int.MaxValue)
            .ThenBy(m => (m.X - middle.X) * (m.X - middle.X) + (m.Y - middle.Y) * (m.Y - middle.Y))
            .Select(m => galaxy.Systems.TryGetValue(m.Id, out StarSystem? sys) ? sys : null)
            .Where(sys => sys != null && sys.ColonyCount == 0)
            .Select(sys => sys!)
            .ToList();
        if (ordered.Count == 0)
        {
            return null;
        }
        if (natives.Count > 0)
        {
            foreach (StarSystem system in ordered)
            {
                if (HasPlanetOfTypes(system, natives))
                {
                    Bootstrap.Log($"RegionPlacement: region {region}: system {system.SystemId} is the closest one to the region site with a native planet ({hops.GetValueOrDefault(system.SystemId, -1)} jumps from the site).");
                    return system;
                }
            }
            foreach (StarSystem system in ordered)
            {
                if (TryForgeNativePlanet(galaxy, system, natives[0]))
                {
                    Bootstrap.Log($"RegionPlacement: region {region} had no native planet; made one in system {system.SystemId} ({hops.GetValueOrDefault(system.SystemId, -1)} jumps from the site).");
                    return system;
                }
            }
        }
        Bootstrap.Log($"RegionPlacement: region {region}: no native planet and none could be made; starting at the system nearest the site, {ordered[0].SystemId}.");
        return ordered[0];
    }

    // What the game accepts as a home planet for the race: its single best colonization type, or the best three when it has no preference.
    private static List<byte> NativeTypes(Galaxy galaxy, Empire empire, Race race)
    {
        var result = new List<byte>();
        try
        {
            OrbTypeFactorList? modifiers = race.ColonizationSuitabilityModifiers;
            if (modifiers != null && modifiers.Count > 0)
            {
                result.Add(modifiers.GetHighest().OrbTypeId);
                return result;
            }
            OrbTypeList? sorted = galaxy.OrbTypes.GetByCategory(OrbCategory.Planet).GetSortedByRaceColonizationSuitability(empire, race);
            if (sorted != null && sorted.Count > 0)
            {
                foreach (OrbType type in sorted.GetFirstCount(3))
                {
                    result.Add(type.OrbTypeId);
                }
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("RegionPlacement.NativeTypes: " + ex.Message);
        }
        return result;
    }

    private static bool HasPlanetOfTypes(StarSystem system, List<byte> types)
    {
        if (system.MajorOrbs == null)
        {
            return false;
        }
        foreach (Orb orb in system.MajorOrbs)
        {
            if (orb != null && orb.Category == OrbCategory.Planet && types.Contains(orb.TypeId))
            {
                return true;
            }
        }
        return false;
    }

    // Turns the planet of the system that looks least like the wanted type into it: a rock or ice ball rather than a habitable world
    // or gas giant. Only the type and size change; resources and the rest stay as generated.
    private static bool TryForgeNativePlanet(Galaxy galaxy, StarSystem system, byte typeId)
    {
        try
        {
            OrbType? target = galaxy.OrbTypes.GetById(typeId);
            if (target == null || system.MajorOrbs == null)
            {
                return false;
            }
            Orb? best = null;
            float bestScore = float.MaxValue;
            foreach (Orb orb in system.MajorOrbs)
            {
                if (orb == null || orb.Category != OrbCategory.Planet)
                {
                    continue;
                }
                OrbType current = orb.GetOrbType();
                if (current == null)
                {
                    continue;
                }
                // Prefer planets of a size close to the wanted type and not habitable themselves.
                float score = Math.Abs(orb.Diameter - 0.5f * (target.DiameterMinimum + target.DiameterMaximum))
                    + (current.QualityRangeMinimum > 0f ? 1e9f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = orb;
                }
            }
            if (best == null)
            {
                return false;
            }
            byte oldType = best.TypeId;
            best.TypeId = typeId;
            best.Diameter = (int)(target.DiameterMinimum + _rnd.NextDouble() * (target.DiameterMaximum - target.DiameterMinimum));
            // The game only offers a planet as a home when its quality suits the race, and the old type's quality is usually far too low.
            best.QualityRaw = target.GenerateQualityRaw(_rnd, Habitability.Quality[Habitability.QualityIndex].Value);
            Bootstrap.Log($"RegionPlacement: planet {best.OrbId} in system {system.SystemId} changed from type {oldType} to {typeId} ({target.Name}).");
            return true;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("RegionPlacement.TryForgeNativePlanet: " + ex.Message);
            return false;
        }
    }

    // Number of jumps from the region's site to each star of the region, over links of up to the neighbour range (regions are grown
    // along these links, so they are connected). Stars cut off from the site are left out.
    private static Dictionary<int, int> HopsFromSite(Regions.Plan plan, int region)
    {
        List<Regions.Star> members = plan.Members[region];
        var result = new Dictionary<int, int>();
        Regions.Star? site = plan.Sites[region];
        if (site == null)
        {
            return result;
        }
        float range2 = plan.Range * plan.Range;
        var queue = new Queue<Regions.Star>();
        result[site.Id] = 0;
        queue.Enqueue(site);
        while (queue.Count > 0)
        {
            Regions.Star u = queue.Dequeue();
            foreach (Regions.Star v in members)
            {
                if (result.ContainsKey(v.Id))
                {
                    continue;
                }
                float dx = u.X - v.X, dy = u.Y - v.Y;
                if (dx * dx + dy * dy <= range2)
                {
                    result[v.Id] = result[u.Id] + 1;
                    queue.Enqueue(v);
                }
            }
        }
        return result;
    }

    private static bool HasHabitablePlanet(StarSystem system)
    {
        if (system.MajorOrbs == null)
        {
            return false;
        }
        foreach (Orb orb in system.MajorOrbs)
        {
            if (orb != null && orb.Category == OrbCategory.Planet && Habitability.IsHabitableType(orb.TypeId))
            {
                return true;
            }
        }
        return false;
    }

    private static int SystemIdOf(Galaxy galaxy, Orb orb) => _orbSystem.TryGetValue(orb.OrbId, out int id) ? id : orb.GetSystemId(galaxy);

    public static StarSystem? SystemOf(Galaxy galaxy, Orb orb)
    {
        int id = SystemIdOf(galaxy, orb);
        return galaxy.Systems.TryGetValue(id, out StarSystem? system) ? system : null;
    }

    // True when the capital being placed has a region and this planet is outside it.
    public static bool RejectsOrb(Galaxy galaxy, Orb orb)
    {
        if (_activeRegion < 0 || _plan == null)
        {
            return false;
        }
        int id = SystemIdOf(galaxy, orb);
        bool known = _plan.RegionOfSystem.TryGetValue(id, out int region);
        bool outside = !known || region != _activeRegion;
        if (outside && _rejectionLogs++ < 3)
        {
            Bootstrap.Log($"RegionPlacement: planet {orb.OrbId} ({orb.TypeId}) is in system {id}, region {(known ? region.ToString() : "none")}, wanted {_activeRegion}; "
                + $"mapped {_orbSystem.ContainsKey(orb.OrbId)}, GetSystemId {orb.GetSystemId(galaxy)}.");
        }
        return outside;
    }
}
