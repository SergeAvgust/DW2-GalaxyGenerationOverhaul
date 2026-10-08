namespace SpiralArms;

// The currently selected Spiral galaxy options. Held in memory only: they are chosen on the new-game screen
// and read while that galaxy (and its previews) are generated. Each option is an index into a table of presets;
// the defaults are the starting selection every time the game starts.
internal static class SpiralSettings
{
    public sealed record Option(string Label, float Value);

    public static readonly Option[] ArmCounts = { new("2", 2), new("3", 3), new("4", 4), new("5", 5), new("6", 6), new("7", 7) };

    // Pitch angle of the logarithmic spiral, in degrees: small = tightly wound.
    public static readonly Option[] Winding =
    {
        new("Very tight", 10), new("Tight", 18), new("Normal", 29), new("Loose", 38), new("Very loose", 45),
    };

    // Angular scatter of stars around the arm centre line, in degrees, measured at half the galaxy radius.
    public static readonly Option[] ArmWidth =
    {
        new("Very thin", 4), new("Thin", 7), new("Normal", 11), new("Wide", 16), new("Very wide", 22),
    };

    // 0 = constant angle (arms widen toward the rim), 1 = constant physical width, above 1 = arms narrow toward the rim.
    public static readonly Option[] ArmTaper =
    {
        new("None", 0f), new("Light", 0.7f), new("Normal", 1.4f), new("Strong", 1.7f), new("Very strong", 2f),
    };

    // Core radius as a fraction of the galaxy radius. The core is uniformly dense, and its density is the same for every size:
    // its share of all stars is proportional to its area (see CoreShare).
    public static readonly Option[] CoreSize =
    {
        new("Tiny", 0.10f), new("Small", 0.15f), new("Normal", 0.20f), new("Large", 0.27f), new("Huge", 0.35f),
    };

    // Exponent of the radial density: 0.5 = uniform area density, larger = thinner outskirts.
    public static readonly Option[] RimThinning =
    {
        new("None", 0.6f), new("Light", 0.9f), new("Normal", 1.3f), new("Strong", 1.6f), new("Very strong", 1.9f),
    };

    public static readonly Option[] NebulaPlacement = { new("Inside arms", 0), new("Between arms", 1) };

    // Fraction of stars scattered between the arms so the gaps are sparse rather than empty.
    public const float FieldShare = 0.1f;

    // Share of all stars per unit of core area (radius as a fraction of the galaxy radius, squared). 4.25 puts 17% of the
    // stars in a "Normal" core of radius 0.2; a core twice as wide holds four times the stars.
    private const float CoreStarDensity = 4.25f;

    // Selected index into each table above. These are the starting selections.
    public static int ArmCountIndex = 4;
    public static int WindingIndex = 2;
    public static int ArmWidthIndex = 2;
    public static int ArmTaperIndex = 2;
    public static int CoreSizeIndex = 2;
    public static int RimThinningIndex = 2;
    public static int NebulaPlacementIndex = 1;

    public static int ArmCount => (int)ArmCounts[ArmCountIndex].Value;
    public static float PitchRad => Winding[WindingIndex].Value * MathF.PI / 180f;
    public static float SpreadRad => ArmWidth[ArmWidthIndex].Value * MathF.PI / 180f;
    public static float ArmTaperValue => ArmTaper[ArmTaperIndex].Value;
    public static float CoreRadius => CoreSize[CoreSizeIndex].Value;
    public static float CoreShare => CoreStarDensity * CoreRadius * CoreRadius;
    public static float EdgeThinning => RimThinning[RimThinningIndex].Value;
    public static bool NebulasBetweenArms => NebulaPlacementIndex == 1;
}
