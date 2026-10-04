using System.Reflection;
using System.Text.Json;
using DistantWorlds.Types;
using HarmonyLib;

namespace SpiralArms;

// Entry point invoked by the launcher's loader (see DW2-Mod-Launcher/docs/DLL Injection.md).
//
// Vanilla places every star of a Spiral (and Elliptical) galaxy with Galaxy.DeterminePointCenterEased:
//   angle = U*2pi, r = R*U^0.7, point = (R,R) + r*(cos, sin)      (R = Dimensions/2)
// i.e. a plain disc with no arms. This prefix replaces that for GalaxyShape.Spiral only; Elliptical shares
// the same method and is passed through to the original untouched.
public static class Bootstrap
{
    private const float TwoPi = MathF.PI * 2f;

    private static bool _started;
    private static string _logPath = string.Empty;

    private static int _armCount = 5;
    private static float _pitchRad = 18f * MathF.PI / 180f;
    private static float _spreadRad = 11f * MathF.PI / 180f;
    private static float _coreRadius = 0.2f;
    private static float _coreShare = 0.15f;
    private static float _fieldShare = 0.1f;
    private static float _edgeThinning = 0.85f;
    private static float _armTaper = 1.3f;
    // Vanilla-style winding mirrored once and for all (no setting).
    private const float WindSign = -1f;
    private static bool _nebulasBetweenArms;

    public static void InitWithOptions(string json)
    {
        _logPath = Path.Combine(Path.GetTempPath(), "SpiralArms.log");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    if (p.Name.Equals("NebulaPlacement", StringComparison.OrdinalIgnoreCase))
                    {
                        _nebulasBetweenArms = (p.Value.GetString() ?? "").Replace(" ", "").Equals("BetweenArms", StringComparison.OrdinalIgnoreCase);
                    }
                    continue;
                }
                if (p.Value.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                float v = p.Value.GetSingle();
                switch (p.Name.ToLowerInvariant())
                {
                    case "armcount": _armCount = Math.Clamp((int)MathF.Round(v), 2, 8); break;
                    case "armpitchdegrees": _pitchRad = Math.Clamp(v, 5f, 45f) * MathF.PI / 180f; break;
                    case "armspreaddegrees": _spreadRad = Math.Clamp(v, 2f, 30f) * MathF.PI / 180f; break;
                    case "coreradius": _coreRadius = Math.Clamp(v, 0.05f, 0.5f); break;
                    case "coreshare": _coreShare = Math.Clamp(v, 0f, 0.6f); break;
                    case "fieldshare": _fieldShare = Math.Clamp(v, 0f, 0.5f); break;
                    case "edgethinning": _edgeThinning = Math.Clamp(v, 0.5f, 1.5f); break;
                    case "armtaper": _armTaper = Math.Clamp(v, 0f, 2f); break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("Failed to parse settings, using defaults: " + ex.Message);
        }
        Init();
    }

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
            Log($"PATCH OK. arms={_armCount} pitch={_pitchRad:F3}rad spread={_spreadRad:F3}rad core={_coreRadius} coreShare={_coreShare} fieldShare={_fieldShare} thinning={_edgeThinning} taper={_armTaper}");
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
            float coreR = _coreRadius * radius;
            float pick = (float)rnd.NextDouble();
            float r;
            float theta;

            if (pick < _coreShare)
            {
                // Central blob: uniform density inside the core disc.
                r = coreR * MathF.Sqrt((float)rnd.NextDouble());
                theta = (float)rnd.NextDouble() * TwoPi;
            }
            else if (pick < _coreShare + _fieldShare)
            {
                // Sparse inter-arm field: same radial falloff as the arms, any angle.
                r = RadialSample(rnd, coreR, radius);
                theta = (float)rnd.NextDouble() * TwoPi;
            }
            else
            {
                // Arm star: logarithmic spiral  theta = phase + arm*2pi/N + ln(r/r0)/tan(pitch), then scatter.
                r = RadialSample(rnd, coreR, radius);
                int arm = rnd.Next(_armCount);
                float phase = ArmPhase(settings.RandomSeed);
                float wind = WindSign * MathF.Log(r / coreR) / MathF.Tan(_pitchRad);
                theta = phase + arm * TwoPi / _armCount + wind + Gaussian(rnd) * ArmSpread(r, radius);
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
            float coreR = _coreRadius * galaxyRadius;
            // Inside the arms: on the arm centre line. Between: halfway to the neighbouring arm.
            float phase = ArmPhase(settings.RandomSeed) + (_nebulasBetweenArms ? MathF.PI / _armCount : 0f);

            for (int attempt = 0; attempt < 100; attempt++)
            {
                float r = RadialSample(rnd, coreR, galaxyRadius);
                int arm = rnd.Next(_armCount);
                float theta = phase + arm * TwoPi / _armCount + WindSign * MathF.Log(r / coreR) / MathF.Tan(_pitchRad) + Gaussian(rnd) * ArmSpread(r, galaxyRadius);

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
        float scale = MathF.Pow(0.5f * galaxyRadius / Math.Max(r, 1f), _armTaper);
        return _spreadRad * Math.Clamp(scale, 0.05f, 3f);
    }

    // r = rMin + (rMax - rMin) * U^p.  p = 0.5 gives uniform area density; larger p thins the outskirts.
    private static float RadialSample(Random rnd, float rMin, float rMax)
    {
        float t = MathF.Pow((float)rnd.NextDouble(), _edgeThinning);
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

    private static void Log(string message)
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
