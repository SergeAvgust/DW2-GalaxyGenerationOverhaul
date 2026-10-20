using DistantWorlds.Types;
using Option = SpiralArms.SpiralSettings.Option;

namespace SpiralArms;

// The option rows added to the galaxy settings screen. Rows of one Group are stacked together; rows of different groups
// overlap in the same screen area, and only the rows whose Shapes contain the selected shape are visible.
internal sealed class UiRow
{
    public required string Group;
    public required string Key;
    public required string Label;
    public required string Hint;
    public required Option[] Options;
    public required GalaxyShape[] Shapes;
    public required Func<int> Get;
    public required Action<int> Set;
    public readonly List<DistantWorlds.UI.DWControl> Controls = new();
    public DistantWorlds.UI.ToggleButtonGroup? Buttons;
}

internal static class UiRows
{
    private static readonly GalaxyShape[] Spiral = { GalaxyShape.Spiral };
    private static readonly GalaxyShape[] Elliptical = { GalaxyShape.Elliptical };
    private static readonly GalaxyShape[] Classic = { GalaxyShape.Classic };
    private static readonly GalaxyShape[] Clusters = { GalaxyShape.ClustersEven, GalaxyShape.ClustersVaried };
    private static readonly GalaxyShape[] VariedClusters = { GalaxyShape.ClustersVaried };
    private static readonly GalaxyShape[] Irregular = { GalaxyShape.Irregular };

