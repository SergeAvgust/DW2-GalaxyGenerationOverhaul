using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;

namespace SpiralArms;

// Entry point invoked by the launcher's loader (see DW2-Mod-Launcher/docs/DLL Injection.md). The options are chosen on the
// new-game screen (SpiralUi) and held in SpiralSettings.
//
// Vanilla places every star of a Spiral (and Elliptical) galaxy with Galaxy.DeterminePointCenterEased:
//   angle = U*2pi, r = R*U^0.7, point = (R,R) + r*(cos, sin)      (R = Dimensions/2)
// i.e. a plain disc with no arms. This prefix replaces that for GalaxyShape.Spiral only; Elliptical shares
// the same method and is passed through to the original untouched.
public static class Bootstrap
{
    private const float TwoPi = MathF.PI * 2f;

    // Vanilla-style winding mirrored once and for all (no setting).
    private const float WindSign = -1f;

    private static bool _started;
    private static string _logPath = string.Empty;

    public static void Init()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        if (_logPath.Length == 0)
        {
            _logPath = Path.Combine(Path.GetTempPath(), "SpiralArms.log");
        }

        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Galaxy), "DeterminePointCenterEased");
            if (target == null)
            {
                Log("ERROR: Galaxy.DeterminePointCenterEased not found.");
                return;
            }

            var harmony = new Harmony("spiralarms.dw2mod");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Bootstrap).GetMethod(nameof(PlacePrefix), BindingFlags.Static | BindingFlags.NonPublic)));

            MethodInfo? cloudTarget = AccessTools.Method(typeof(Galaxy), "DetermineCloudSettingsSpiralArm");
            if (cloudTarget == null)
            {
                Log("WARN: Galaxy.DetermineCloudSettingsSpiralArm not found; nebulae keep vanilla placement.");
            }
            else
            {
                harmony.Patch(cloudTarget, prefix: new HarmonyMethod(typeof(Bootstrap).GetMethod(nameof(CloudPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            Log($"PATCH OK. arms={SpiralSettings.ArmCount} pitch={SpiralSettings.PitchRad:F3}rad spread={SpiralSettings.SpreadRad:F3}rad core={SpiralSettings.CoreRadius} coreShare={SpiralSettings.CoreShare} fieldShare={SpiralSettings.FieldShare} thinning={SpiralSettings.EdgeThinning} taper={SpiralSettings.ArmTaperValue}");

            SpiralUi.Install(harmony);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION during mod init: " + ex);
        }
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    // Returning false skips the original; returning true leaves other shapes alone.
    private static bool PlacePrefix(Random rnd, GameStartSettings settings, float centerRadius, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (settings.Shape != GalaxyShape.Spiral)
        {
            return true;
        }

        try
        {
            float radius = centerRadius;
            float coreR = SpiralSettings.CoreRadius * radius;
            float pick = (float)rnd.NextDouble();
            float r;
            float theta;

            if (pick < SpiralSettings.CoreShare)
            {
                // Central blob: uniform density inside the core disc.
                r = coreR * MathF.Sqrt((float)rnd.NextDouble());
                theta = (float)rnd.NextDouble() * TwoPi;
            }
            else if (pick < SpiralSettings.CoreShare + SpiralSettings.FieldShare)
            {
                // Sparse inter-arm field: same radial falloff as the arms, any angle.
                r = RadialSample(rnd, coreR, radius);
                theta = (float)rnd.NextDouble() * TwoPi;
            }
            else
            {
                // Arm star: logarithmic spiral  theta = phase + arm*2pi/N + ln(r/r0)/tan(pitch), then scatter.
                r = RadialSample(rnd, coreR, radius);
                int arm = rnd.Next(SpiralSettings.ArmCount);
                float phase = ArmPhase(settings.RandomSeed);
                float wind = WindSign * MathF.Log(r / coreR) / MathF.Tan(SpiralSettings.PitchRad);
                theta = phase + arm * TwoPi / SpiralSettings.ArmCount + wind + Gaussian(rnd) * ArmSpread(r, radius);
            }

            x = Math.Clamp(radius + r * MathF.Cos(theta), 0f, 2f * radius);
            y = Math.Clamp(radius + r * MathF.Sin(theta), 0f, 2f * radius);
            return false;
        }
        catch (Exception ex)
        {
            Log("PlacePrefix error, falling back to vanilla: " + ex.Message);
            return true;
        }
    }

    // Vanilla DetermineCloudSettingsSpiralArm (shared by Classic and Spiral) walks a fixed 2-arm spiral from
    // Calculations.CalculateSpiralPoint. For Spiral this replaces it with clouds that follow the star arms,
    // either on them or in the lanes between them (NebulaPlacement setting).
    // GenerateNebulae still decides *how many* clouds exist (from the NebulaDensity setting); this only decides where.
    // Parameter names must match the original's. Mirrors the vanilla method's outputs and its 100-try
    // rejection against overlapping clouds.
    private static bool CloudPrefix(Galaxy __instance, Random rnd, GameStartSettings settings, float cloudSizeMinimum, float cloudSizeMaximum, float permeabilityMinimum, float permeabilityMaximum,
        out float x, out float y, out float radius, out string name, out float permeability)
    {
        x = 0f;
        y = 0f;
        radius = 1f;
        name = string.Empty;
        permeability = 1f;
        if (settings.Shape != GalaxyShape.Spiral)
        {
            return true;
        }

        try
        {
            float galaxyRadius = settings.Dimensions * 0.5f;
            float coreR = SpiralSettings.CoreRadius * galaxyRadius;
            // Inside the arms: on the arm centre line. Between: halfway to the neighbouring arm.
            float phase = ArmPhase(settings.RandomSeed) + (SpiralSettings.NebulasBetweenArms ? MathF.PI / SpiralSettings.ArmCount : 0f);

            for (int attempt = 0; attempt < 100; attempt++)
            {
                float r = RadialSample(rnd, coreR, galaxyRadius);
                int arm = rnd.Next(SpiralSettings.ArmCount);
                float theta = phase + arm * TwoPi / SpiralSettings.ArmCount + WindSign * MathF.Log(r / coreR) / MathF.Tan(SpiralSettings.PitchRad) + Gaussian(rnd) * ArmSpread(r, galaxyRadius);

                x = galaxyRadius + r * MathF.Cos(theta);
                y = galaxyRadius + r * MathF.Sin(theta);
                // Clouds grow toward the rim, as in vanilla (which scales by sqrt of the walk progress).
                radius = (cloudSizeMinimum + (float)rnd.NextDouble() * (cloudSizeMaximum - cloudSizeMinimum)) * MathF.Sqrt(Math.Max(0.1f, r / galaxyRadius));

                if (!__instance.NebulaClouds.CheckAnyWithinRange((int)x, (int)y, radius, 0.4f))
                {
                    break;
                }
            }

            permeability = permeabilityMinimum + (float)rnd.NextDouble() * (permeabilityMaximum - permeabilityMinimum);
            return false;
        }
        catch (Exception ex)
        {
            Log("CloudPrefix error, falling back to vanilla: " + ex.Message);
            return true;
        }
    }

    // Angular scatter (radians, 1 sigma) of an arm at radius r. A constant angle makes the arm's physical
    // width grow with r (wide at the rim). Scaling by (rRef/r)^taper counters that: taper 0 = constant angle,
    // 1 = constant physical width, above 1 = arms narrow toward the rim. The configured width applies at rRef = half radius.
    private static float ArmSpread(float r, float galaxyRadius)
    {
        float scale = MathF.Pow(0.5f * galaxyRadius / Math.Max(r, 1f), SpiralSettings.ArmTaperValue);
        return SpiralSettings.SpreadRad * Math.Clamp(scale, 0.05f, 3f);
    }

    // r = rMin + (rMax - rMin) * U^p.  p = 0.5 gives uniform area density; larger p thins the outskirts.
    private static float RadialSample(Random rnd, float rMin, float rMax)
    {
        float t = MathF.Pow((float)rnd.NextDouble(), SpiralSettings.EdgeThinning);
        return rMin + (rMax * 0.97f - rMin) * t;
    }

    // Same arm rotation for every star of one galaxy, different between galaxies.
    private static float ArmPhase(int seed) => (float)new Random(seed ^ 0x5A17).NextDouble() * TwoPi;

    private static float Gaussian(Random rnd)
    {
        double u1 = 1.0 - rnd.NextDouble();
        double u2 = rnd.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(TwoPi * u2));
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the game.
        }
    }
}
