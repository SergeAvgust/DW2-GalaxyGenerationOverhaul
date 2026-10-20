using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// Splits the vanilla "Colony Prevalence" setting in two.
//
// Vanilla stores the button in GameStartSettings.OrbQuality (0.5 / 0.7 / 0.85 / 1.0 / 1.2). It scales the quality range of every
// planet type (OrbType.GenerateQualityRaw), so it changes how good planets are, and only through that how many reach a useful
// quality. Here the vanilla button is read as the *frequency* of habitable planets, and a new row (Planet Quality) provides the
// quality factor that vanilla would have used:
//   - Frequency: while Galaxy.GenerateStarSystems runs, the weighted pick of a child type (OrbTypeFactorList.SelectRandomFactor)
//     multiplies the weight of every habitable type by the chosen factor. Habitable = a planet type whose QualityRangeMinimum is
//     above zero (Continental, Ocean, Desert, Ice, Tundra, Carbonaceous, ...; gas giants and plain rock have a minimum of 0).
//     It is an odds multiplier on those weights; the share of habitable types among the candidates is not fixed.
//   - Quality: Galaxy.Generate swaps GameStartSettings.OrbQuality for the selected quality factor for the duration of the call
//     and puts the button value back afterwards, so the dialog keeps showing what the player chose.
internal static class Habitability
{
    // Vanilla button values, in button order. The frequency is chosen with the vanilla button.
    private static readonly float[] ButtonLevels = { 0.5f, 0.7f, 0.85f, 1.0f, 1.2f };
    private static readonly float[] FrequencyFactors = { 0.35f, 0.65f, 1f, 1.6f, 2.5f };

    // Quality factor handed to the game in place of the button value (vanilla used the button value for this).
    public static readonly Option[] Quality =
    {
        new("Very low", 0.5f), new("Low", 0.7f), new("Normal", 0.85f), new("High", 1.0f), new("Very high", 1.2f),
    };
    public static int QualityIndex = 2;

    // The vanilla Habitable Planets row gets a sixth button, "Scripted only" (see VanillaExtras): its value is below this and no habitable
    // planet is drawn while the stars are generated. The planets made on purpose remain (capitals made in a region by RegionPlacement,
    // planets of scripted events), so it needs Balanced regions for the capitals.
    public const float ScriptedOnlyBelow = 0.3f;

    // What the Habitable Planets buttons say right now, kept up to date by VanillaExtras (0 until the dialog has been used). Galaxy.Generate
    // swaps GameStartSettings.OrbQuality for the quality factor, and the menu runs overlapping preview generations on the same settings
    // object, so a restore from a saved copy can put a swapped value back; this mirror is what gets restored instead.
    public static float ButtonValue;
    // True while a real galaxy is generated with Independent Colonies = None: colonies of the independent empire are not created at all,
    // including the ones scripted events make.
    private static volatile bool _independentsNone;