    private static readonly UiRow[] BaseRows =
    {
        // ---- Spiral ----
        new()
        {
            Group = "Spiral", Key = "Arms", Label = "Spiral Arms", Options = SpiralSettings.ArmCounts, Shapes = Spiral,
            Hint = "Number of spiral arms. Two arms with a large core give a barred-spiral look; more arms give a busier, more tightly packed spiral.",
            Get = () => SpiralSettings.ArmCountIndex, Set = i => SpiralSettings.ArmCountIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Winding", Label = "Arm Winding", Options = SpiralSettings.Winding, Shapes = Spiral,
            Hint = "How tightly the arms wind around the core. Tight arms circle the galaxy several times; loose arms sweep outward more directly.",
            Get = () => SpiralSettings.WindingIndex, Set = i => SpiralSettings.WindingIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Width", Label = "Arm Width", Options = SpiralSettings.ArmWidth, Shapes = Spiral,
            Hint = "How far stars scatter from the centre line of each arm. Thin arms are sharply defined; wide arms blur into the gaps between them.",
            Get = () => SpiralSettings.ArmWidthIndex, Set = i => SpiralSettings.ArmWidthIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Taper", Label = "Arm Taper", Options = SpiralSettings.ArmTaper, Shapes = Spiral,
            Hint = "How the arms change in width toward the rim. None lets arms widen with distance from the core; stronger settings make them narrower and sharper at the edge of the galaxy.",
            Get = () => SpiralSettings.ArmTaperIndex, Set = i => SpiralSettings.ArmTaperIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Core", Label = "Core Size", Options = SpiralSettings.CoreSize, Shapes = Spiral,
            Hint = "Size of the dense central blob. Its star density stays the same, so a larger core holds proportionally more of the galaxy's stars.",
            Get = () => SpiralSettings.CoreSizeIndex, Set = i => SpiralSettings.CoreSizeIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Rim", Label = "Rim Thinning", Options = SpiralSettings.RimThinning, Shapes = Spiral,
            Hint = "How quickly star density falls off toward the rim. None keeps the density even all the way out; stronger settings leave the outskirts sparse.",
            Get = () => SpiralSettings.RimThinningIndex, Set = i => SpiralSettings.RimThinningIndex = i,
        },
        new()
        {
            Group = "Spiral", Key = "Nebulae", Label = "Nebula Placement", Options = SpiralSettings.NebulaPlacement, Shapes = Spiral,
            Hint = "Where nebula clouds appear: along the arms, among the stars, or in the gaps between the arms. The nebula density setting above still controls how many there are.",
            Get = () => SpiralSettings.NebulaPlacementIndex, Set = i => SpiralSettings.NebulaPlacementIndex = i,
        },

        // ---- Elliptical ----
        new()
        {
            Group = "Elliptical", Key = "EllElongation", Label = "Elongation", Options = ShapeSettings.Elongation, Shapes = Elliptical,
            Hint = "How stretched the galaxy is. Round is a plain disc; higher settings squash it along one axis into an oval or a cigar, in a direction picked from the galaxy seed.",
            Get = () => ShapeSettings.ElongationIndex, Set = i => ShapeSettings.ElongationIndex = i,
        },
        new()
        {
            Group = "Elliptical", Key = "EllConcentration", Label = "Concentration", Options = ShapeSettings.Concentration, Shapes = Elliptical,
            Hint = "How strongly stars pile up toward the center. None spreads them evenly over the whole disc; high settings make a bright dense heart with a faint halo.",
            Get = () => ShapeSettings.ConcentrationIndex, Set = i => ShapeSettings.ConcentrationIndex = i,
        },
        new()
        {
            Group = "Elliptical", Key = "EllBar", Label = "Bar", Options = ShapeSettings.Bar, Shapes = Elliptical,
            Hint = "Stretches the dense core along the long axis into an elongated spindle with pointed ends, fading smoothly into the disc. It spreads the core out; it does not add stars.",
            Get = () => ShapeSettings.BarIndex, Set = i => ShapeSettings.BarIndex = i,
        },

        // ---- Classic ----
        new()
        {
            Group = "Classic", Key = "ClsRing", Label = "Ring Stars", Options = ShapeSettings.ClassicRingShare, Shapes = Classic,
            Hint = "How many stars sit in the outer ring, relative to the central blob and the scattered field. The three shares are balanced against each other.",
            Get = () => ShapeSettings.ClassicRingShareIndex, Set = i => ShapeSettings.ClassicRingShareIndex = i,
        },
        new()
        {
            Group = "Classic", Key = "ClsCenter", Label = "Center Stars", Options = ShapeSettings.ClassicCenterShare, Shapes = Classic,
            Hint = "How many stars sit in the dense central blob, relative to the ring and the scattered field.",
            Get = () => ShapeSettings.ClassicCenterShareIndex, Set = i => ShapeSettings.ClassicCenterShareIndex = i,
        },
        new()
        {
            Group = "Classic", Key = "ClsField", Label = "Field Stars", Options = ShapeSettings.ClassicFieldShare, Shapes = Classic,
            Hint = "How many stars are scattered evenly across the whole galaxy, relative to the ring and the central blob. Minimal is an extremely sparse field with large voids, plus a few winding star bridges linking the blob to the ring.",
            Get = () => ShapeSettings.ClassicFieldShareIndex, Set = i => ShapeSettings.ClassicFieldShareIndex = i,
        },
        new()
        {
            Group = "Classic", Key = "ClsRingRadius", Label = "Ring Radius", Options = ShapeSettings.ClassicRingRadius, Shapes = Classic,
            Hint = "Distance of the ring from the center, as a fraction of the galaxy radius. Large is the standard ring near the rim.",
            Get = () => ShapeSettings.ClassicRingRadiusIndex, Set = i => ShapeSettings.ClassicRingRadiusIndex = i,
        },
        new()
        {
            Group = "Classic", Key = "ClsNebArms", Label = "Nebula Arms", Options = ShapeSettings.ClassicNebulaArms, Shapes = Classic,
            Hint = "Number of spiral arms of nebula clouds winding through the galaxy. The standard Classic galaxy has two; more than three or four starts to look crowded.",
            Get = () => ShapeSettings.ClassicNebulaArmsIndex, Set = i => ShapeSettings.ClassicNebulaArmsIndex = i,
        },
        new()
        {
            Group = "Classic", Key = "ClsNebWinding", Label = "Nebula Winding", Options = ShapeSettings.ClassicNebulaWinding, Shapes = Classic,
            Hint = "How tightly the nebula arms wind. Tight is the standard Classic look; loose arms sweep outward more directly.",
            Get = () => ShapeSettings.ClassicNebulaWindingIndex, Set = i => ShapeSettings.ClassicNebulaWindingIndex = i,
        },

        // ---- Clusters ----
        new()
        {
            Group = "Clusters", Key = "ClAmount", Label = "Cluster Amount", Options = ShapeSettings.ClusterAmount, Shapes = Clusters,
            Hint = "Number of star clusters. The standard galaxy decides a base number from the star count; this scales it up or down.",
            Get = () => ShapeSettings.ClusterAmountIndex, Set = i => ShapeSettings.ClusterAmountIndex = i,
        },
        new()
        {
            Group = "Clusters", Key = "ClSize", Label = "Cluster Size", Options = ShapeSettings.ClusterSize, Shapes = Clusters,
            Hint = "How wide each cluster is. Large clusters merge into each other; tiny ones stay as separate dense knots.",
            Get = () => ShapeSettings.ClusterSizeIndex, Set = i => ShapeSettings.ClusterSizeIndex = i,
        },
        new()
        {
            Group = "Clusters", Key = "ClWeb", Label = "Web Strength", Options = ShapeSettings.ClusterWeb, Shapes = Clusters,
            Hint = "Connects the clusters with filaments of stars, like a cosmic web. This is the share of cluster stars that sit on the filaments instead of in the clusters.",
            Get = () => ShapeSettings.ClusterWebIndex, Set = i => ShapeSettings.ClusterWebIndex = i,
        },
        new()
        {
            Group = "Clusters", Key = "ClSoftness", Label = "Filament Width", Options = ShapeSettings.FilamentSoftness, Shapes = Clusters,
            Hint = "How thick the filaments are. Sharp filaments are thin threads between clusters; diffuse ones are broad, fuzzy bridges. Only matters when the web strength is above None.",
            Get = () => ShapeSettings.ClusterSoftnessIndex, Set = i => ShapeSettings.ClusterSoftnessIndex = i,
        },
        new()
        {
            Group = "Clusters", Key = "ClBackground", Label = "Voids", Options = ShapeSettings.ClusterBackground, Shapes = Clusters,
            Hint = "How empty the space between the clusters is. Filled scatters many stars across the gaps; deep voids leave them nearly empty.",
            Get = () => ShapeSettings.ClusterBackgroundIndex, Set = i => ShapeSettings.ClusterBackgroundIndex = i,
        },
        // Last, so the Even shape (which hides it) leaves no gap.
        new()
        {
            Group = "Clusters", Key = "ClVariability", Label = "Variability", Options = ShapeSettings.ClusterVariability, Shapes = VariedClusters,
            Hint = "How different the clusters are from each other. None makes them all the same size and weight; high settings let a few huge clusters dominate while the rest stay small.",
            Get = () => ShapeSettings.ClusterVariabilityIndex, Set = i => ShapeSettings.ClusterVariabilityIndex = i,
        },

        // ---- Irregular ----
        new()
        {
            Group = "Irregular", Key = "IrrWeb", Label = "Web Strength", Options = ShapeSettings.IrregularWeb, Shapes = Irregular,
            Hint = "Turns the featureless Irregular disc into a web of star filaments and knots with voids between. This is the share of stars on the web; None keeps the standard even scatter.",
            Get = () => ShapeSettings.IrregularWebIndex, Set = i => ShapeSettings.IrregularWebIndex = i,
        },
        new()
        {
            Group = "Irregular", Key = "IrrNodes", Label = "Web Nodes", Options = ShapeSettings.IrregularNodes, Shapes = Irregular,
            Hint = "Number of knots the filaments connect. Few knots make big open voids; many make a finer mesh. Only matters when the web strength is above None.",
            Get = () => ShapeSettings.IrregularNodesIndex, Set = i => ShapeSettings.IrregularNodesIndex = i,
        },
        new()
        {
            Group = "Irregular", Key = "IrrSoftness", Label = "Filament Width", Options = ShapeSettings.FilamentSoftness, Shapes = Irregular,
            Hint = "How sharp the edges of the voids are. Sharp filaments are thin threads with hard-edged voids; diffuse ones fade gradually into the voids.",
            Get = () => ShapeSettings.IrregularSoftnessIndex, Set = i => ShapeSettings.IrregularSoftnessIndex = i,
        },
    };

