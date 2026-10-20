using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// Selected options for every shape except Spiral (see SpiralSettings). Same scheme: each option is an index into a table
// of presets, held in memory for the session. The defaults reproduce the vanilla look of the shape where one exists.
internal static class ShapeSettings
{
    // ---- Elliptical ----

    // Axis ratio of the galaxy outline. The long axis keeps the full galaxy radius, the short one shrinks.
    public static readonly Option[] Elongation =
    {
        new("Round", 1f), new("Slight", 1.3f), new("Oval", 1.7f), new("Elongated", 2.2f), new("Cigar", 3f),
    };

    // Radial profile exponent: r = R * U^c. 0.5 = uniform area density, larger = more concentrated toward the center.
    public static readonly Option[] Concentration =
    {
        new("None", 0.5f), new("Low", 0.6f), new("Normal", 0.7f), new("High", 0.9f), new("Very high", 1.2f),
    };

    // Bar: the core is stretched along the long axis, so the center becomes a spindle (an elongated core with pointed ends
    // that fades into the round disc) instead of a separate clump of extra stars. The option value is how many times longer
    // the very center becomes; BarCoreRadius is the radius of the stretched region as a fraction of the galaxy radius.
    public static readonly Option[] Bar =
    {
        new("None", 1f), new("Short", 1.5f), new("Medium", 2f), new("Long", 2.7f), new("Huge", 3.5f),
    };
    public static readonly float[] BarCoreRadius = { 0.2f, 0.18f, 0.24f, 0.30f, 0.36f };

    public static int ElongationIndex;
    public static int ConcentrationIndex = 2;
    public static int BarIndex;

    // ---- Classic ----
    // Vanilla draws 25% ring, 32% center blob, 43% uniform field. The three shares are normalised, so only their ratio matters.

    public static readonly Option[] ClassicRingShare =
    {
        new("None", 0f), new("Low", 0.12f), new("Normal", 0.25f), new("High", 0.40f), new("Very high", 0.55f),
    };
    public static readonly Option[] ClassicCenterShare =
    {
        new("None", 0f), new("Low", 0.15f), new("Normal", 0.32f), new("High", 0.45f), new("Very high", 0.60f),
    };
    public static readonly Option[] ClassicFieldShare =
    {
        new("Minimal", 0.03f), new("Low", 0.2f), new("Normal", 0.43f), new("High", 0.65f), new("Very high", 0.9f),
    };
    // Minimal replaces the old None: an extremely sparse field plus 2-4 winding bridges of stars from the center blob to the ring (see Placement.BridgePoints).
    public const int ClassicFieldMinimalIndex = 0;

    // Ring radius as a fraction of the galaxy radius (vanilla 0.93).
    public static readonly Option[] ClassicRingRadius =
    {
        new("Tiny", 0.5f), new("Small", 0.65f), new("Medium", 0.8f), new("Large", 0.93f), new("Huge", 0.98f),
    };

    // Nebula spiral arms of the Classic shape (vanilla: two arms, pitch about 12 degrees).
    public static readonly Option[] ClassicNebulaArms =
    {
        new("1", 1), new("2", 2), new("3", 3), new("4", 4), new("5", 5),
    };
    public static readonly Option[] ClassicNebulaWinding =
    {
        new("Very tight", 8), new("Tight", 12), new("Normal", 18), new("Loose", 29), new("Very loose", 40),
    };

    public static int ClassicRingShareIndex = 2;
    public static int ClassicCenterShareIndex = 2;
    public static int ClassicFieldShareIndex = 2;
    public static int ClassicRingRadiusIndex = 3;
    public static int ClassicNebulaArmsIndex = 1;
    public static int ClassicNebulaWindingIndex = 1;

    // ---- Ring shapes (Ring / Dual ring, with and without a dense center) ----

    // Share of ring stars clumped toward one side: density ~ 1 + e * cos(angle - clump angle).
    public static readonly Option[] RingEccentricity =
    {
        new("None", 0f), new("Low", 0.25f), new("Medium", 0.5f), new("High", 0.75f), new("Extreme", 0.95f),
    };

    // Axis ratio of the rings and the center blob. Capped by the galaxy bounds: the ring gets narrower more than longer.
    public static readonly Option[] RingEllipticity =
    {
        new("Round", 1f), new("Slight", 1.2f), new("Oval", 1.5f), new("Elongated", 1.9f), new("Very elongated", 2.5f),
    };

    // Ring profile presets: band width multiplier (RingProfileWidths) and the stretch of the inward side of the density
    // falloff (RingProfileSoftness; 1 = same as the outward side, as in vanilla, larger = fades toward the center).
    public static readonly Option[] RingProfile =
    {
        new("Thin", 0), new("Normal", 1), new("Wide", 2), new("Soft", 3), new("Fading", 4),
    };
    public static readonly float[] RingProfileWidths = { 0.6f, 1f, 1.4f, 1f, 1.3f };
    public static readonly float[] RingProfileSoftness = { 1f, 1f, 1f, 2.5f, 4f };