    // Set while GenerateStarSystems runs; read by the pick patch.
    private static volatile bool _inStarGeneration;
    private static float _multiplier = 1f;
    private static int _picks;
    private static int _habitablePicks;
    private static double _expectedHabitable;
    private static bool[]? _habitable;

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(Galaxy), "Generate", prefix: nameof(GeneratePrefix), finalizer: nameof(GenerateFinalizer));
        Patch(harmony, typeof(Galaxy), "GenerateStarSystems", prefix: nameof(StarsPrefix), finalizer: nameof(StarsFinalizer));
        // Planet and moon types are drawn in GenerateStarSystem / GenerateOrb (every overload), whichever thread runs them.
        foreach (MethodInfo m in typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name == "GenerateStarSystem" || m.Name == "GenerateOrb")
            {
                harmony.Patch(m, prefix: Own(nameof(OrbPrefix)), finalizer: Own(nameof(OrbFinalizer)));
            }
        }
        MethodInfo? pick = AccessTools.Method(typeof(OrbTypeFactorList), "SelectRandomFactor", new[] { typeof(Random), typeof(float) });
        if (pick == null)
        {
            Bootstrap.Log("Habitability: OrbTypeFactorList.SelectRandomFactor(Random, float) not found; frequency setting disabled.");
        }
        else
        {
            harmony.Patch(pick, prefix: Own(nameof(PickPrefix)));
        }

        MethodInfo? independents = AccessTools.Method(typeof(Galaxy), "GenerateIndependentColonies");
        if (independents == null)
        {
            Bootstrap.Log("Habitability: Galaxy.GenerateIndependentColonies not found; the Independents None option is disabled.");
        }
        else
        {
            harmony.Patch(independents, prefix: Own(nameof(IndependentsPrefix)));
        }

        MethodInfo? colonyAtOrb = typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "GenerateColonyAtOrb" && m.GetParameters().Any(p => p.Name == "allowQualityAdjustment"));
        if (colonyAtOrb == null)
        {
            Bootstrap.Log("Habitability: Galaxy.GenerateColonyAtOrb not found; events can still make independent colonies with Independents None.");
        }
        else
        {
            harmony.Patch(colonyAtOrb, prefix: Own(nameof(ColonyAtOrbPrefix)));
        }

        // The vanilla row is now the frequency, say so.
        MethodInfo? text = AccessTools.Method(typeof(TextResolver), "GetText", new[] { typeof(string) });
        if (text == null)
        {
            Bootstrap.Log("Habitability: TextResolver.GetText not found; the vanilla row keeps its old label.");
        }
        else
        {
            harmony.Patch(text, postfix: Own(nameof(TextPostfix)));
        }
    }

    private static HarmonyMethod Own(string name) => new(typeof(Habitability).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    private static void Patch(Harmony harmony, Type type, string method, string prefix, string finalizer)
    {
        MethodInfo? target = AccessTools.Method(type, method);
        if (target == null)
        {
            Bootstrap.Log($"Habitability: {type.Name}.{method} not found.");
            return;
        }
        harmony.Patch(target, prefix: Own(prefix), finalizer: Own(finalizer));
    }

    private static int NearestLevel(float buttonValue)
    {
        int best = 0;
        for (int i = 1; i < ButtonLevels.Length; i++)
        {
            if (Math.Abs(ButtonLevels[i] - buttonValue) < Math.Abs(ButtonLevels[best] - buttonValue))
            {
                best = i;
            }
        }
        return best;
    }

    // ---- Galaxy.Generate: quality factor in, button value back out ----

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void GeneratePrefix(GameStartSettings settings, out float __state)
    {
        __state = ButtonValue > 0f ? ButtonValue : settings.OrbQuality;
        _pickCalls = 0;
        _independentsNone = settings.StarCount > 0 && settings.AlienLifePrevalence <= 0f;
        try
        {
            bool scriptedOnly = __state < ScriptedOnlyBelow;
            _multiplier = scriptedOnly ? 0f : FrequencyFactors[NearestLevel(__state)];
            if (scriptedOnly && RegionPlacement.MethodIndex != 1)
            {
                Bootstrap.Log("Habitability: WARNING: no natural habitable planets with the Standard empire placement; capitals may not be found. Use Balanced regions.");
            }
            settings.OrbQuality = Quality[QualityIndex].Value;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("Habitability.GeneratePrefix: " + ex.Message);
        }
    }

    private static Exception? GenerateFinalizer(Galaxy __instance, GameStartSettings settings, float __state, Exception? __exception)
    {
        settings.OrbQuality = __state;
        _independentsNone = false;
        if (__exception == null && settings.StarCount > 0)
        {
            try
            {
                int habitable = 0;
                foreach (Orb orb in __instance.Orbs)
                {
                    if (orb != null && IsHabitableType(orb.TypeId))
                    {
                        habitable++;
                    }
                }
                int independents = __instance.GetIndependentEmpire()?.Colonies?.Count ?? 0;
                Bootstrap.Log($"Habitability: at the end of generation: {habitable} planets of habitable types, {independents} independent colonies "
                    + $"(button value {__state:0.##}, multiplier x{_multiplier:0.##}, {_pickCalls} type picks seen in star generation).");
            }
            catch (Exception ex)
            {
                Bootstrap.Log("Habitability: end-of-generation count failed: " + ex.Message);
            }
        }
        return __exception;
    }

    // ---- Galaxy.GenerateStarSystems: where planet types are drawn ----

    [ThreadStatic] private static int _orbDepth;
    private static int _pickCalls;

    private static void OrbPrefix() => _orbDepth++;

    private static Exception? OrbFinalizer(Exception? __exception)
    {
        _orbDepth--;
        return __exception;
    }

    private static void StarsPrefix()
    {
        _picks = 0;
        _habitablePicks = 0;
        _expectedHabitable = 0;
        _inStarGeneration = true;
    }

    private static Exception? StarsFinalizer(GameStartSettings settings, Exception? __exception)
    {
        _inStarGeneration = false;
        if (settings.StarCount > 0 && _picks > 0)
        {
            Bootstrap.Log($"Habitability: frequency x{_multiplier:0.##}, quality factor {Quality[QualityIndex].Value:0.##}: {_habitablePicks} of {_picks} planet-type picks were habitable "
                + $"({100.0 * _habitablePicks / _picks:0.0}%; {100.0 * _expectedHabitable / _picks:0.0}% expected without the multiplier).");
        }
        return __exception;
    }

    public static bool IsHabitableType(byte orbTypeId) => HabitableTypes()[orbTypeId];

    private static bool[] HabitableTypes()
    {
        bool[]? table = _habitable;
        if (table != null)
        {
            return table;
        }
        table = new bool[256];
        var names = new List<string>();
        foreach (OrbType type in Galaxy.OrbTypesStatic)
        {
            if (type.Category == OrbCategory.Planet && type.QualityRangeMinimum > 0f)
            {
                table[type.OrbTypeId] = true;
                names.Add($"{type.Name}({type.QualityRangeMinimum:0.##})");
            }
        }
        Bootstrap.Log("Habitability: habitable types: " + string.Join(", ", names));
        _habitable = table;
        return table;
    }

    // Replaces OrbTypeFactorList.SelectRandomFactor(rnd, minimumFactor) while stars are generated: the same single weighted draw,
    // with habitable types counted `_multiplier` times heavier. Lists without a habitable type, or a multiplier of 1, run the original.
    private static bool PickPrefix(OrbTypeFactorList __instance, Random rnd, float minimumFactor, ref OrbTypeFactor __result)
    {
        if (!_inStarGeneration && _orbDepth <= 0)
        {
            return true;
        }
        Interlocked.Increment(ref _pickCalls);
        try
        {
            bool[] habitable = HabitableTypes();
            float multiplier = _multiplier;
            double total = 0, habitableWeight = 0;
            foreach (OrbTypeFactor f in __instance)
            {
                if (f.Factor < minimumFactor)
                {
                    continue;
                }
                total += f.Factor;
                if (habitable[f.OrbTypeId])
                {
                    habitableWeight += f.Factor;
                }
            }
            if (habitableWeight <= 0 || total <= 0)
            {
                return true;
            }
            if (multiplier == 1f || total - habitableWeight <= 0)
            {
                return true;
            }

            double weighted = total + habitableWeight * (multiplier - 1.0);
            double r = rnd.NextDouble() * weighted;
            double sum = 0;
            OrbTypeFactor picked = OrbTypeFactor.Empty;
            foreach (OrbTypeFactor f in __instance)
            {
                if (f.Factor < minimumFactor)
                {
                    continue;
                }
                sum += habitable[f.OrbTypeId] ? f.Factor * multiplier : f.Factor;
                if (r < sum)
                {
                    picked = f;
                    break;
                }
            }
            __result = picked;

            _picks++;
            _expectedHabitable += habitableWeight / total;
            if (!picked.IsEmpty && habitable[picked.OrbTypeId])
            {
                _habitablePicks++;
            }
            return false;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("Habitability.PickPrefix error, falling back to vanilla: " + ex.Message);
            return true;
        }
    }

    // Colonies made by scripted events for the independent empire (an event passes no empire, or the independent one). The callers drop the result.
    private static bool ColonyAtOrbPrefix(Galaxy __instance, Empire empire, ref Colony? __result)
    {
        if (!_independentsNone)
        {
            return true;
        }
        try
        {
            if (empire != null && empire.EmpireId != __instance.GetIndependentEmpire()?.EmpireId)
            {
                return true;
            }
        }
        catch
        {
            return true;
        }
        __result = null;
        return false;
    }

    // The vanilla Independent Colonies row gets a sixth button, "None", which stores 0.
    private static bool IndependentsPrefix(GameStartSettings settings)
    {
        if (settings.AlienLifePrevalence > 0f)
        {
            return true;
        }
        Bootstrap.Log("Independents: none generated (option).");
        return false;
    }

    // ---- labels of the vanilla row ----

    private static void TextPostfix(string tag, ref string __result)
    {
        switch (tag)
        {
            case "Colony Prevalence":
                __result = "Habitable Planets";
                break;
            case "Colony Prevalence Explanation":
                __result = "How common habitable planets and moons are: types that can be colonised at all, from Continental and Ocean worlds to "
                    + "desert, ice, tundra and volcanic ones, which are poor but may suit some races. Gas giants and bare rock are not counted. "
                    + "This changes how many there are, not how good they are; that is Planet Quality below.\n\nScripted only generates none by chance: "
                    + "the only habitable planets are those made on purpose, such as the home planets of the empires and planets placed by events. "
                    + "It needs Empire Placement set to Balanced regions, which makes a home planet in each region.\n\nThe other settings that "
                    + "change the number of potential colonies are the Independent Colonies setting and the star count.";
                break;
        }
    }
}