    // Rings have different structures per shape (outer ring only, plus a center blob, plus an inner ring, or both), so
    // each ring shape gets its own stack of rows. They all edit the same settings.
    private static IEnumerable<UiRow> RingRows(string group, GalaxyShape shape, bool innerRing, bool center)
    {
        GalaxyShape[] shapes = { shape };
        UiRow Row(string key, string label, string hint, SpiralSettings.Option[] options, Func<int> get, Action<int> set) => new()
        {
            Group = group, Key = key + group, Label = label, Hint = hint, Options = options, Shapes = shapes, Get = get, Set = set,
        };

        yield return Row("RingEcc", "Eccentricity",
            "How much of the ring's material is clumped on one side. None is an even ring; extreme leaves the opposite side almost empty. The clumped side is picked from the galaxy seed.",
            ShapeSettings.RingEccentricity, () => ShapeSettings.RingEccentricityIndex, i => ShapeSettings.RingEccentricityIndex = i);
        yield return Row("RingEllipticity", "Ellipticity",
            "Stretches the rings (and the central blob) into ovals along a diagonal of the galaxy, so the ends can reach into its corners.",
            ShapeSettings.RingEllipticity, () => ShapeSettings.RingEllipticityIndex, i => ShapeSettings.RingEllipticityIndex = i);
        yield return Row("RingProfile", "Ring Profile",
            "Shape of the ring band. Thin and wide change its width; soft and fading let stars trail off toward the center on the inner side.",
            ShapeSettings.RingProfile, () => ShapeSettings.RingProfileIndex, i => ShapeSettings.RingProfileIndex = i);
        if (innerRing)
        {
            yield return Row("RingInnerRadius", "Inner Ring Radius",
                "Radius of the inner ring, as a fraction of the galaxy radius. The outer ring always stays near the rim.",
                ShapeSettings.RingInnerRadius, () => ShapeSettings.RingInnerRadiusIndex, i => ShapeSettings.RingInnerRadiusIndex = i);
            yield return Row("RingInnerOffset", "Inner Ring Offset",
                "Moves the inner ring toward the outer ring. Touching moves it until the two rings meet. The outer ring itself is never moved.",
                ShapeSettings.RingOffset, () => ShapeSettings.RingInnerOffsetIndex, i => ShapeSettings.RingInnerOffsetIndex = i);
        }
        if (center)
        {
            yield return Row("RingCenterOffset", "Center Offset",
                innerRing
                    ? "Moves the central blob toward the inner ring. Touching moves it until it meets the ring. It moves along with the inner ring if that is offset."
                    : "Moves the central blob toward the ring. Touching moves it until it meets the ring.",
                ShapeSettings.RingOffset, () => ShapeSettings.RingCenterOffsetIndex, i => ShapeSettings.RingCenterOffsetIndex = i);
        }
        if (innerRing || center)
        {
            yield return Row("RingOffsetDir", "Offset Direction",
                "Compass direction of the offsets of the inner ring and the central blob, which share it so they can all touch at one point. Random picks one from the galaxy seed.",
                ShapeSettings.RingOffsetDirection, () => ShapeSettings.RingOffsetDirectionIndex, i => ShapeSettings.RingOffsetDirectionIndex = i);
        }
        if (innerRing)
        {
            yield return Row("RingInnerRotation", "Inner Ring Rotation",
                "Turns the inner ring's oval shape and its eccentric clump relative to the outer ring. Only matters when the ellipticity or eccentricity is set.",
                ShapeSettings.RingInnerRotation, () => ShapeSettings.RingInnerRotationIndex, i => ShapeSettings.RingInnerRotationIndex = i);
        }
    }

