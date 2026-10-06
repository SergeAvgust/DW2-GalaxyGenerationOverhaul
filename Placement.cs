using DistantWorlds.Types;
using Stride.Core.Mathematics;

namespace SpiralArms;

// Star placement for every shape except Spiral (see Bootstrap.PlacePrefix for that one).
//
// Vanilla's GenerateStarSystems draws one random number per candidate star and, per shape, picks one of the helpers
// DeterminePointRing / DeterminePointCenter / DeterminePointRandomCircular / DeterminePointCluster with fixed
// probabilities, then rejects candidates that are too close to existing systems. For the shapes handled here, the
// patched helpers all end up in Sample(), which ignores which helper was called and draws a component itself from the
// target mix. That makes the mix adjustable; the defaults reproduce the vanilla mixes:
//   Classic          25% ring (0.93R), 32% center blob (0.462R), 43% disc
//   Ring no center   60% ring, 40% disc
//   Ring dense ctr   40% ring, 20% center blob (0.33R), 40% disc
//   Dual ring no ctr 38% outer ring, 27% inner ring (0.5R), 35% disc
//   Dual ring dense  27% outer ring, 23% inner ring, 20% center blob, 30% disc
//   Irregular        100% disc
//   Clusters         60% clusters, 40% disc
// R is half of GameStartSettings.Dimensions; the galaxy center is (R, R).
internal static class Placement
{
    private const float TwoPi = MathF.PI * 2f;
    private const float DiscMargin = 1_875_000f;
    private const float EdgeMargin = 2_500_000f;

    public static bool IsRingShape(GalaxyShape shape) =>
        shape is GalaxyShape.RingNoCenter or GalaxyShape.RingDenseCenter or GalaxyShape.DualRingNoCenter or GalaxyShape.DualRingDenseCenter;

    public static bool IsClusterShape(GalaxyShape shape) => shape is GalaxyShape.ClustersEven or GalaxyShape.ClustersVaried;

    // Shapes whose helper calls are redirected to Sample().
    public static bool Handles(GalaxyShape shape) =>
        shape == GalaxyShape.Classic || IsRingShape(shape) || shape == GalaxyShape.Irregular || IsClusterShape(shape);

    // ---- per-galaxy random parameters (orientation, clump angle, offset direction) derived from the seed ----

    private sealed class SeedParams
    {
        public int Seed;
        public float Orientation;
        public float ClumpAngle;
        public float RandomOffsetAngle;
    }

    private static SeedParams? _seedParams;

    private static SeedParams ParamsFor(int seed)
    {
        SeedParams? p = _seedParams;
        if (p != null && p.Seed == seed)
        {
            return p;
        }
        var rnd = new Random(seed ^ 0x3C6E);
        p = new SeedParams
        {
            Seed = seed,
            // Stretched shapes lie along a diagonal of the square galaxy (the corners lie beyond the circle).
            Orientation = (rnd.Next(0, 2) == 0 ? 45f : 135f) * MathF.PI / 180f,
            ClumpAngle = (float)rnd.NextDouble() * TwoPi,
            RandomOffsetAngle = (float)rnd.NextDouble() * TwoPi,
        };
        _seedParams = p;
        return p;
    }

    // ---- entry point for the patched helpers ----

    public static void Sample(Galaxy galaxy, GameStartSettings settings, Random rnd, out float x, out float y)
    {
        float radius = settings.Dimensions * 0.5f;
        switch (settings.Shape)
        {
            case GalaxyShape.Classic:
                SampleClassic(settings, rnd, radius, out x, out y);
                break;
            case GalaxyShape.Irregular:
                SampleIrregular(settings, rnd, radius, out x, out y);
                break;
            case GalaxyShape.ClustersEven:
            case GalaxyShape.ClustersVaried:
                SampleClusters(galaxy, settings, rnd, radius, out x, out y);
                break;
            default:
                SampleRings(settings, rnd, radius, out x, out y);
                break;
        }
    }

    // ---- basic components, as offsets from the galaxy center ----

    private static void Disc(Random rnd, float galaxyRadius, out float vx, out float vy)
    {
        float r = (galaxyRadius - DiscMargin) * MathF.Sqrt((float)rnd.NextDouble());
        float theta = (float)rnd.NextDouble() * TwoPi;
        vx = r * MathF.Cos(theta);
        vy = r * MathF.Sin(theta);
    }

