using System.Reflection;
using DistantWorlds.Types;
using Stride.Core.Mathematics;
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
            // Classic, rings, Irregular and clusters: every vanilla placement helper is redirected to Placement.Sample.
            foreach (string helper in new[] { "DeterminePointRing", "DeterminePointCenter", "DeterminePointRandomCircular", "DeterminePointCluster" })
            {
                PatchIfFound(harmony, helper, prefix: nameof(HelperPrefix));
            }
            PatchIfFound(harmony, "GenerateStarClusters", postfix: nameof(ClustersPostfix));
            // Nebulae placed with the rings and the center blob follow the rings' ellipse and offset.
            PatchIfFound(harmony, "DetermineCloudSettingsRing", postfix: nameof(CloudRingCenterPostfix));
            PatchIfFound(harmony, "DetermineCloudSettingsCenter", postfix: nameof(CloudRingCenterPostfix));

            Log($"PATCH OK. arms={SpiralSettings.ArmCount} pitch={SpiralSettings.PitchRad:F3}rad spread={SpiralSettings.SpreadRad:F3}rad core={SpiralSettings.CoreRadius} coreShare={SpiralSettings.CoreShare} fieldShare={SpiralSettings.FieldShare} thinning={SpiralSettings.EdgeThinning} taper={SpiralSettings.ArmTaperValue}");

            Habitability.Install(harmony);
            Exploration.Install(harmony);
            HomeSites.Install(harmony);
            Regions.Install(harmony);
            RegionPlacement.Install(harmony);
            PlacementStep.Install(harmony);
            SpiralUi.Install(harmony);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION during mod init: " + ex);
        }
    }

    private static void PatchIfFound(Harmony harmony, string method, string? prefix = null, string? postfix = null)
    {
        MethodInfo? target = AccessTools.Method(typeof(Galaxy), method);
        if (target == null)
        {
            Log($"WARN: Galaxy.{method} not found.");
            return;
        }
        HarmonyMethod? pre = prefix == null ? null : new HarmonyMethod(typeof(Bootstrap).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
        HarmonyMethod? post = postfix == null ? null : new HarmonyMethod(typeof(Bootstrap).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic));
        harmony.Patch(target, prefix: pre, postfix: post);
    }

    // Shared by DeterminePointRing / Center / RandomCircular / Cluster: they all take (rnd, settings, ..., out x, out y).
    // Shapes not handled by Placement (Spiral, Elliptical, and anything unknown) run the original.
    private static bool HelperPrefix(Galaxy __instance, Random rnd, GameStartSettings settings, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (!Placement.Handles(settings.Shape))
        {
            return true;
        }

        try
        {
            Placement.Sample(__instance, settings, rnd, out x, out y);
            return false;
        }
        catch (Exception ex)
        {
            Log("Placement.Sample error, falling back to vanilla: " + ex.Message);
            return true;
        }
    }

    private static void ClustersPostfix(Galaxy __instance, GameStartSettings settings, List<Point> starClusterLocations, List<float> starClusterPortions)
    {
        try
        {
            Placement.AdjustClusters(__instance, settings, starClusterLocations, starClusterPortions);
        }
        catch (Exception ex)
        {
            Log("AdjustClusters error: " + ex.Message);
        }
    }

    private static void CloudRingCenterPostfix(GameStartSettings settings, ref float x, ref float y)
    {
        if (!Placement.IsRingShape(settings.Shape))
        {
            return;
        }
        try
        {
            float radius = settings.Dimensions * 0.5f;
            float vx = x - radius;
            float vy = y - radius;
            Placement.OuterEllipse(settings, ref vx, ref vy);
            x = radius + vx;
            y = radius + vy;
        }
        catch (Exception ex)
        {
            Log("CloudRingCenterPostfix error: " + ex.Message);
        }
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    // Returning false skips the original; returning true leaves other shapes alone.
    private static bool PlacePrefix(Random rnd, GameStartSettings settings, float centerRadius, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (settings.Shape == GalaxyShape.Elliptical)
        {
            // Elliptical shares this helper with Spiral in vanilla; place it with elongation / concentration / bar.
            try
            {
                Placement.SampleElliptical(settings, rnd, centerRadius, out x, out y);
                return false;
            }
            catch (Exception ex)
            {
                Log("SampleElliptical error, falling back to vanilla: " + ex.Message);
                return true;
            }
        }

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
        if (settings.Shape == GalaxyShape.Classic)
        {
            return ClassicCloud(__instance, rnd, settings, cloudSizeMinimum, cloudSizeMaximum, permeabilityMinimum, permeabilityMaximum, out x, out y, out radius, out permeability);
        }

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

    // Classic shares the nebula arm method with Spiral. Vanilla walks two fixed arms (a logarithmic spiral from 0.05R out to R,
    // pitch about 12 degrees, cloud size growing with the square root of the walk progress); this keeps that shape but
    // takes the number of arms and the winding from the Classic settings. Vanilla makes two sets of calls whose armRotation
    // differs by pi (one per arm); using that value as the phase would give two sets of N arms (6 arms for N = 3), so the
    // phase comes from the galaxy seed instead and is the same for every call.
    private static bool ClassicCloud(Galaxy galaxy, Random rnd, GameStartSettings settings, float cloudSizeMinimum, float cloudSizeMaximum,
        float permeabilityMinimum, float permeabilityMaximum, out float x, out float y, out float radius, out float permeability)
    {
        x = 0f;
        y = 0f;
        radius = 1f;
        permeability = 1f;
        try
        {
            const float Range = 2.96f; // ln(R / r_min): vanilla's 4 * 2.2 coils * ln(1.4)
            float galaxyRadius = settings.Dimensions * 0.5f;
            int arms = (int)ShapeSettings.V(ShapeSettings.ClassicNebulaArms, ShapeSettings.ClassicNebulaArmsIndex);
            float phase = ArmPhase(settings.RandomSeed);
            float pitch = ShapeSettings.V(ShapeSettings.ClassicNebulaWinding, ShapeSettings.ClassicNebulaWindingIndex) * MathF.PI / 180f;

            for (int attempt = 0; attempt < 100; attempt++)
            {
                float progress = 0.01f + 0.99f * MathF.Sqrt(1f - (float)rnd.NextDouble());
                float logRadius = Range * (progress - 1f);
                float r = galaxyRadius * MathF.Exp(logRadius);
                int arm = rnd.Next(arms);
                float theta = phase + arm * TwoPi / arms + WindSign * logRadius / MathF.Tan(pitch) + Gaussian(rnd) * 0.03f;

                x = galaxyRadius + r * MathF.Cos(theta);
                y = galaxyRadius + r * MathF.Sin(theta);
                radius = (cloudSizeMinimum + (float)rnd.NextDouble() * (cloudSizeMaximum - cloudSizeMinimum)) * MathF.Sqrt(progress);

                if (!galaxy.NebulaClouds.CheckAnyWithinRange((int)x, (int)y, radius, 0.4f))
                {
                    break;
                }
            }

            permeability = permeabilityMinimum + (float)rnd.NextDouble() * (permeabilityMaximum - permeabilityMinimum);
            return false;
        }
        catch (Exception ex)
        {
            Log("ClassicCloud error, falling back to vanilla: " + ex.Message);
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