    // Rows for every shape, added to the colonization section of the dialog next to the vanilla Colony Prevalence row.
    public static readonly UiRow[] Colonization =
    {
        new()
        {
            Group = "Colonization", Key = "PlanetQuality", Label = "Planet Quality", Options = Habitability.Quality, Shapes = Array.Empty<GalaxyShape>(),
            Hint = "How good the planets are, as opposed to how many there are (that is Habitable Planets above). Higher quality gives habitable worlds larger, "
                + "richer populations; lower quality leaves most of them marginal.",
            Get = () => Habitability.QualityIndex, Set = i => Habitability.QualityIndex = i,
        },
        new()
        {
            Group = "Colonization", Key = "PlacementMethod", Label = "Empire Placement", Options = RegionPlacement.Methods, Shapes = Array.Empty<GalaxyShape>(),
            Hint = "How the empires are placed. Standard is the game's own search: a random point, then the nearest suitable planet, which can leave "
                + "big areas empty and put an empire on a lonely star. Balanced regions splits the populated part of the galaxy into one region per "
                + "empire, each holding about the same number of stars, and places every empire in its own region. Your own start location setting "
                + "(deep core, core, rim or a spot on the map) picks which region is yours.",
            Get = () => RegionPlacement.MethodIndex, Set = i => RegionPlacement.MethodIndex = i,
        },
    };