    // Vanilla DeterminePointCenter: r = radius * U, i.e. denser toward the middle.
    private static void CenterBlob(Random rnd, float radius, out float vx, out float vy)
    {
        float r = radius * (float)rnd.NextDouble();
        float theta = (float)rnd.NextDouble() * TwoPi;
        vx = r * MathF.Cos(theta);
        vy = r * MathF.Sin(theta);
    }

    // Vanilla DeterminePointRing: the radial offset from the ring radius is width/2 * (1 - sqrt(U)), on a random side.
    // innerSoftness stretches the inward side; eccentricity clumps stars toward the clump angle.
    private static void RingBand(Random rnd, float ringRadius, float width, float innerSoftness, float eccentricity, float clumpAngle, out float vx, out float vy)
    {
        float theta = (float)rnd.NextDouble() * TwoPi;
        if (eccentricity > 0f)
        {
            for (int i = 0; i < 40 && rnd.NextDouble() * (1f + eccentricity) > 1f + eccentricity * MathF.Cos(theta - clumpAngle); i++)
            {
                theta = (float)rnd.NextDouble() * TwoPi;
            }
        }

        float offset = width * (1f - MathF.Sqrt((float)rnd.NextDouble())) * 0.5f;
        if (rnd.Next(0, 2) == 1)
        {
            offset = -offset * innerSoftness;
        }
        float r = Math.Abs(ringRadius + offset);
        vx = r * MathF.Cos(theta);
        vy = r * MathF.Sin(theta);
    }

    private static bool InBounds(float x, float y, float galaxyRadius) =>
        x >= EdgeMargin && y >= EdgeMargin && x <= 2f * galaxyRadius - EdgeMargin && y <= 2f * galaxyRadius - EdgeMargin;

