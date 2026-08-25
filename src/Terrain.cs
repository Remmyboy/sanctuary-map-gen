// Structural analysis of a finished heightfield.
//
// The economy numbers came out of counting markers, which is easy. What makes a
// hand-made map feel better than a generated one is structure - where the lanes
// are, how wide they get, how often they pinch, and what overlooks them - and
// none of that is visible in a slope histogram. A map can be 100% reachable,
// 80% flat and completely characterless.
//
// So: find the route an army actually takes between two spawns, then measure it.
// Clearance comes from a distance transform of the walkable mask, which gives
// the radius of the largest circle that fits at each cell - the natural
// definition of "how wide is it here".
//
// Everything here uses Sanctuary's Land nav rule even when the input is a
// Supreme Commander map, because the question being asked is what that terrain
// would play like in this game.
public static partial class MapGen
{
    /// Radius in metres of the largest free circle centred on each walkable
    /// cell. Zero on blocked ground.
    public static float[,] ClearanceField()
    {
        int n = HRes;
        float step = MapSize / (n - 1);
        var d = DistanceInside(Walkable, n);      // chamfer, in cells
        var outp = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                outp[y, x] = d[y, x] * step;
        return outp;
    }

    /// Cell indices of the shortest walkable route between two world points,
    /// as a flat list of row,col pairs. Empty if there is no route.
    ///
    /// Dijkstra on the 8-neighbourhood with true step costs. This is meant to
    /// be the line a player draws on the minimap, not the flow field the engine
    /// would produce.
    public static List<int> RouteCells(float ax, float az, float bx, float bz)
    {
        int n = HRes;
        float step = MapSize / (n - 1);
        int ar = (int)Math.Round((MapSize - az) / step), ac = (int)Math.Round(ax / step);
        int br = (int)Math.Round((MapSize - bz) / step), bc = (int)Math.Round(bx / step);
        var empty = new List<int>();
        if (ar < 0 || ar >= n || ac < 0 || ac >= n || br < 0 || br >= n || bc < 0 || bc >= n)
            return empty;

        // A spawn can sit a cell or two off walkable ground; snap to the
        // nearest walkable cell rather than reporting no route.
        if (!Walkable[ar, ac] && !SnapToWalkable(ref ar, ref ac)) return empty;
        if (!Walkable[br, bc] && !SnapToWalkable(ref br, ref bc)) return empty;

        // Dijkstra with true step costs, not breadth-first.
        //
        // BFS minimises the number of hops, which is not the same as the
        // shortest route on an 8-neighbourhood: for a target on the diagonal
        // every monotone staircase uses the same number of hops, and BFS
        // returns an arbitrary one. Measuring that staircase with a cost of
        // sqrt(2) per diagonal step and 1 per orthogonal step then reports a
        // route far longer than the straight line - a pure staircase scores
        // exactly sqrt(2)/2 = 0.707 directness on completely open ground.
        //
        // That artefact was rejecting good maps. Every Open-style roll came out
        // between 0.77 and 0.79 regardless of what the terrain looked like,
        // which is the signature of a measurement bug rather than a map
        // property: flat, empty ground should score near 1.
        var prev = new int[n * n];
        var dist = new float[n * n];
        for (int i = 0; i < prev.Length; i++) { prev[i] = -1; dist[i] = float.MaxValue; }

        int start = ar * n + ac, target = br * n + bc;
        dist[start] = 0f;

        // Binary heap over (cost, cell). Hand-rolled to keep this compiling on
        // whatever C# the host provides.
        var heapCell = new int[1024];
        var heapCost = new float[1024];
        int heapN = 0;

        Action<int, float> push = null;
        push = (cell, cost) =>
        {
            if (heapN == heapCell.Length)
            {
                Array.Resize(ref heapCell, heapN * 2);
                Array.Resize(ref heapCost, heapN * 2);
            }
            int i = heapN++;
            heapCell[i] = cell; heapCost[i] = cost;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heapCost[p] <= heapCost[i]) break;
                int tc = heapCell[p]; heapCell[p] = heapCell[i]; heapCell[i] = tc;
                float tf = heapCost[p]; heapCost[p] = heapCost[i]; heapCost[i] = tf;
                i = p;
            }
        };
        Func<int> pop = () =>
        {
            int top = heapCell[0];
            heapN--;
            heapCell[0] = heapCell[heapN]; heapCost[0] = heapCost[heapN];
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < heapN && heapCost[l] < heapCost[m]) m = l;
                if (r < heapN && heapCost[r] < heapCost[m]) m = r;
                if (m == i) break;
                int tc = heapCell[m]; heapCell[m] = heapCell[i]; heapCell[i] = tc;
                float tf = heapCost[m]; heapCost[m] = heapCost[i]; heapCost[i] = tf;
                i = m;
            }
            return top;
        };

        push(start, 0f);
        var done = new bool[n * n];
        bool found = false;

        int[] dR = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dC = { 0, 0, 1, -1, 1, -1, 1, -1 };
        float[] cost = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

        while (heapN > 0)
        {
            int v = pop();
            if (done[v]) continue;
            done[v] = true;
            if (v == target) { found = true; break; }
            int r = v / n, c = v % n;
            for (int i = 0; i < 8; i++)
            {
                int rr = r + dR[i], cc = c + dC[i];
                if (rr < 0 || rr >= n || cc < 0 || cc >= n) continue;
                if (!Walkable[rr, cc]) continue;
                int w = rr * n + cc;
                if (done[w]) continue;
                float nd = dist[v] + cost[i];
                if (nd < dist[w]) { dist[w] = nd; prev[w] = v; push(w, nd); }
            }
        }
        if (!found) return empty;

        var path = new List<int>();
        for (int v = target; v != -1; v = prev[v]) { path.Add(v / n); path.Add(v % n); }
        path.Reverse();                            // reversed pairs, see below
        // Reverse() on the flat list swaps the order within each pair too, so
        // put them back.
        for (int i = 0; i + 1 < path.Count; i += 2)
        {
            int t = path[i]; path[i] = path[i + 1]; path[i + 1] = t;
        }
        return path;
    }

    static bool SnapToWalkable(ref int r, ref int c)
    {
        int n = HRes;
        for (int rad = 1; rad <= 12; rad++)
            for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (rr < 0 || rr >= n || cc < 0 || cc >= n) continue;
                    if (!Walkable[rr, cc]) continue;
                    r = rr; c = cc; return true;
                }
        return false;
    }

    /// Structure of the route between two spawns.
    ///
    /// Returns, in order:
    ///   0  route length in metres
    ///   1  directness: straight-line distance / route length, 1 = a straight
    ///      shot, lower means the terrain forces a detour
    ///   2  median clearance in metres along the route
    ///   3  minimum clearance in metres
    ///   4  chokepoints: sustained pinches, entered below 60% of the route
    ///      median and not left until it opens past 90%
    ///   5  fraction of the route overlooked by ground at least 8 m higher
    ///      within 45 m
    /// All zero if there is no route.
    public static float[] RouteStats(float ax, float az, float bx, float bz)
    {
        var zero = new float[] { 0, 0, 0, 0, 0, 0 };
        var path = RouteCells(ax, az, bx, bz);
        if (path.Count < 4) return zero;

        int n = HRes;
        float step = MapSize / (n - 1);
        var clear = ClearanceField();

        int cells = path.Count / 2;
        float length = 0f;
        var widths = new List<float>(cells);
        for (int i = 0; i < cells; i++)
        {
            int r = path[i * 2], c = path[i * 2 + 1];
            widths.Add(clear[r, c]);
            if (i > 0)
            {
                int pr = path[(i - 1) * 2], pc = path[(i - 1) * 2 + 1];
                length += (pr != r && pc != c) ? step * 1.41421356f : step;
            }
        }

        // Trim the ends. Clearance right at a spawn measures the base, not the
        // lane - a commander on a peninsula scores 3 m and tells us nothing
        // about the route. Drop the first and last tenth.
        int lo = cells / 10, hi = cells - cells / 10;
        if (hi - lo < 8) { lo = 0; hi = cells; }
        var lane = widths.GetRange(lo, hi - lo);

        float straight = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        var sorted = new List<float>(lane);
        sorted.Sort();
        float median = sorted[sorted.Count / 2];
        float min = sorted[0];

        // Chokepoints, with hysteresis and a minimum length. Counting every
        // cell under a threshold turns one long pass into dozens of pinches,
        // and a bare threshold flickers on any profile that wobbles across it.
        // A pinch starts below 60% of the median, has to last a few cells, and
        // does not end until the route opens back past 90%.
        float enter = median * 0.6f, exit = median * 0.9f;
        int chokes = 0, run = 0;
        bool inDip = false, counted = false;
        foreach (float w in lane)
        {
            if (!inDip)
            {
                if (w < enter) { inDip = true; run = 1; counted = false; }
            }
            else
            {
                run++;
                if (!counted && run >= 3) { chokes++; counted = true; }
                if (w > exit) inDip = false;
            }
        }

        // High ground: sample a ring around each route cell and ask whether
        // anything nearby is meaningfully above it. This is what makes a lane
        // feel like a valley rather than a corridor drawn on a plain.
        int look = Math.Max(2, (int)(45f / step));
        int overlooked = 0;
        for (int i = 0; i < cells; i += 2)                 // every other cell is plenty
        {
            int r = path[i * 2], c = path[i * 2 + 1];
            float h = Height[r, c];
            bool above = false;
            for (int a = 0; a < 12 && !above; a++)
            {
                double ang = a * Math.PI / 6.0;
                int rr = r + (int)Math.Round(Math.Sin(ang) * look);
                int cc = c + (int)Math.Round(Math.Cos(ang) * look);
                if (rr < 0 || rr >= n || cc < 0 || cc >= n) continue;
                if (Height[rr, cc] - h >= 8f) above = true;
            }
            if (above) overlooked++;
        }
        int sampled = (cells + 1) / 2;

        return new[]
        {
            length,
            length > 0 ? straight / length : 0f,
            median,
            min,
            (float)chokes,
            sampled > 0 ? (float)overlooked / sampled : 0f,
        };
    }

    /// How much of the land sits on raised ground, as a rough read on whether a
    /// map has tiers at all or is one flat sheet. Returns the fraction of land
    /// cells more than `rise` metres above the land median.
    public static float PlateauFraction(float rise)
    {
        var land = new List<float>();
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
                if (Height[r, c] > WaterLevel) land.Add(Height[r, c]);
        if (land.Count == 0) return 0f;
        land.Sort();
        float med = land[land.Count / 2];
        int hi = 0;
        foreach (float h in land) if (h - med > rise) hi++;
        return (float)hi / land.Count;
    }

    /// Fraction of the map under water.
    public static float WaterFraction()
    {
        int wet = 0;
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
                if (Height[r, c] <= WaterLevel) wet++;
        return (float)wet / (HRes * HRes);
    }
}
