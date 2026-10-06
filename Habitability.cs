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
        MethodInfo? pick = AccessTools.Method(typeof(OrbTypeFactorList), "SelectRandomFactor", new[] { typeof(Random), typeof(float) });
        if (pick == null)
        {
            Bootstrap.Log("Habitability: OrbTypeFactorList.SelectRandomFactor(Random, float) not found; frequency setting disabled.");
        }
        else
        {
            harmony.Patch(pick, prefix: Own(nameof(PickPrefix)));
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
        __state = settings.OrbQuality;
        try
        {
            _multiplier = FrequencyFactors[NearestLevel(settings.OrbQuality)];
            settings.OrbQuality = Quality[QualityIndex].Value;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("Habitability.GeneratePrefix: " + ex.Message);
        }
    }

    private static Exception? GenerateFinalizer(GameStartSettings settings, float __state, Exception? __exception)
    {
        settings.OrbQuality = __state;
        return __exception;
    }

    // ---- Galaxy.GenerateStarSystems: where planet types are drawn ----

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
        if (!_inStarGeneration)
        {
            return true;
        }
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
            if (multiplier == 1f)
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
                    + "This changes how many there are, not how good they are; that is Planet Quality below.\n\nThe other settings that "
                    + "change the number of potential colonies are the Independent Colonies setting and the star count.";
                break;
        }
    }
}
