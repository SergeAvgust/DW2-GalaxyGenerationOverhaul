using System.Globalization;
using System.Reflection;
using System.Text;
using DistantWorlds.Types;
using HarmonyLib;

namespace SpiralArms;

// Diagnostic first step of the empire-placement overhaul: splits the galaxy into one balanced region per empire and writes a map
// (SpiralArms_regions.svg in the temp folder) plus a log summary. It does not change where anything is placed yet.
//
//   1. Density: every star gets the number of other stars within the neighbour range (HomeSites.Ranges). Relative to the galaxy's
//      median that makes it void / sparse / moderate / dense.
//   2. Candidates: moderate and dense stars. Voids and stragglers are left out.
//   3. Regions: candidates within range of each other are linked into a graph. Seeds are spread over it (in proportion to the size of
//      each connected piece), and all regions then grow outward, the smallest region always taking the next star. A region can only
//      take a star next to one it already holds, so it stays in one piece and cannot jump a void; pieces too small to hold a
//      region are left out. Cost is measured in hops between stars, so it works for any galaxy shape.
//   4. Site: per region, a well-connected star near the middle of its stars.
// The map also shows where vanilla actually put the capitals, to compare with the regions.
internal static class Regions
{
    private sealed class Star
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

    private static void Analyse(Galaxy galaxy)
    {
        var stars = new List<Star>();
        foreach (KeyValuePair<int, StarSystem> entry in galaxy.Systems)
        {
            if (entry.Value != null)
            {
                stars.Add(new Star { Id = entry.Value.SystemId, X = entry.Value.GalaxyX / 1e6f, Y = entry.Value.GalaxyY / 1e6f });
            }
        }
        var capitals = new List<(string Name, bool Player, float X, float Y)>();
        foreach (Empire e in galaxy.Empires)
        {
            if (e.Capital != null && !e.IsPirate)
            {
                capitals.Add((e.Name, e.IsPlayer, e.Capital.GalaxyX / 1e6f, e.Capital.GalaxyY / 1e6f));
            }
        }
        int k = capitals.Count;
        if (stars.Count < 10 || k < 1)
        {
            Bootstrap.Log($"Regions: nothing to do ({stars.Count} stars, {k} empires).");
            return;
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
        }

        // 2-3. candidates and balanced regions
        List<Star> candidates = stars.Where(s => s.Density >= 2).ToList();
        if (candidates.Count < k)
        {
            candidates = stars;
        }
        float[] cx = new float[k], cy = new float[k];
        Grow(candidates, k, range);
        int dropped = candidates.Count(c => c.Region < 0);
        candidates = candidates.Where(c => c.Region >= 0).ToList();

        // 4. sites
        var sites = new Star?[k];
        var sizes = new int[k];
        foreach (Star s in candidates)
        {
            sizes[s.Region]++;
        }
        for (int r = 0; r < k; r++)
        {
            var members = candidates.Where(s => s.Region == r).ToList();
            if (members.Count == 0)
            {
                continue;
            }
            int med = members.Select(s => s.Neighbours).OrderBy(v => v).ToArray()[members.Count / 2];
            // medoid among the well-connected stars: the one closest to all the others, so it lies inside the region
            sites[r] = members.Where(s => s.Neighbours >= med).OrderBy(s => members.Sum(m => Math.Sqrt(Dist2(s.X, s.Y, m.X, m.Y)))).First();
            cx[r] = sites[r]!.X;
            cy[r] = sites[r]!.Y;
        }

        // where vanilla put the capitals: region of the nearest candidate star
        var perRegion = new int[k];
        var capitalRegion = new int[k];
        for (int i = 0; i < k; i++)
        {
            Star nearest = candidates.OrderBy(s => Dist2(s.X, s.Y, capitals[i].X, capitals[i].Y)).First();
            capitalRegion[i] = nearest.Region;
            perRegion[nearest.Region]++;
        }

        int[] byDensity = new int[4];
        foreach (Star s in stars)
        {
            byDensity[s.Density]++;
        }
        Bootstrap.Log($"Regions: {stars.Count} stars, range {range:0}M, median {median:0} neighbours; void {byDensity[0]}, sparse {byDensity[1]}, moderate {byDensity[2]}, dense {byDensity[3]}.");
        Bootstrap.Log($"Regions: {k} regions of {sizes.Min()}..{sizes.Max()} stars (ideal {candidates.Count / (float)k:0}), {dropped} candidate stars left out in pieces too small for a region; "
            + $"vanilla capitals: {perRegion.Count(c => c == 0)} regions without one, {perRegion.Count(c => c > 1)} with more than one.");
        for (int i = 0; i < k; i++)
        {
            Bootstrap.Log($"Regions: capital {capitals[i].Name}{(capitals[i].Player ? " (player)" : "")} is in region {capitalRegion[i]}.");
        }

        string path = Path.Combine(Path.GetTempPath(), "SpiralArms_regions.svg");
        File.WriteAllText(path, Svg(stars, candidates, cx, cy, sites, capitals, capitalRegion));
        Bootstrap.Log("Regions: map written to " + path);
    }