    // The starting-situation step (home system, expansion, tech level, ...): rows about the player's empire.
    public static readonly UiRow[] StartingSituation =
    {
        new()
        {
            Group = "StartingSituation", Key = "GalaxyKnowledge", Label = "Galaxy Knowledge", Options = Exploration.Modes, Shapes = Array.Empty<GalaxyShape>(),
            Hint = "How much of the galaxy your empire knows at the start. Standard is the usual handful of nearby systems. Everything explored "
                + "marks every system as explored. Everything surveyed also surveys every planet, moon and asteroid to the maximum, so all resources, bonuses and facilities "
                + "are known without sending survey ships (ruins still have to be investigated).",
            Get = () => Exploration.ModeIndex, Set = i => Exploration.ModeIndex = i,
        },
    };

    // The same setting on the pirate empire step (own key: every row owns its controls, so the two toggles are kept in step by SpiralUi).
    public static readonly UiRow[] PirateStartingSituation =
    {
        new()
        {
            Group = "PirateStartingSituation", Key = "GalaxyKnowledgePirate", Label = StartingSituation[0].Label, Options = Exploration.Modes, Shapes = Array.Empty<GalaxyShape>(),
            Hint = StartingSituation[0].Hint,
            Get = () => Exploration.ModeIndex, Set = i => Exploration.ModeIndex = i,
        },
    };

    // Rows added to a section of the new-game dialog for every galaxy shape, keyed by the dialog's section index:
    // 3 = colonization, 6 = your starting situation, 10 = pirate empire.
    public static readonly Dictionary<int, UiRow[]> CommonBySection = new()
    {
        [3] = Colonization,
        [6] = StartingSituation,
        [10] = PirateStartingSituation,
    };

    public static readonly UiRow[] All = BaseRows
        .Concat(RingRows("RingNo", GalaxyShape.RingNoCenter, innerRing: false, center: false))
        .Concat(RingRows("RingDense", GalaxyShape.RingDenseCenter, innerRing: false, center: true))
        .Concat(RingRows("DualNo", GalaxyShape.DualRingNoCenter, innerRing: true, center: false))
        .Concat(RingRows("DualDense", GalaxyShape.DualRingDenseCenter, innerRing: true, center: true))
        .ToArray();
}
