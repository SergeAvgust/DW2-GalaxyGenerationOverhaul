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

    public static void InitWithOptions(string json)
    {
        _logPath = Path.Combine(Path.GetTempPath(), "SpiralArms.log");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
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
            Log($"PATCH OK. arms={_armCount} pitch={_pitchRad:F3}rad spread={_spreadRad:F3}rad core={_coreRadius} coreShare={_coreShare} fieldShare={_fieldShare} thinning={_edgeThinning}");
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
                float wind = MathF.Log(r / coreR) / MathF.Tan(_pitchRad);
                theta = phase + arm * TwoPi / _armCount + wind + Gaussian(rnd) * _spreadRad;
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