    private static float Dist2(float ax, float ay, float bx, float by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);

    // Links candidate stars within `range` of each other, spreads k seeds over the graph (in proportion to the size of each connected
    // piece, farthest-first inside a piece) and grows the regions: the smallest region takes the cheapest star next to its own.
    private static void Grow(List<Star> pts, int k, float range)
    {
        int n = pts.Count;
        float range2 = range * range;
        var adj = new List<(int To, float Cost)>[n];
        for (int i = 0; i < n; i++)
        {
            adj[i] = new List<(int, float)>();
        }
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float d2 = Dist2(pts[i].X, pts[i].Y, pts[j].X, pts[j].Y);
                if (d2 <= range2)
                {
                    float cost = 1f + (float)Math.Sqrt(d2) / range;
                    adj[i].Add((j, cost));
                    adj[j].Add((i, cost));
                }
            }
            pts[i].Region = -1;
        }

        // connected pieces
        var comp = new int[n];
        Array.Fill(comp, -1);
        var sizesByComp = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (comp[i] >= 0)
            {
                continue;
            }
            int id = sizesByComp.Count, count = 0;
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
            sizesByComp.Add(count);
        }

        // seeds per piece: largest remainder on size; pieces smaller than half a region get none
        float ideal = n / (float)k;
        var seeds = new int[sizesByComp.Count];
        var eligible = Enumerable.Range(0, sizesByComp.Count).Where(c => sizesByComp[c] >= 0.5f * ideal).ToList();
        if (eligible.Count == 0)
        {
            eligible = new List<int> { Enumerable.Range(0, sizesByComp.Count).OrderByDescending(c => sizesByComp[c]).First() };
        }
        int eligibleStars = eligible.Sum(c => sizesByComp[c]);
        int given = 0;
        foreach (int c in eligible)
        {
            seeds[c] = (int)(k * (double)sizesByComp[c] / eligibleStars);
            given += seeds[c];
        }
        foreach (int c in eligible.OrderByDescending(c => k * (double)sizesByComp[c] / eligibleStars - seeds[c]).Take(k - given))
        {
            seeds[c]++;
        }

        // farthest-first seeds inside each piece, by cost through the graph
        var seedNodes = new List<int>();
        foreach (int c in eligible)
        {
            if (seeds[c] == 0)
            {
                continue;
            }
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

        // growth: the smallest region with a reachable star takes the cheapest one next to its own
        int regions = seedNodes.Count;
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
            for (int r = 0; r < regions; r++)
            {
                if (frontier[r].Count > 0 && (pick < 0 || held[r] < held[pick]))
                {
                    pick = r;
                }
            }
            if (pick < 0)
            {
                break;
            }
            frontier[pick].TryDequeue(out int node, out float cost);
            if (pts[node].Region >= 0)
            {
                continue;
            }
            pts[node].Region = pick;
            held[pick]++;
            foreach (var (v, edge) in adj[node])
            {
                if (pts[v].Region < 0)
                {
                    frontier[pick].Enqueue(v, cost + edge);
                }
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
