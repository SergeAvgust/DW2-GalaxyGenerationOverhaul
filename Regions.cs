using System.Globalization;
using System.Reflection;
using System.Text;
using DistantWorlds.Types;
using HarmonyLib;

namespace SpiralArms;

// Splits the galaxy into one balanced region per empire (used by RegionPlacement), and after each generated game writes a map of the
// regions and of where the capitals ended up (SpiralArms_regions.svg in the temp folder) plus a log summary.
//
//   1. Density: every star gets the number of other stars within the neighbour range (HomeSites.Ranges). Relative to the galaxy's
//      median that makes it void / sparse / moderate / dense.
//   2. Candidates: moderate and dense stars. Voids and stragglers are left out.
//   3. Regions: candidates closer than 0.75 * range are linked into continuous stretches (an arm with the core it springs from). Seeds
//      are spread over them (at least one per stretch of half a region or more), and the regions grow outward along the links, the
//      smallest first, over links that get expensive quickly with length. Regions never cross the gaps between stretches, and stop
//      at 1.3 times the ideal size, so they follow arms and clusters instead of reaching across voids. Stretches too small for a
//      region are left out. Works for any galaxy shape.
//   4. Site: per region, a well-connected star near the middle of its stars.
internal static class Regions
{
    internal sealed class Star
    {
        public int Id;
        public float X, Y;       // millions
        public int Neighbours;
        public int Density;      // 0 void, 1 sparse, 2 moderate, 3 dense
        public int Region = -1;
    }