    private static float Gaussian(Random rnd)
    {
        double u1 = 1.0 - rnd.NextDouble();
        double u2 = rnd.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(TwoPi * u2));
    }

    // ---- Classic ----

    private static void SampleClassic(GameStartSettings settings, Random rnd, float galaxyRadius, out float x, out float y)
    {
        float ring = ShapeSettings.V(ShapeSettings.ClassicRingShare, ShapeSettings.ClassicRingShareIndex);
        float center = ShapeSettings.V(ShapeSettings.ClassicCenterShare, ShapeSettings.ClassicCenterShareIndex);
        float field = ShapeSettings.V(ShapeSettings.ClassicFieldShare, ShapeSettings.ClassicFieldShareIndex);
        float total = ring + center + field;
        if (total <= 0f)
        {
            field = 1f;
            total = 1f;
        }

        float pick = (float)rnd.NextDouble() * total;
        float vx, vy;
        if (pick < ring)
        {
            // Vanilla Classic ring: width is 0.9 of the shared ring width (0.13R).
            RingBand(rnd, ShapeSettings.V(ShapeSettings.ClassicRingRadius, ShapeSettings.ClassicRingRadiusIndex) * galaxyRadius,
                0.13f * 0.9f * galaxyRadius, 1f, 0f, 0f, out vx, out vy);
        }
        else if (pick < ring + center)
        {
            CenterBlob(rnd, 0.33f * 1.4f * galaxyRadius, out vx, out vy);
        }
        else
        {
            Disc(rnd, galaxyRadius, out vx, out vy);
        }
        x = galaxyRadius + vx;
        y = galaxyRadius + vy;
    }

    // ---- Ellipses ----

    // Scales of the long (a) and short (b) axes of an outline with axis ratio q > 1. Roughly area preserving
    // (a = sqrt(q), b = a / q), but the long axis lies on a diagonal of the square galaxy, so it may reach into the
    // corners beyond the circle. A structure reaching `extent` * R must keep its x/y extent, sqrt((A^2 + B^2) / 2), inside R.
    private static void AxisScales(float q, float extent, out float a, out float b)
    {
        float limit = 0.97f * MathF.Sqrt(2f) / MathF.Sqrt(1f + 1f / (q * q)) / extent;
        a = Math.Max(1f, Math.Min(MathF.Sqrt(q), limit));
        b = a / q;
    }

    // Stretches an offset vector into an ellipse whose long axis points along `orientation`.
    private static void Ellipse(ref float vx, ref float vy, float q, float orientation, float extent)
    {
        if (q <= 1f)
        {
            return;
        }
        AxisScales(q, extent, out float a, out float b);
        float cos = MathF.Cos(orientation);
        float sin = MathF.Sin(orientation);
        float lx = (vx * cos + vy * sin) * a;
        float ly = (-vx * sin + vy * cos) * b;
        vx = lx * cos - ly * sin;
        vy = lx * sin + ly * cos;
    }

    // ---- Ring shapes ----

    // The outer ring is only stretched (diagonally), never moved. The inner ring and the center blob can be moved
    // toward the structure around them, in a shared direction, up to the point where they touch it; the inner ring can
    // also be turned relative to the outer one. The center blob moves relative to the inner ring when there is one.
    private static void SampleRings(GameStartSettings settings, Random rnd, float galaxyRadius, out float x, out float y)
    {
        // Component weights: outer ring, inner ring, center blob; the rest is the background disc.
        float outer, inner, center;
        switch (settings.Shape)
        {
            case GalaxyShape.RingNoCenter: outer = 0.60f; inner = 0f; center = 0f; break;
            case GalaxyShape.RingDenseCenter: outer = 0.40f; inner = 0f; center = 0.20f; break;
            case GalaxyShape.DualRingNoCenter: outer = 0.38f; inner = 0.27f; center = 0f; break;
            default: outer = 0.27f; inner = 0.23f; center = 0.20f; break;
        }

        float pick = (float)rnd.NextDouble();
        float vx, vy;
        if (pick >= outer + inner + center)
        {
            Disc(rnd, galaxyRadius, out vx, out vy);
            x = galaxyRadius + vx;
            y = galaxyRadius + vy;
            return;
        }

        SeedParams seed = ParamsFor(settings.RandomSeed);
        bool hasInner = inner > 0f;
        float eccentricity = ShapeSettings.V(ShapeSettings.RingEccentricity, ShapeSettings.RingEccentricityIndex);
        float q = ShapeSettings.V(ShapeSettings.RingEllipticity, ShapeSettings.RingEllipticityIndex);
        float widthMul = ShapeSettings.RingProfileWidths[ShapeSettings.RingProfileIndex];
        float softness = ShapeSettings.RingProfileSoftness[ShapeSettings.RingProfileIndex];
        float innerRadius = ShapeSettings.V(ShapeSettings.RingInnerRadius, ShapeSettings.RingInnerRadiusIndex);
        float rotation = ShapeSettings.V(ShapeSettings.RingInnerRotation, ShapeSettings.RingInnerRotationIndex) * MathF.PI / 180f;

        float direction = ShapeSettings.V(ShapeSettings.RingOffsetDirection, ShapeSettings.RingOffsetDirectionIndex);
        float angle = direction < 0f ? seed.RandomOffsetAngle : direction * MathF.PI / 180f;
        float ux = MathF.Cos(angle);
        float uy = MathF.Sin(angle);

        // Distances (in galaxy radii) at which a structure would touch the one around it, measured along the offset direction.
        // An ellipse reaches (a, b) times as far along its long and short axes, so each radius is scaled by how far its
        // outline extends in that direction (Reach).
        float a = 1f, b = 1f;
        if (q > 1f)
        {
            AxisScales(q, 0.995f, out a, out b);
        }
        float Reach(float delta)
        {
            if (q <= 1f)
            {
                return 1f;
            }
            float c = MathF.Cos(delta) / a;
            float s = MathF.Sin(delta) / b;
            return 1f / MathF.Sqrt(c * c + s * s);
        }
        float reachOuter = Reach(angle - seed.Orientation);
        float reachInner = Reach(angle - seed.Orientation - rotation);
        float outerRingInnerEdge = (0.93f - 0.065f * widthMul) * reachOuter;
        float innerOffset = hasInner
            ? ShapeSettings.V(ShapeSettings.RingOffset, ShapeSettings.RingInnerOffsetIndex) * Math.Max(0f, outerRingInnerEdge - (innerRadius + 0.09f * widthMul) * reachInner) * galaxyRadius
            : 0f;
        float centerReference = hasInner ? (innerRadius - 0.09f * widthMul) * reachInner : outerRingInnerEdge;
        float centerOffset = ShapeSettings.V(ShapeSettings.RingOffset, ShapeSettings.RingCenterOffsetIndex) * Math.Max(0f, centerReference - 0.33f * reachOuter) * galaxyRadius;

        // The candidate may fall outside the galaxy once stretched and shifted; resample a few times, then clamp.
        x = y = galaxyRadius;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (pick < outer)
            {
                RingBand(rnd, 0.93f * galaxyRadius, 0.13f * widthMul * galaxyRadius, softness, eccentricity, seed.ClumpAngle, out vx, out vy);
                Ellipse(ref vx, ref vy, q, seed.Orientation, 0.995f);
            }
            else if (pick < outer + inner)
            {
                RingBand(rnd, innerRadius * galaxyRadius, 0.18f * widthMul * galaxyRadius, softness, eccentricity, seed.ClumpAngle + rotation, out vx, out vy);
                Ellipse(ref vx, ref vy, q, seed.Orientation + rotation, 0.995f);
                vx += innerOffset * ux;
                vy += innerOffset * uy;
            }
            else
            {
                CenterBlob(rnd, 0.33f * galaxyRadius, out vx, out vy);
                Ellipse(ref vx, ref vy, q, seed.Orientation, 0.995f);
                float total = (hasInner ? innerOffset : 0f) + centerOffset;
                vx += total * ux;
                vy += total * uy;
            }

            x = galaxyRadius + vx;
            y = galaxyRadius + vy;
            if (InBounds(x, y, galaxyRadius))
            {
                return;
            }
        }
        x = Math.Clamp(x, EdgeMargin, 2f * galaxyRadius - EdgeMargin);
        y = Math.Clamp(y, EdgeMargin, 2f * galaxyRadius - EdgeMargin);
    }

    // Nebulae placed with the rings and the center blob follow the outer ring's ellipse.
    public static void OuterEllipse(GameStartSettings settings, ref float vx, ref float vy)
    {
        Ellipse(ref vx, ref vy, ShapeSettings.V(ShapeSettings.RingEllipticity, ShapeSettings.RingEllipticityIndex), ParamsFor(settings.RandomSeed).Orientation, 0.995f);
    }

    // ---- Elliptical (placed by DeterminePointCenterEased, like Spiral) ----

    // A concentrated disc, optionally stretched along a diagonal (so the ends can reach into the corners of the galaxy),
    // and optionally with a bar: the core is stretched along the long axis and squeezed toward the axis near its tips, which
    // gives an elongated spindle that fades smoothly into the round disc.
    public static void SampleElliptical(GameStartSettings settings, Random rnd, float galaxyRadius, out float x, out float y)
    {
        float q = ShapeSettings.V(ShapeSettings.Elongation, ShapeSettings.ElongationIndex);
        float concentration = ShapeSettings.V(ShapeSettings.Concentration, ShapeSettings.ConcentrationIndex);
        float barStretch = ShapeSettings.V(ShapeSettings.Bar, ShapeSettings.BarIndex);
        float barCore = ShapeSettings.BarCoreRadius[ShapeSettings.BarIndex] * galaxyRadius;
        float orientation = ParamsFor(settings.RandomSeed).Orientation;
        float cos = MathF.Cos(orientation);
        float sin = MathF.Sin(orientation);

        x = y = galaxyRadius;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            // Local frame: x along the long axis.
            float r = galaxyRadius * MathF.Pow((float)rnd.NextDouble(), concentration);
            float theta = (float)rnd.NextDouble() * TwoPi;
            float lx = r * MathF.Cos(theta);
            float ly = r * MathF.Sin(theta);

            if (barStretch > 1f)
            {
                // Area-preserving stretch (x * k, y / k), so stars are moved rather than piled up: barStretch is the axis ratio
                // of the contours at the very center, falling to 1 within about two core radii (a Gaussian window, so the
                // outline of the disc is untouched), plus a mild pinch of the width toward the tips of the stretched region.
                float t2 = r * r / (barCore * barCore);
                float core = MathF.Exp(-t2);
                float k = MathF.Pow(barStretch, 0.5f * core);
                lx *= k;
                ly /= k;
                float tip = lx / (barCore * barStretch);
                ly /= 1f + 0.5f * core * tip * tip;
            }

            if (q > 1f)
            {
                AxisScales(q, 1f, out float a, out float b);
                lx *= a;
                ly *= b;
            }

            x = galaxyRadius + lx * cos - ly * sin;
            y = galaxyRadius + lx * sin + ly * cos;
            if (x >= 0f && y >= 0f && x <= 2f * galaxyRadius && y <= 2f * galaxyRadius)
            {
                return;
            }
        }
        x = Math.Clamp(x, 0f, 2f * galaxyRadius);
        y = Math.Clamp(y, 0f, 2f * galaxyRadius);
    }

    // ---- Webs of filaments ----

    private sealed class Web
    {
        private readonly float[] _x;
        private readonly float[] _y;
        private readonly List<(int A, int B)> _edges = new();
        private float[] _cumulativeLength = Array.Empty<float>();

        public Web(float[] xs, float[] ys)
        {
            _x = xs;
            _y = ys;
            Connect();
        }

        // Minimum spanning tree (so the web is connected) plus every node's two nearest neighbours (so it has loops).
        private void Connect()
        {
            int n = _x.Length;
            var pairs = new List<(float D, int A, int B)>();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    pairs.Add((Dist(i, j), i, j));
                }
            }
            pairs.Sort((p, q) => p.D.CompareTo(q.D));

            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int v) => parent[v] == v ? v : parent[v] = Find(parent[v]);
            var set = new HashSet<(int, int)>();
            foreach ((float _, int a, int b) in pairs)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb)
                {
                    parent[ra] = rb;
                    set.Add((a, b));
                }
            }
            for (int i = 0; i < n; i++)
            {
                int found = 0;
                foreach ((float _, int a, int b) in pairs)
                {
                    if (a != i && b != i)
                    {
                        continue;
                    }
                    set.Add((a, b));
                    if (++found == 2)
                    {
                        break;
                    }
                }
            }

            float total = 0f;
            var cumulative = new List<float>();
            foreach ((int a, int b) in set)
            {
                _edges.Add((a, b));
                total += Dist(a, b);
                cumulative.Add(total);
            }
            _cumulativeLength = cumulative.ToArray();
        }

        private float Dist(int a, int b)
        {
            float dx = _x[a] - _x[b];
            float dy = _y[a] - _y[b];
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        // A point near a filament (chosen by length), or around a node with probability nodeShare.
        public void Sample(Random rnd, float sigma, float nodeShare, out float x, out float y)
        {
            if (_edges.Count == 0 || rnd.NextDouble() < nodeShare)
            {
                int node = rnd.Next(_x.Length);
                x = _x[node] + Gaussian(rnd) * sigma * 2.2f;
                y = _y[node] + Gaussian(rnd) * sigma * 2.2f;
                return;
            }

            float pick = (float)rnd.NextDouble() * _cumulativeLength[^1];
            int e = Array.FindIndex(_cumulativeLength, c => pick <= c);
            (int a, int b) = _edges[Math.Max(e, 0)];
            float t = (float)rnd.NextDouble();
            float dx = _x[b] - _x[a];
            float dy = _y[b] - _y[a];
            float length = Math.Max(MathF.Sqrt(dx * dx + dy * dy), 1f);
            float perp = Gaussian(rnd) * sigma;
            x = _x[a] + dx * t - dy / length * perp;
            y = _y[a] + dy * t + dx / length * perp;
        }
    }

    // ---- Irregular ----

    private static (int Seed, int Nodes, float Radius, Web Web)? _irregularWeb;

    // A third of the nodes sit on a ring near the rim (evenly spaced around it, with some jitter), the rest are scattered
    // inside by dart throwing with a minimum spacing. Connecting them makes the whole galaxy, edge included, one web.
    private static Web IrregularWeb(int seed, int nodeCount, float galaxyRadius)
    {
        var cached = _irregularWeb;
        if (cached != null && cached.Value.Seed == seed && cached.Value.Nodes == nodeCount && cached.Value.Radius == galaxyRadius)
        {
            return cached.Value.Web;
        }

        var rnd = new Random(seed ^ 0x77A1);
        var xs = new List<float>();
        var ys = new List<float>();

        int edgeCount = Math.Max(5, nodeCount / 3);
        float edgePhase = (float)rnd.NextDouble() * TwoPi;
        for (int i = 0; i < edgeCount; i++)
        {
            float theta = edgePhase + (i + (float)(rnd.NextDouble() - 0.5) * 0.6f) * TwoPi / edgeCount;
            float r = galaxyRadius * (0.90f + 0.06f * (float)rnd.NextDouble());
            xs.Add(galaxyRadius + r * MathF.Cos(theta));
            ys.Add(galaxyRadius + r * MathF.Sin(theta));
        }

        // Mitchell's best candidate: each new node is the one of a few random candidates that is farthest from the nodes
        // placed so far, which spreads them evenly (including the middle) and avoids big empty patches.
        int innerCount = nodeCount - edgeCount;
        for (int i = 0; i < innerCount; i++)
        {
            float bestX = galaxyRadius, bestY = galaxyRadius, bestDistance = -1f;
            for (int candidate = 0; candidate < 14; candidate++)
            {
                float r = 0.8f * galaxyRadius * MathF.Sqrt((float)rnd.NextDouble());
                float theta = (float)rnd.NextDouble() * TwoPi;
                float px = galaxyRadius + r * MathF.Cos(theta);
                float py = galaxyRadius + r * MathF.Sin(theta);
                float nearest = float.MaxValue;
                for (int j = 0; j < xs.Count; j++)
                {
                    float dx = xs[j] - px;
                    float dy = ys[j] - py;
                    nearest = Math.Min(nearest, dx * dx + dy * dy);
                }
                if (nearest > bestDistance)
                {
                    bestDistance = nearest;
                    bestX = px;
                    bestY = py;
                }
            }
            xs.Add(bestX);
            ys.Add(bestY);
        }

        var web = new Web(xs.ToArray(), ys.ToArray());
        _irregularWeb = (seed, nodeCount, galaxyRadius, web);
        return web;
    }

    private static void SampleIrregular(GameStartSettings settings, Random rnd, float galaxyRadius, out float x, out float y)
    {
        float webShare = ShapeSettings.V(ShapeSettings.IrregularWeb, ShapeSettings.IrregularWebIndex);
        float vx, vy;
        if (webShare <= 0f || rnd.NextDouble() >= webShare)
        {
            Disc(rnd, galaxyRadius, out vx, out vy);
            x = galaxyRadius + vx;
            y = galaxyRadius + vy;
            return;
        }

        Web web = IrregularWeb(settings.RandomSeed, (int)ShapeSettings.V(ShapeSettings.IrregularNodes, ShapeSettings.IrregularNodesIndex), galaxyRadius);
        float sigma = ShapeSettings.V(ShapeSettings.FilamentSoftness, ShapeSettings.IrregularSoftnessIndex) * galaxyRadius;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            web.Sample(rnd, sigma, 0.3f, out x, out y);
            if (InBounds(x, y, galaxyRadius))
            {
                return;
            }
        }
        Disc(rnd, galaxyRadius, out vx, out vy);
        x = galaxyRadius + vx;
        y = galaxyRadius + vy;
    }

    // ---- Clusters ----

    private sealed class ClusterContext
    {
        public required List<Point> Locations;
        public required List<float> Portions;
        public required float[] Cumulative;
        public required Web Web;
    }

    private static ClusterContext? _clusters;

    // Called after the game has generated the cluster centers (GenerateStarClusters): rescales their number, their
    // portions (Varied only) and remembers them for Sample().
    public static void AdjustClusters(Galaxy galaxy, GameStartSettings settings, List<Point> locations, List<float> portions)
    {
        _clusters = null;
        if (!IsClusterShape(settings.Shape) || locations.Count == 0 || locations.Count != portions.Count)
        {
            return;
        }

        var rnd = new Random(settings.RandomSeed ^ 0x51C3);
        float originalSum = portions.Sum();
        float average = originalSum / portions.Count;

        int target = Math.Max(2, (int)MathF.Round(locations.Count * ShapeSettings.V(ShapeSettings.ClusterAmount, ShapeSettings.ClusterAmountIndex)));
        while (locations.Count > target)
        {
            int i = rnd.Next(locations.Count);
            locations.RemoveAt(i);
            portions.RemoveAt(i);
        }
        float center = galaxy.SizeX * 0.5f;
        while (locations.Count < target)
        {
            float r = 0.42f * galaxy.SizeX * MathF.Sqrt((float)rnd.NextDouble());
            float theta = (float)rnd.NextDouble() * TwoPi;
            locations.Add(new Point((int)(center + r * MathF.Cos(theta)), (int)(center + r * MathF.Sin(theta))));
            portions.Add(average * (0.5f + (float)rnd.NextDouble()));
        }

        if (settings.Shape == GalaxyShape.ClustersVaried)
        {
            float exponent = ShapeSettings.V(ShapeSettings.ClusterVariability, ShapeSettings.ClusterVariabilityIndex);
            if (exponent != 1f)
            {
                for (int i = 0; i < portions.Count; i++)
                {
                    portions[i] = MathF.Pow(Math.Max(portions[i], 1e-6f), exponent);
                }
            }
        }
        float sum = portions.Sum();
        if (sum > 0f)
        {
            for (int i = 0; i < portions.Count; i++)
            {
                portions[i] *= originalSum / sum;
            }
        }

        var cumulative = new float[portions.Count];
        float running = 0f;
        for (int i = 0; i < portions.Count; i++)
        {
            running += portions[i];
            cumulative[i] = running;
        }

        _clusters = new ClusterContext
        {
            Locations = locations,
            Portions = portions,
            Cumulative = cumulative,
            Web = new Web(locations.Select(p => (float)p.X).ToArray(), locations.Select(p => (float)p.Y).ToArray()),
        };
    }

    private static void SampleClusters(Galaxy galaxy, GameStartSettings settings, Random rnd, float galaxyRadius, out float x, out float y)
    {
        ClusterContext? ctx = _clusters;
        float background = ShapeSettings.V(ShapeSettings.ClusterBackground, ShapeSettings.ClusterBackgroundIndex);
        float vx, vy;
        if (ctx == null || rnd.NextDouble() < background)
        {
            Disc(rnd, galaxyRadius, out vx, out vy);
            x = galaxyRadius + vx;
            y = galaxyRadius + vy;
            return;
        }

        float edge = EdgeMargin;
        float max = galaxy.SizeX - edge;
        float web = ShapeSettings.V(ShapeSettings.ClusterWeb, ShapeSettings.ClusterWebIndex);
        if (web > 0f && rnd.NextDouble() < web)
        {
            float sigma = ShapeSettings.V(ShapeSettings.FilamentSoftness, ShapeSettings.ClusterSoftnessIndex) * galaxyRadius;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                ctx.Web.Sample(rnd, sigma, 0f, out x, out y);
                if (x >= edge && y >= edge && x <= max && y <= max)
                {
                    return;
                }
            }
        }

        // Vanilla DeterminePointCluster: cluster picked by portion, uniform inside a disc whose radius grows with sqrt(portion).
        float pick = (float)rnd.NextDouble() * ctx.Cumulative[^1];
        int index = Array.FindIndex(ctx.Cumulative, c => pick <= c);
        index = Math.Max(index, 0);
        float baseRadius = galaxy.SizeX / (2f * MathF.Sqrt(ctx.Locations.Count));
        float clusterRadius = MathF.Sqrt(ctx.Portions[index]) * baseRadius * 3f * 0.5f * ShapeSettings.V(ShapeSettings.ClusterSize, ShapeSettings.ClusterSizeIndex);

        x = ctx.Locations[index].X;
        y = ctx.Locations[index].Y;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            float r = MathF.Sqrt((float)rnd.NextDouble()) * clusterRadius;
            float theta = (float)rnd.NextDouble() * TwoPi;
            x = ctx.Locations[index].X + r * MathF.Cos(theta);
            y = ctx.Locations[index].Y + r * MathF.Sin(theta);
            if (x >= edge && y >= edge && x <= max && y <= max)
            {
                return;
            }
        }
        x = Math.Clamp(x, edge, max);
        y = Math.Clamp(y, edge, max);
    }
}