    // How far the inner ring / the center blob is moved toward the structure around it, as a share of the distance at which
    // they would touch it. The outer ring is never moved. The center blob moves relative to the inner ring when there is one.
    public static readonly Option[] RingOffset =
    {
        new("None", 0f), new("Low", 0.25f), new("Medium", 0.5f), new("High", 0.75f), new("Touching", 0.97f),
    };

    // Direction of those offsets in degrees; a negative value means "random, from the galaxy seed".
    public static readonly Option[] RingOffsetDirection =
    {
        new("Random", -1f), new("E", 0f), new("NE", 45f), new("N", 90f), new("NW", 135f), new("W", 180f), new("SW", 225f), new("S", 270f), new("SE", 315f),
    };

    // Turns the inner ring's ellipse and its eccentric clump relative to the outer ring's, in degrees.
    public static readonly Option[] RingInnerRotation =
    {
        new("Aligned", 0f), new("45°", 45f), new("90°", 90f), new("135°", 135f), new("Opposite", 180f),
    };

    // Radius of the inner ring of the dual-ring shapes (vanilla 0.5).
    public static readonly Option[] RingInnerRadius =
    {
        new("Tiny", 0.3f), new("Small", 0.4f), new("Normal", 0.5f), new("Large", 0.6f), new("Huge", 0.7f),
    };

    public static int RingEccentricityIndex;
    public static int RingEllipticityIndex;
    public static int RingProfileIndex = 1;
    public static int RingInnerOffsetIndex;
    public static int RingCenterOffsetIndex;
    public static int RingOffsetDirectionIndex;
    public static int RingInnerRotationIndex;
    public static int RingInnerRadiusIndex = 2;

    // ---- Webs (Irregular, and the filaments of the cluster shapes) ----

    // Irregular: share of stars on a web of filaments between seeded nodes. None = vanilla (a uniform disc).
    public static readonly Option[] IrregularWeb =
    {
        new("None", 0f), new("Light", 0.3f), new("Medium", 0.55f), new("Strong", 0.8f), new("Total", 0.97f),
    };
    public static readonly Option[] IrregularNodes =
    {
        new("Few", 10), new("Some", 16), new("Normal", 24), new("Many", 36), new("Lots", 52),
    };

    // Filament thickness (1 sigma) as a fraction of the galaxy radius: sharp-edged to diffuse.
    public static readonly Option[] FilamentSoftness =
    {
        new("Sharp", 0.012f), new("Firm", 0.02f), new("Normal", 0.035f), new("Soft", 0.055f), new("Diffuse", 0.085f),
    };

    public static int IrregularWebIndex;
    public static int IrregularNodesIndex = 2;
    public static int IrregularSoftnessIndex = 2;

    // ---- Cluster shapes ----

    // Multiplies the number of clusters the game generated.
    public static readonly Option[] ClusterAmount =
    {
        new("Very few", 0.5f), new("Few", 0.75f), new("Normal", 1f), new("Many", 1.5f), new("Very many", 2.2f),
    };

    // Multiplies each cluster's radius.
    public static readonly Option[] ClusterSize =
    {
        new("Tiny", 0.5f), new("Small", 0.75f), new("Normal", 1f), new("Large", 1.4f), new("Huge", 2f),
    };

    // Exponent applied to cluster portions (Varied only): 0 = all clusters equal, 1 = as generated, above 1 = a few dominate.
    public static readonly Option[] ClusterVariability =
    {
        new("None", 0f), new("Low", 0.5f), new("Normal", 1f), new("High", 1.6f), new("Extreme", 2.4f),
    };

    // Fraction of the cluster stars placed on filaments between clusters instead of in the clusters.
    public static readonly Option[] ClusterWeb =
    {
        new("None", 0f), new("Light", 0.2f), new("Medium", 0.4f), new("Strong", 0.6f), new("Dominant", 0.8f),
    };

    // Share of all stars scattered uniformly between the clusters (vanilla 0.4).
    public static readonly Option[] ClusterBackground =
    {
        new("Filled", 0.6f), new("Normal", 0.4f), new("Deep voids", 0.25f), new("Very deep", 0.12f), new("Empty", 0.03f),
    };

    public static int ClusterAmountIndex = 2;
    public static int ClusterSizeIndex = 2;
    public static int ClusterVariabilityIndex = 2;
    public static int ClusterWebIndex;
    public static int ClusterSoftnessIndex = 2;
    public static int ClusterBackgroundIndex = 1;

    public static float V(Option[] table, int index) => table[index].Value;
}