    public static void Install(Harmony harmony)
    {
        MethodInfo? generate = AccessTools.Method(typeof(Galaxy), "Generate");
        if (generate == null)
        {
            Bootstrap.Log("Regions: Galaxy.Generate not found; the region map is disabled.");
            return;
        }
        harmony.Patch(generate, finalizer: new HarmonyMethod(typeof(Regions).GetMethod(nameof(GenerateFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    private static Exception? GenerateFinalizer(Galaxy __instance, GameStartSettings settings, bool previewMode, bool isBackgroundGalaxy, Exception? __exception)
    {
        if (__exception == null && !previewMode && !isBackgroundGalaxy && settings.StarCount > 0)
        {
            try
            {
                Analyse(__instance);
            }
            catch (Exception ex)
            {
                Bootstrap.Log("Regions: failed: " + ex);
            }
        }
        return __exception;
    }

    // Result of splitting the galaxy: candidate stars grouped into one region per empire, and a site per region.
    internal sealed class Plan
    {
        public List<Star> Stars = new();
        public List<Star> Candidates = new();
        public int K;
        public float Range;      // millions
        public float Median;
        public int Dropped;
        public int[] ByDensity = new int[4];
        public int[] Sizes = Array.Empty<int>();
        public List<Star>[] Members = Array.Empty<List<Star>>();
        public Star?[] Sites = Array.Empty<Star?>();
        public float[] Cx = Array.Empty<float>(), Cy = Array.Empty<float>();   // site positions, millions
        public readonly Dictionary<int, int> RegionOfSystem = new();
    }

    // minDensity: 3 = dense stars only, 2 = dense and moderate, 1 = everything but voids.
    internal static Plan? Build(Galaxy galaxy, int k, int minDensity)
    {
        var plan = new Plan { K = k };
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            if (entry.Value != null)
            {
                plan.Stars.Add(new Star { Id = entry.Value.SystemId, X = entry.Value.GalaxyX / 1e6f, Y = entry.Value.GalaxyY / 1e6f });
            }
        }
        List<Star> stars = plan.Stars;
        if (stars.Count < 10 || k < 1)
        {
            return null;
        }

        // 1. density
        float range = HomeSites.Ranges[HomeSites.RangeIndex].Value / 1e6f;
        float range2 = range * range;
        foreach (Star a in stars)
        {
            int n = 0;
            foreach (Star b in stars)
            {
                float dx = a.X - b.X, dy = a.Y - b.Y;
                if (dx * dx + dy * dy <= range2)
                {
                    n++;
                }
            }
            a.Neighbours = n - 1;
        }
        int[] sorted = stars.Select(s => s.Neighbours).OrderBy(v => v).ToArray();
        float median = Math.Max(1f, sorted[sorted.Length / 2]);
        foreach (Star s in stars)
        {
            s.Density = s.Neighbours < 0.25f * median ? 0 : s.Neighbours < 0.6f * median ? 1 : s.Neighbours < 1.5f * median ? 2 : 3;
            plan.ByDensity[s.Density]++;
        }
        plan.Range = range;
        plan.Median = median;

        // 2-3. candidates and balanced regions
        List<Star> candidates = stars.Where(s => s.Density >= minDensity).ToList();
        if (candidates.Count < k)
        {
            candidates = stars;
        }
        Grow(candidates, k, range);
        plan.Dropped = candidates.Count(c => c.Region < 0);
        candidates = candidates.Where(c => c.Region >= 0).ToList();
        plan.Candidates = candidates;

        // 4. sites
        plan.Sites = new Star?[k];
        plan.Sizes = new int[k];
        plan.Members = new List<Star>[k];
        plan.Cx = new float[k];
        plan.Cy = new float[k];
        for (int r = 0; r < k; r++)
        {
            List<Star> members = candidates.Where(s => s.Region == r).ToList();
            plan.Members[r] = members;
            plan.Sizes[r] = members.Count;
            foreach (Star m in members)
            {
                plan.RegionOfSystem[m.Id] = r;
            }
            if (members.Count == 0)
            {
                continue;
            }
            int med = members.Select(s => s.Neighbours).OrderBy(v => v).ToArray()[members.Count / 2];
            // medoid among the well-connected stars: the one closest to all the others, so it lies inside the region
            Star site = members.Where(s => s.Neighbours >= med).OrderBy(s => members.Sum(m => Math.Sqrt(Dist2(s.X, s.Y, m.X, m.Y)))).First();
            plan.Sites[r] = site;
            plan.Cx[r] = site.X;
            plan.Cy[r] = site.Y;
        }
        return plan;
    }

    private static void Analyse(Galaxy galaxy)
    {
        var capitals = new List<(string Name, bool Player, float X, float Y)>();
        foreach (Empire e in galaxy.Empires)
        {
            if (e.Capital != null && !e.IsPirate)
            {
                capitals.Add((e.Name, e.IsPlayer, e.Capital.GalaxyX / 1e6f, e.Capital.GalaxyY / 1e6f));
            }
        }
        int k = capitals.Count;
        Plan? plan = Build(galaxy, k, RegionPlacement.MinDensity);
        if (plan == null)
        {
            Bootstrap.Log($"Regions: nothing to do ({galaxy.Systems.Count} stars, {k} empires).");
            return;
        }
        List<Star> candidates = plan.Candidates;

        // where the capitals ended up: region of the nearest candidate star
        var perRegion = new int[k];
        var capitalRegion = new int[k];
        for (int i = 0; i < k; i++)
        {
            Star nearest = candidates.OrderBy(s => Dist2(s.X, s.Y, capitals[i].X, capitals[i].Y)).First();
            capitalRegion[i] = nearest.Region;
            perRegion[nearest.Region]++;
        }

        Bootstrap.Log($"Regions: {plan.Stars.Count} stars, range {plan.Range:0}M, median {plan.Median:0} neighbours; void {plan.ByDensity[0]}, sparse {plan.ByDensity[1]}, moderate {plan.ByDensity[2]}, dense {plan.ByDensity[3]}.");
        Bootstrap.Log($"Regions: {k} regions of {plan.Sizes.Min()}..{plan.Sizes.Max()} stars (ideal {candidates.Count / (float)k:0}), {plan.Dropped} candidate stars left out in pieces too small for a region; "
            + $"capitals: {perRegion.Count(c => c == 0)} regions without one, {perRegion.Count(c => c > 1)} with more than one.");
        for (int i = 0; i < k; i++)
        {
            Bootstrap.Log($"Regions: capital {capitals[i].Name}{(capitals[i].Player ? " (player)" : "")} is in region {capitalRegion[i]}.");
        }

        string path = Path.Combine(Path.GetTempPath(), "SpiralArms_regions.svg");
        File.WriteAllText(path, Svg(plan.Stars, candidates, plan.Cx, plan.Cy, plan.Sites, capitals, capitalRegion));
        Bootstrap.Log("Regions: map written to " + path);
    }

    private static float Dist2(float ax, float ay, float bx, float by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);

    // Share of the neighbour range up to which two stars count as one continuous stretch of the galaxy. Longer links are gaps (the
    // space between arms): regions never grow across them.
    private const float GapShare = 0.75f;
    // A region stops growing at this multiple of the ideal size, so one that has used up its arm does not have to cross a void.
    private const float MaxSizeFactor = 1.3f;
    // How many times the seeds are moved to the middle of their regions and the regions regrown.
    private const int RelaxIterations = 3;

    // Splits the candidate stars into k regions.
    //   - Stars are linked when closer than GapShare * range. Connected stretches ("pieces", e.g. an arm with the core it springs from)
    //     are the only places regions can live; pieces smaller than half a region get none and their stars are left out.
    //   - Every piece with at least half a region of stars gets at least one seed, the rest in proportion to size; inside a piece the
    //     seeds are placed farthest-first, so they end up at the tips of arms and in the middle of blobs.
    //   - Regions then grow outward along the links, the smallest first, each taking the star next to it that is nearest to its seed
    //     (so regions are compact blobs, and long links only matter for what counts as connected). A region stops at MaxSizeFactor times
    //     the ideal size; stars nobody wanted are handed to the neighbouring region afterwards.
    //   - Seeds are then moved to the middle of their regions and the growth is repeated a few times, and stars surrounded by another
    //     region join it.
    private static void Grow(List<Star> pts, int k, float range)
    {
        int n = pts.Count;
        float linkMax2 = GapShare * range * GapShare * range;
        var adj = new List<(int To, float Cost)>[n];
        for (int i = 0; i < n; i++)
        {
            adj[i] = new List<(int, float)>();
            pts[i].Region = -1;
        }
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float d2 = Dist2(pts[i].X, pts[i].Y, pts[j].X, pts[j].Y);
                if (d2 <= linkMax2)
                {
                    float ratio = (float)Math.Sqrt(d2) / range;
                    float cost = 1f + 8f * ratio * ratio * ratio;
                    adj[i].Add((j, cost));
                    adj[j].Add((i, cost));
                }
            }
        }

        // pieces
        var comp = new int[n];
        Array.Fill(comp, -1);
        var sizes = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (comp[i] >= 0)
            {
                continue;
            }
            int id = sizes.Count, count = 0;
            var stack = new Stack<int>();
            stack.Push(i);
            comp[i] = id;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                count++;
                foreach (var (v, _) in adj[u])
                {
                    if (comp[v] < 0)
                    {
                        comp[v] = id;
                        stack.Push(v);
                    }
                }
            }
            sizes.Add(count);
        }

        // seeds per piece
        float ideal = n / (float)k;
        List<int> eligible = Enumerable.Range(0, sizes.Count).Where(c => sizes[c] >= 0.5f * ideal).OrderByDescending(c => sizes[c]).Take(k).ToList();
        if (eligible.Count == 0)
        {
            eligible.Add(Enumerable.Range(0, sizes.Count).OrderByDescending(c => sizes[c]).First());
        }
        int eligibleStars = eligible.Sum(c => sizes[c]);
        var seeds = new int[sizes.Count];
        var share = new double[sizes.Count];
        foreach (int c in eligible)
        {
            share[c] = k * (double)sizes[c] / eligibleStars;
            seeds[c] = Math.Max(1, (int)Math.Floor(share[c]));
        }
        int total = eligible.Sum(c => seeds[c]);
        while (total < k)
        {
            int c = eligible.OrderByDescending(x => share[x] - seeds[x]).First();
            seeds[c]++;
            total++;
        }
        while (total > k)
        {
            int c = eligible.Where(x => seeds[x] > 1).OrderBy(x => share[x] - seeds[x]).FirstOrDefault(-1);
            if (c < 0)
            {
                break;
            }
            seeds[c]--;
            total--;
        }

        // seeds inside each piece, farthest-first by link cost, starting from the best-connected star
        var seedNodes = new List<int>();
        foreach (int c in eligible)
        {
            var members = Enumerable.Range(0, n).Where(i => comp[i] == c).ToList();
            var dist = new float[n];
            Array.Fill(dist, float.MaxValue);
            int next = members.OrderByDescending(i => pts[i].Neighbours).First();
            for (int s = 0; s < seeds[c]; s++)
            {
                seedNodes.Add(next);
                Relax(adj, dist, next);
                next = members.OrderByDescending(i => dist[i]).First();
            }
        }

        // Growth is run a few times: after each run every seed moves to the star nearest the middle of its region and the regions are
        // grown again (k-means on the link graph), which makes them compact and keeps seeds off the edges.
        var eligibleSet = new HashSet<int>(eligible);
        int cap = (int)Math.Ceiling(MaxSizeFactor * ideal);
        for (int iteration = 0; ; iteration++)
        {
            GrowRegions(pts, adj, seedNodes, cap, comp, eligibleSet, range);
            if (iteration >= RelaxIterations)
            {
                break;
            }
            bool moved = false;
            for (int r = 0; r < seedNodes.Count; r++)
            {
                float sx = 0, sy = 0;
                int count = 0;
                foreach (Star s in pts)
                {
                    if (s.Region == r)
                    {
                        sx += s.X;
                        sy += s.Y;
                        count++;
                    }
                }
                if (count == 0)
                {
                    continue;
                }
                sx /= count;
                sy /= count;
                int best = seedNodes[r];
                float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (pts[i].Region == r)
                    {
                        float d = Dist2(pts[i].X, pts[i].Y, sx, sy);
                        if (d < bestD)
                        {
                            bestD = d;
                            best = i;
                        }
                    }
                }
                if (best != seedNodes[r])
                {
                    seedNodes[r] = best;
                    moved = true;
                }
            }
            if (!moved)
            {
                break;
            }
        }
        Smooth(pts, adj, seedNodes);
    }

    // Grows one region per seed. The smallest region that can still grow takes the star next to it (over a link) that is nearest to
    // its seed, so regions stay compact; stars nobody wanted join a neighbouring region afterwards.
    private static void GrowRegions(List<Star> pts, List<(int To, float Cost)>[] adj, List<int> seedNodes, int cap, int[] comp, HashSet<int> eligibleSet, float range)
    {
        int n = pts.Count;
        int regions = seedNodes.Count;
        foreach (Star s in pts)
        {
            s.Region = -1;
        }
        var frontier = new PriorityQueue<int, float>[regions];
        var held = new int[regions];
        for (int r = 0; r < regions; r++)
        {
            frontier[r] = new PriorityQueue<int, float>();
            frontier[r].Enqueue(seedNodes[r], 0f);
        }
        while (true)
        {
            int pick = -1;
            float pickScaled = 0f;
            for (int r = 0; r < regions; r++)
            {
                // Soft balance: the cheapest offer wins, but a region that is already big pays more for each star.
                if (held[r] < cap && frontier[r].TryPeek(out _, out float offer))
                {
                    float scaled = (0.2f + offer) * (0.5f + held[r] * MaxSizeFactor / cap);
                    if (pick < 0 || scaled < pickScaled)
                    {
                        pick = r;
                        pickScaled = scaled;
                    }
                }
            }
            if (pick < 0)
            {
                break;
            }
            frontier[pick].TryDequeue(out int node, out _);
            if (pts[node].Region >= 0)
            {
                continue;
            }
            pts[node].Region = pick;
            held[pick]++;
            Star seed = pts[seedNodes[pick]];
            foreach (var (v, _) in adj[node])
            {
                if (pts[v].Region < 0)
                {
                    frontier[pick].Enqueue(v, (float)Math.Sqrt(Dist2(pts[v].X, pts[v].Y, seed.X, seed.Y)) / range);
                }
            }
        }

        // stars left over inside the pieces (their neighbours were full): join the region next to them, the smallest if several
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (pts[i].Region >= 0 || !eligibleSet.Contains(comp[i]))
                {
                    continue;
                }
                int best = -1;
                foreach (var (v, _) in adj[i])
                {
                    int r = pts[v].Region;
                    if (r >= 0 && (best < 0 || held[r] < held[best]))
                    {
                        best = r;
                    }
                }
                if (best >= 0)
                {
                    pts[i].Region = best;
                    held[best]++;
                    changed = true;
                }
            }
        }
    }

    // A star linked mostly to another region's stars joins that region (removes isolated specks and thin fingers).
    private static void Smooth(List<Star> pts, List<(int To, float Cost)>[] adj, List<int> seedNodes)
    {
        var seeds = new HashSet<int>(seedNodes);
        var counts = new Dictionary<int, int>();
        for (int pass = 0; pass < 2; pass++)
        {
            var flips = new List<(int Star, int Region)>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].Region < 0 || seeds.Contains(i))
                {
                    continue;
                }
                counts.Clear();
                foreach (var (v, _) in adj[i])
                {
                    int r = pts[v].Region;
                    if (r >= 0)
                    {
                        counts[r] = counts.GetValueOrDefault(r) + 1;
                    }
                }
                int own = counts.GetValueOrDefault(pts[i].Region);
                int bestRegion = pts[i].Region, bestCount = own;
                foreach (KeyValuePair<int, int> c in counts)
                {
                    if (c.Value > bestCount + 1)
                    {
                        bestRegion = c.Key;
                        bestCount = c.Value;
                    }
                }
                if (bestRegion != pts[i].Region)
                {
                    flips.Add((i, bestRegion));
                }
            }
            if (flips.Count == 0)
            {
                break;
            }
            foreach (var (star, region) in flips)
            {
                pts[star].Region = region;
            }
        }
    }

    private static void Relax(List<(int To, float Cost)>[] adj, float[] dist, int source)
    {
        var queue = new PriorityQueue<int, float>();
        dist[source] = 0f;
        queue.Enqueue(source, 0f);
        while (queue.TryDequeue(out int u, out float d))
        {
            if (d > dist[u])
            {
                continue;
            }
            foreach (var (v, cost) in adj[u])
            {
                if (d + cost < dist[v])
                {
                    dist[v] = d + cost;
                    queue.Enqueue(v, dist[v]);
                }
            }
        }
    }

    private static string Svg(List<Star> stars, List<Star> candidates, float[] cx, float[] cy, Star?[] sites,
        List<(string Name, bool Player, float X, float Y)> capitals, int[] capitalRegion)
    {
        float minX = stars.Min(s => s.X), maxX = stars.Max(s => s.X), minY = stars.Min(s => s.Y), maxY = stars.Max(s => s.Y);
        float scale = 1000f / Math.Max(maxX - minX, maxY - minY);
        float W = (maxX - minX) * scale + 40, H = (maxY - minY) * scale + 40;
        string X(float v) => ((v - minX) * scale + 20).ToString("0.#", CultureInfo.InvariantCulture);
        string Y(float v) => ((v - minY) * scale + 20).ToString("0.#", CultureInfo.InvariantCulture);
        string Color(int region) => $"hsl({region * 360 / Math.Max(1, cx.Length)},75%,50%)";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns='http://www.w3.org/2000/svg' width='{W:0}' height='{H:0}' style='background:#10131a'>\n");
        foreach (Star s in stars.Where(s => s.Density < 2))
        {
            sb.Append($"<circle cx='{X(s.X)}' cy='{Y(s.Y)}' r='{(s.Density == 0 ? 1.2 : 1.6)}' fill='{(s.Density == 0 ? "#4a4a4a" : "#7a7a7a")}'/>\n");
        }
        foreach (Star s in candidates)
        {
            sb.Append($"<circle cx='{X(s.X)}' cy='{Y(s.Y)}' r='{(s.Density == 3 ? 2.6 : 2)}' fill='{Color(s.Region)}'/>\n");
        }
        for (int r = 0; r < sites.Length; r++)
        {
            Star? site = sites[r];
            if (site != null)
            {
                sb.Append($"<circle cx='{X(site.X)}' cy='{Y(site.Y)}' r='9' fill='none' stroke='white' stroke-width='2'/>\n");
                sb.Append($"<text x='{X(site.X)}' y='{Y(site.Y)}' dx='12' dy='-8' fill='white' font-size='14' font-family='sans-serif'>{r}</text>\n");
            }
        }
        for (int i = 0; i < capitals.Count; i++)
        {
            string stroke = capitals[i].Player ? "#ffd400" : "#ff3030";
            string x = X(capitals[i].X), y = Y(capitals[i].Y);
            sb.Append($"<path d='M{x} {y}m-7 -7l14 14m0 -14l-14 14' stroke='{stroke}' stroke-width='3'/>\n");
            sb.Append($"<text x='{x}' y='{y}' dx='9' dy='16' fill='{stroke}' font-size='12' font-family='sans-serif'>{System.Security.SecurityElement.Escape(capitals[i].Name)} (r{capitalRegion[i]})</text>\n");
        }
        sb.Append("<text x='20' y='14' fill='#bbb' font-size='12' font-family='sans-serif'>coloured = region (dense dots larger); grey = sparse / void; white ring = region site; X = vanilla capital (yellow = player)</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
