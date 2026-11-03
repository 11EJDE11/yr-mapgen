using MapGen.Model;
using MapGen.Terrain;

namespace MapGen.Generation;

/// <summary>
/// The terraced terrain foundation: the map is divided into a coarse grid of blocks, each block is
/// either lowland or a raised terrace, and the borders between them become long cliff lines with ramp
/// gaps where the routes cross. Bases sit on whichever level their region has (on highland maps the
/// whole base is raised). The block grid is centred on the map so every symmetry maps it onto itself:
/// with no asymmetry the terraces are exact copies, with asymmetry they drift like everything else.
/// </summary>
internal sealed partial class GenerationRun
{
    /// <summary>Block edge length in cells. At least 14 so one ramp gap (8 cells plus end pieces) fits on a single edge.</summary>
    private const int Block = 14;

    private enum TerraceShape
    {
        /// <summary>Raised ground from noise alone; bases on the lowland.</summary>
        Rolling,
        /// <summary>Bases and the ground round them raised, lowland between the sides.</summary>
        Highland,
        /// <summary>The centre raised, bases on the lowland.</summary>
        Mesa,
    }

    private TerraceShape terraceShape;
    private bool terraceTestGaps;

    // Block grid: block (i, j) covers cells blockOrigin + i*Block .. +Block-1 in each axis.
    private int blockOrigin, blockCount;
    private bool[,] raised;
    private bool[,] forcedBase;

    /// <summary>Per cell: whether a terrace border runs within a few cells (lakes keep off it).</summary>
    private bool[,] nearTerraceEdge;

    private (int I, int J) BlockOf(CellPos p) => ((int)Math.Floor((p.X - blockOrigin) / (double)Block), (int)Math.Floor((p.Y - blockOrigin) / (double)Block));

    private CellPos BlockCorner(int i, int j) => new(blockOrigin + i * Block, blockOrigin + j * Block);

    private CellPos BlockCentre(int i, int j) => new(blockOrigin + i * Block + Block / 2, blockOrigin + j * Block + Block / 2);

    private bool IsRaisedBlock(int i, int j) => i >= 0 && j >= 0 && i < blockCount && j < blockCount && raised[i, j];

    private bool IsRaisedCell(CellPos p)
    {
        var (i, j) = BlockOf(p);
        return IsRaisedBlock(i, j);
    }

    // ---------------------------------------------------------------- the level map

    /// <summary>
    /// Decides which blocks are raised. <paramref name="amount"/> (the Cliffs slider) sets how much of
    /// the map is terrace; the shape decides where it tends to be.
    /// </summary>
    private void PlanTerraces(double amount)
    {
        int c = map.Width + map.Height;
        // Grid lines at c/2 + k*Block: c is even, so the centre line is a grid line and mirrors and
        // rotations about the centre map the grid onto itself.
        int k = (c / 2) / Block + 1;
        blockOrigin = c / 2 - k * Block;
        blockCount = 2 * k + 1;
        raised = new bool[blockCount, blockCount];

        int seed = rng.Next();
        // Of the blocks that are free to be either; bases and the centre take a good part of small maps.
        double share = 0.2 + 0.4 * Math.Clamp(amount, 0, 1);

        // Blocks whose cells all lie inside the playable area (with room for the cliff outside them).
        var usableBlock = new bool[blockCount, blockCount];
        var score = new double[blockCount, blockCount];
        var starts = players.Select(p => p.Position).ToList();
        var centre = MapCentre();
        double reach = starts.Min(s => s.DistanceTo(centre));

        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
            {
                // Whole blocks inside the playable area, with room for the cliff outside them: terraces
                // never touch the map edge (a cliff in the margin could not take a ramp).
                bool ok = true;
                for (int dx = 0; dx < Block && ok; dx++)
                    for (int dy = 0; dy < Block && ok; dy++)
                        ok = map.IsInLocal(new CellPos(blockOrigin + i * Block + dx, blockOrigin + j * Block + dy), 1);
                usableBlock[i, j] = ok;
                if (!ok)
                    continue;

                // Noise on the canonical block (drifted with asymmetry) so copies match.
                var bc = BlockCentre(i, j);
                var cc = sym.Canonical(bc);
                var (ddx, ddy) = Drift(bc);
                double sx = cc.X + 2 * ddx, sy = cc.Y + 2 * ddy;
                double n = 0.7 * ValueNoise(sx / 55.0, sy / 55.0, seed) + 0.3 * ValueNoise(sx / 20.0, sy / 20.0, seed + 1);

                double nearestStart = starts.Min(s => s.DistanceTo(bc));
                // Highland: a shelf is the base plus about one ring of blocks round it; further out the
                // noise may not raise anything, so shelves stay separate and the lowland stays open.
                double shelfReach = BaseRadius + 8 + Block * 0.6;
                double bias = terraceShape switch
                {
                    TerraceShape.Highland => nearestStart - Block * 0.7 < shelfReach ? 0.3 : -2,
                    TerraceShape.Mesa => 0.35 * Math.Clamp(1 - bc.DistanceTo(centre) / (reach * 0.7), -1, 1),
                    _ => 0,
                };
                score[i, j] = n + bias;
            }

        // The threshold is taken from the distribution (over the blocks that are free to be either)
        // so the slider means the same on every map.
        // A block is forced when any of its cells is within reach of a base (so the cliff, whose tiles
        // reach a few cells past the block border, clears the base zone and its margin) or of the
        // centre. Measured to the block's nearest cell, so shelves stay as tight as they can.
        const double BaseReach = 7;
        double BlockDistance(int i, int j, CellPos p)
        {
            var o = BlockCorner(i, j);
            double dx = Math.Max(0, Math.Max(o.X - p.X, p.X - (o.X + Block - 1)));
            double dy = Math.Max(0, Math.Max(o.Y - p.Y, p.Y - (o.Y + Block - 1)));
            return Math.Sqrt(dx * dx + dy * dy);
        }
        bool ForcedByBase(int i, int j) => starts.Min(s => BlockDistance(i, j, s)) < BaseRadius + BaseReach;
        bool Forced(int i, int j) => ForcedByBase(i, j) || BlockDistance(i, j, centre) < 8;
        var values = new List<double>();
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
                if (usableBlock[i, j] && !Forced(i, j))
                    values.Add(score[i, j]);
        values.Sort();
        double threshold = values.Count == 0 ? double.MaxValue : values[(int)Math.Clamp((1 - share) * (values.Count - 1), 0, values.Count - 1)];

        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
                raised[i, j] = usableBlock[i, j] && score[i, j] >= threshold;
        bool debugLevels = Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1";
        int Count() { int c2 = 0; for (int i = 0; i < blockCount; i++) for (int j = 0; j < blockCount; j++) if (raised[i, j]) c2++; return c2; }
        if (debugLevels)
            Console.Error.WriteLine($"TERRACE levels: {values.Count} free blocks, share {share:0.00}, raised by noise {Count()}");

        // Bases: the whole base disk on one level (raised on highland maps, lowland otherwise), with
        // room for the cliff to clear the base zone. The centre stays one level too.
        bool basesRaised = terraceShape == TerraceShape.Highland;
        bool centreRaised = terraceShape == TerraceShape.Mesa;
        // Highland: a lowland valley between the sides, so each team holds its own shelf and the
        // fight for the middle happens below the cliffs.
        bool InValley(int i, int j)
        {
            if (!basesRaised || starts.Count < 2)
                return false;
            // Between the two nearest players: every base gets a shelf of its own, teammates included,
            // so a shelf is never a third of the map.
            var bc = BlockCentre(i, j);
            // Two blocks wide: each shelf's cliff reaches several cells past its border.
            var nearest = starts.Select(s => s.DistanceTo(bc)).OrderBy(d => d).ToList();
            return nearest[1] - nearest[0] < Block * 2.1;
        }
        forcedBase = new bool[blockCount, blockCount];
        void ForceLevels()
        {
            for (int i = 0; i < blockCount; i++)
                for (int j = 0; j < blockCount; j++)
                {
                    if (!usableBlock[i, j])
                        continue;
                    if (ForcedByBase(i, j))
                    {
                        raised[i, j] = basesRaised;
                        forcedBase[i, j] = basesRaised;
                    }
                    else if (Forced(i, j))
                        raised[i, j] = centreRaised;
                    else if (InValley(i, j))
                        raised[i, j] = false;
                }
        }
        ForceLevels();

        // Clean up: fill one-block gaps, then drop one-block spurs and specks, so every border run is at
        // least two blocks long and no two terraces touch at a corner.
        if (debugLevels)
            Console.Error.WriteLine($"TERRACE levels: after forcing bases/centre {Count()}");
        Close();
        if (debugLevels)
            Console.Error.WriteLine($"TERRACE levels: after close {Count()}");
        Open();
        if (debugLevels)
            Console.Error.WriteLine($"TERRACE levels: after open {Count()}");
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
                if (!usableBlock[i, j])
                    raised[i, j] = false;
        // The clean-up may have crept back over a base or the centre: those cells are forced once more.
        ForceLevels();
        DropSmallRegions(minBlocks: 4);
        SeparateRegions();
        DropSmallRegions(minBlocks: 4);
        if (debugLevels)
            Console.Error.WriteLine($"TERRACE levels: after dropping small regions {Count()}");

        // Debugging aid: one square terrace at the centre, to check the loop and ramp conventions.
        string test = Environment.GetEnvironmentVariable("MAPGEN_TERRACE_TEST");
        if (test != null)
        {
            raised = new bool[blockCount, blockCount];
            var (ci, cj) = BlockOf(centre);
            for (int i = ci - 1; i <= ci + 1; i++)
                for (int j = cj - 1; j <= cj + 1; j++)
                    raised[i, j] = true;
            terraceTestGaps = test == "gaps";
        }

        nearTerraceEdge = new bool[map.Span, map.Span];
        foreach (var p in map.AllValidCells())
        {
            bool r = IsRaisedCell(p);
            for (int dx = -5; dx <= 5 && !nearTerraceEdge[p.X, p.Y]; dx++)
                for (int dy = -5; dy <= 5; dy++)
                {
                    var q = new CellPos(p.X + dx, p.Y + dy);
                    if (map.IsValid(q) && IsRaisedCell(q) != r)
                    {
                        nearTerraceEdge[p.X, p.Y] = true;
                        break;
                    }
                }
        }
    }

    private void Close()
    {
        var dilated = Morph(raised, true);
        raised = Morph(dilated, false);
    }

    private void Open()
    {
        var eroded = Morph(raised, false);
        raised = Morph(eroded, true);
    }

    /// <summary>
    /// Morphology with a 2x2 window anchored at its top-left block. Erosion keeps a block only when
    /// the window at it is fully set; dilation sets a block when any window covering it is set. Open
    /// (erode, dilate) removes one-block spurs and specks; close (dilate, erode) fills one-block gaps.
    /// </summary>
    private bool[,] Morph(bool[,] src, bool dilate)
    {
        bool At(int x, int y) => x >= 0 && y >= 0 && x < blockCount && y < blockCount && src[x, y];
        var dst = new bool[blockCount, blockCount];
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
                dst[i, j] = dilate
                    ? At(i, j) || At(i - 1, j) || At(i, j - 1) || At(i - 1, j - 1)
                    : At(i, j) && At(i + 1, j) && At(i, j + 1) && At(i + 1, j + 1);
        return dst;
    }

    private List<List<(int I, int J)>> RaisedRegions()
    {
        var seen = new bool[blockCount, blockCount];
        var regions = new List<List<(int I, int J)>>();
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
            {
                if (!raised[i, j] || seen[i, j])
                    continue;
                var region = new List<(int I, int J)>();
                var queue = new Queue<(int I, int J)>();
                queue.Enqueue((i, j));
                seen[i, j] = true;
                while (queue.Count > 0)
                {
                    var (a, b) = queue.Dequeue();
                    region.Add((a, b));
                    foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int x = a + da, y = b + db;
                        if (x < 0 || y < 0 || x >= blockCount || y >= blockCount || seen[x, y] || !raised[x, y])
                            continue;
                        seen[x, y] = true;
                        queue.Enqueue((x, y));
                    }
                }
                regions.Add(region);
            }
        return regions;
    }

    /// <summary>
    /// Regions need two blocks of lowland between them: each cliff reaches several cells past its
    /// border, and a ramp needs its foot. Blocks nearer than that to another region are dropped on
    /// both sides (symmetric, so mirrored regions stay mirrored), base blocks excepted.
    /// </summary>
    private void SeparateRegions()
    {
        var regions = RaisedRegions();
        var owner = new int[blockCount, blockCount];
        for (int r = 0; r < regions.Count; r++)
            foreach (var (i, j) in regions[r])
                owner[i, j] = r + 1;
        var drop = new List<(int I, int J)>();
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
            {
                if (owner[i, j] == 0)
                    continue;
                for (int di = -2; di <= 2; di++)
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        int x = i + di, y = j + dj;
                        if (x >= 0 && y >= 0 && x < blockCount && y < blockCount && owner[x, y] != 0 && owner[x, y] != owner[i, j] && !forcedBase[i, j])
                            drop.Add((i, j));
                    }
            }
        foreach (var (i, j) in drop)
            raised[i, j] = false;
    }

    /// <summary>Blocks that are not raised but enclosed by the region: not reachable from the grid border through non-raised blocks.</summary>
    private HashSet<(int I, int J)> HoleBlocks(List<(int I, int J)> region)
    {
        var inRegion = region.ToHashSet();
        var outside = new HashSet<(int I, int J)>();
        var queue = new Queue<(int I, int J)>();
        for (int i = 0; i < blockCount; i++)
            foreach (var j in new[] { 0, blockCount - 1 })
                foreach (var b in new[] { (i, j), (j, i) })
                    if (!inRegion.Contains(b) && outside.Add(b))
                        queue.Enqueue(b);
        while (queue.Count > 0)
        {
            var (a, b) = queue.Dequeue();
            foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (a + da, b + db);
                if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= blockCount || n.Item2 >= blockCount || inRegion.Contains(n) || !outside.Add(n))
                    continue;
                queue.Enqueue(n);
            }
        }
        var holes = new HashSet<(int I, int J)>();
        for (int i = 0; i < blockCount; i++)
            for (int j = 0; j < blockCount; j++)
                if (!inRegion.Contains((i, j)) && !outside.Contains((i, j)))
                    holes.Add((i, j));
        return holes;
    }

    private void DropSmallRegions(int minBlocks)
    {
        foreach (var region in RaisedRegions())
            if (region.Count < minBlocks)
                foreach (var (i, j) in region)
                    raised[i, j] = false;
    }

    // ---------------------------------------------------------------- cliffs along the borders

    /// <summary>
    /// Traces every terrace border as a loop of block corners, cuts ramp gaps where the routes cross
    /// (and a few more), and lays the cliffs with the connected-tile planner. A region whose border
    /// cannot be built is flattened.
    /// </summary>
    private void BuildTerraceCliffs(ConnectedTileType cliffType)
    {
        bool debug = Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1";
        bool ramps = cliffType.IniName == "CliffSet";
        if (debug)
        {
            Console.Error.WriteLine($"TERRACE map {terraceShape}, block {Block}, {blockCount}x{blockCount} blocks");
            for (int j = 0; j < blockCount; j++)
            {
                var line = new System.Text.StringBuilder();
                for (int i = 0; i < blockCount; i++)
                {
                    var bc = BlockCentre(i, j);
                    bool start = players.Any(pl => BlockOf(pl.Position) == (i, j));
                    line.Append(!map.IsValid(bc) ? ' ' : start ? (raised[i, j] ? 'S' : 's') : raised[i, j] ? '#' : '.');
                }
                Console.Error.WriteLine(line.ToString());
            }
        }

        foreach (var region in RaisedRegions())
        {
            var loops = TraceLoops(region);
            var cells = new HashSet<CellPos>();
            foreach (var (i, j) in region)
                for (int dx = 0; dx < Block; dx++)
                    for (int dy = 0; dy < Block; dy++)
                        cells.Add(new CellPos(blockOrigin + i * Block + dx, blockOrigin + j * Block + dy));

            bool hasBase = players.Any(p => cells.Contains(p.Position));
            if (!TryBuildTerrace(cliffType, region, loops, cells, hasBase, ramps, out string why))
            {
                if (debug)
                    Console.Error.WriteLine($"TERRACE {region.Count} blocks (base {hasBase}): FAILED {why}");
                foreach (var (i, j) in region)
                    raised[i, j] = false;
                // The mirror of this region may already stand: flattening one side only would be unfair.
                if (hasBase || region.Count >= 6)
                    throw new GenerationFailedException(hasBase ? "Could not build the cliffs round a raised base" : "Could not build a terrace");
            }
            else if (debug)
                Console.Error.WriteLine($"TERRACE {region.Count} blocks (base {hasBase}): ok, {loops.Count} loop(s)");
        }
    }

    /// <summary>A border loop: block corners in order, with the raised side on the left of travel (in cell space, which the screen preserves).</summary>
    private List<List<(int I, int J)>> TraceLoops(List<(int I, int J)> region)
    {
        var inRegion = region.ToHashSet();
        // Directed unit edges between block corners with the region on the left.
        var outEdges = new Dictionary<(int I, int J), List<(int I, int J)>>();
        void Edge((int I, int J) a, (int I, int J) b)
        {
            if (!outEdges.TryGetValue(a, out var list))
                outEdges[a] = list = new List<(int I, int J)>();
            list.Add(b);
        }
        foreach (var (i, j) in region)
        {
            // Travelling with the block on the left: left normal of direction (dx, dy) is (-dy, dx).
            if (!inRegion.Contains((i, j + 1))) Edge((i + 1, j + 1), (i, j + 1));   // bottom side (y = j+1), block above: direction (-1,0), left (0,-1)
            if (!inRegion.Contains((i, j - 1))) Edge((i, j), (i + 1, j));           // top side (y = j), block below: direction (1,0), left (0,1)
            if (!inRegion.Contains((i + 1, j))) Edge((i + 1, j), (i + 1, j + 1));   // right side (x = i+1), block left: direction (0,1), left (-1,0)
            if (!inRegion.Contains((i - 1, j))) Edge((i, j + 1), (i, j));           // left side (x = i), block right: direction (0,-1), left (1,0)
        }

        var loops = new List<List<(int I, int J)>>();
        var used = new HashSet<((int I, int J) A, (int I, int J) B)>();
        foreach (var startCorner in outEdges.Keys.OrderBy(k => k.J).ThenBy(k => k.I).ToList())
        {
            foreach (var first in outEdges[startCorner])
            {
                if (used.Contains((startCorner, first)))
                    continue;
                var loop = new List<(int I, int J)> { startCorner };
                var prev = startCorner;
                var cur = first;
                used.Add((prev, cur));
                int guard = 0;
                while (cur != startCorner && guard++ < 10000)
                {
                    loop.Add(cur);
                    var dir = (cur.I - prev.I, cur.J - prev.J);
                    // Prefer the sharpest left turn so touching regions are kept apart.
                    var options = outEdges.TryGetValue(cur, out var list) ? list.Where(n => !used.Contains((cur, n))).ToList() : new List<(int I, int J)>();
                    if (options.Count == 0)
                        break;
                    var next = options.OrderByDescending(n => TurnScore(dir, (n.I - cur.I, n.J - cur.J))).First();
                    used.Add((cur, next));
                    prev = cur;
                    cur = next;
                }
                // Traced with the region on the left in coordinate terms. The cliff planner raises the
                // side on the right of travel (counter-clockwise as seen on screen), so reverse.
                if (cur == startCorner && loop.Count >= 4)
                {
                    loop.Reverse();
                    loops.Add(MergeCollinear(loop));
                }
            }
        }
        return loops;
    }

    /// <summary>Higher for a left turn, lowest for a right turn (cross product in cell space).</summary>
    private static int TurnScore((int X, int Y) a, (int X, int Y) b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// Block corners outside the valid map are pulled in towards the centre until they are on it, so
    /// a loop that straddles the map edge closes through the margin. Repeats are dropped.
    /// </summary>
    private List<CellPos> ClampToMap(List<CellPos> vertices)
    {
        var centre = MapCentre();
        var result = new List<CellPos>();
        foreach (var v0 in vertices)
        {
            var v = v0;
            for (int step = 0; step < 30 && !map.IsValid(v); step++)
                v = new CellPos(v.X + Math.Sign(centre.X - v.X), v.Y + Math.Sign(centre.Y - v.Y));
            if (!map.IsValid(v))
                continue;
            if (result.Count == 0 || result[^1] != v)
                result.Add(v);
        }
        if (result.Count > 1 && result[0] == result[^1])
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static List<(int I, int J)> MergeCollinear(List<(int I, int J)> loop)
    {
        var result = new List<(int I, int J)>();
        int n = loop.Count;
        for (int k = 0; k < n; k++)
        {
            var p = loop[(k + n - 1) % n];
            var c = loop[k];
            var q = loop[(k + 1) % n];
            bool straight = (c.I - p.I) * (q.J - c.J) - (c.J - p.J) * (q.I - c.I) == 0;
            if (!straight)
                result.Add(c);
        }
        return result;
    }

    /// <summary>A cell of the region well inside it (nearest its centroid), avoiding the boundary cells.</summary>
    private static CellPos InnerCell(HashSet<CellPos> cells, HashSet<CellPos> boundary)
    {
        double cx = cells.Average(c => c.X), cy = cells.Average(c => c.Y);
        return cells.Where(c => !boundary.Contains(c)).OrderBy(c => (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)).FirstOrDefault();
    }

    private sealed class TerraceGap
    {
        public int Run;            // index of the loop vertex the run starts at
        public CellPos A, B;       // the two gap ends along the run, in travel order
        public RampEdge Edge;
        public HashSet<CellPos> Ramp, Foot;
    }

    private bool TryBuildTerrace(ConnectedTileType cliffType, List<(int I, int J)> region, List<List<(int I, int J)>> loops,
        HashSet<CellPos> cells, bool hasBase, bool ramps, out string why)
    {
        why = "";
        bool reversed = cliffLoopReversed.GetValueOrDefault(cliffType.IniName);
        var planSeed = rng.Next();
        var allCliff = new HashSet<CellPos>();
        var allRamp = new HashSet<CellPos>();
        var allFoot = new HashSet<CellPos>();
        var plans = new List<(ConnectedTilePlan Plan, bool HasRamps)>();
        int gapCount = 0;

        // Lowland pockets enclosed by the region count as inside for the leak test.
        var holes = HoleBlocks(region);
        bool NearRaised(CellPos p)
        {
            var (bi, bj) = BlockOf(p);
            if (holes.Contains((bi, bj)))
                return true;
            // Cliff pieces at a concave corner reach up to eight cells past the block border.
            for (int dx = -9; dx <= 9; dx++)
                for (int dy = -9; dy <= 9; dy++)
                    if (cells.Contains(new CellPos(p.X + dx, p.Y + dy)))
                        return true;
            return false;
        }
        int holeCells = holes.Count * Block * Block;

        // Outer loop first, so the flood that checks each later loop is already bounded by it.
        foreach (var loop in loops.OrderByDescending(l => l.Count == 0 ? 0 : l.Max(v => v.I) - l.Min(v => v.I) + l.Max(v => v.J) - l.Min(v => v.J)))
        {
            var vertices = ClampToMap(loop.Select(v => BlockCorner(v.I, v.J)).ToList());
            if (vertices.Count < 3)
            {
                why = "loop collapsed at the map edge";
                return false;
            }
            // The same clearance the cliff planner demands, so an accepted gap never blocks its stretch.
            bool GapRoom(CellPos p) => map.IsValid(p) && map.IsInLocal(p, 0) && zone[p.X, p.Y] == Zone.Free && !allCliff.Contains(p) && !allRamp.Contains(p) && !NearProtected(p, 2);
            var gaps = ramps ? ChooseGaps(vertices, cells, hasBase, GapRoom) : new List<TerraceGap>();

            bool Valid(CellPos p) => map.IsValid(p) && zone[p.X, p.Y] == Zone.Free && !allCliff.Contains(p)
                                     && !allRamp.Contains(p) && !allFoot.Contains(p) && !NearProtected(p, 2);

            var planned = new List<(ConnectedTilePlan Plan, bool HasRamps)>();
            bool ok = false;
            // If the stretches between the gaps cannot be built, try other gaps, then (for a region
            // nobody starts on) no gaps at all.
            var excluded = new HashSet<int>();
            for (int gapTry = 0; gapTry < 4 && !ok; gapTry++)
            {
            if (gapTry > 0)
            {
                foreach (var g in gaps)
                    excluded.Add(g.Run);
                gaps = ramps && (gapTry < 3 || hasBase) ? ChooseGaps(vertices, cells, hasBase, GapRoom, excluded) : new List<TerraceGap>();
                if (gaps.Count == 0 && hasBase)
                    break;
            }
            // Slope pieces only fit the traced direction, so only closed loops may be tried the other way round.
            int orientations = gaps.Count > 0 ? 1 : 2;
            for (int orientation = 0; orientation < orientations && !ok; orientation++)
            {
                bool rev = reversed != (orientation == 1);
                planned.Clear();
                var verts = rev ? Enumerable.Reverse(vertices).ToList() : vertices;
                // Reversed, vertex k sits at index (n - k) % n, so run r (from k = r to r + 1) becomes the run starting at n - 1 - r.
                var gs = rev ? gaps.Select(g => new TerraceGap { Run = (2 * vertices.Count - 1 - g.Run) % vertices.Count, A = g.B, B = g.A, Edge = g.Edge, Ramp = g.Ramp, Foot = g.Foot }).ToList() : gaps;
                if (!PlanLoop(cliffType, verts, gs, planSeed + orientation, Valid, cells, region, planned, out why))
                {
                    if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                        Console.Error.WriteLine($"    orientation {(rev ? "reversed" : "as traced")}: {why}");
                    continue;
                }

                // Sealed, and the right way round: the flood from a raised cell, bounded by every cliff
                // so far, must stay by the region, and no cliff foot may touch it.
                var loopCliff = planned.SelectMany(pl => FoundationCells(pl.Plan.Placements)).ToHashSet();
                var boundary = allCliff.Concat(allRamp).Concat(loopCliff).Concat(gs.SelectMany(g => g.Ramp)).ToHashSet();
                var seedCell = InnerCell(cells, boundary);
                var flooded = FloodInside(seedCell, boundary, cells.Count + holeCells + 40);
                if (flooded == null || flooded.Any(p => !NearRaised(p)))
                {
                    why = flooded == null ? "flood ran away" : "leaks past the border";
                    if (flooded != null && Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    {
                        var leaks = flooded.Where(p => !NearRaised(p)).Take(4).ToList();
                        Console.Error.WriteLine($"    leak: {flooded.Count} flooded of {cells.Count} region cells; e.g. " + string.Join(" ", leaks.Select(l =>
                            $"{l} (raised {cells.Min(c => c.DistanceTo(l)):0.#} away, cliff {boundary.Min(c => c.DistanceTo(l)):0.#} away)")));
                    }
                    continue;
                }
                // Slope pieces mix low ramp cells with the cliff ends, so they are left out of the check.
                int wrong = 0;
                foreach (var (plan, hasRamps) in planned)
                {
                    var cliffOnly = hasRamps
                        ? new ConnectedTilePlan(plan.Type, plan.Origin, plan.Placements.Where(pl => !CliffRamps.IsRampPiece(pl.Tile)).ToList(), isClosed: false, usesEndPieces: false)
                        : plan;
                    wrong += CountWrongWayCliffCells(cliffOnly, flooded, loopCliff);
                }
                if (wrong > 0)
                {
                    why = "wrong way";
                    continue;
                }
                cliffLoopReversed[cliffType.IniName] = rev;
                reversed = rev;
                ok = true;
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    Console.Error.WriteLine($"  loop of {vertices.Count} corners, {gaps.Count} gap(s): ok {(rev ? "reversed" : "as traced")}");
            }
            }
            gapCount += gaps.Count;
            if (!ok)
            {
                // Debugging aid: lay the last attempt anyway so it can be looked at in a render.
                if (Environment.GetEnvironmentVariable("MAPGEN_KEEP_FAILED") == "1")
                    foreach (var (plan, _) in planned)
                        foreach (var p in CliffBuilder.Apply(map, theater, plan, 0, new Random(planSeed)))
                        {
                            tilePlaced[p.X, p.Y] = true;
                            zone[p.X, p.Y] = Zone.Cliff;
                        }
                return false;
            }

            foreach (var pl in planned)
                allCliff.UnionWith(FoundationCells(pl.Plan.Placements));
            foreach (var g in gaps)
            {
                allRamp.UnionWith(g.Ramp);
                allFoot.UnionWith(g.Foot);
            }
            plans.AddRange(planned);
        }

        // The whole region, now bounded by all of its loops.
        var topSeed = InnerCell(cells, allCliff.Concat(allRamp).ToHashSet());
        var top = FloodInside(topSeed, allCliff.Concat(allRamp).ToHashSet(), cells.Count + holeCells + 40);
        if (top == null || top.Any(p => !NearRaised(p)))
        {
            why = "region not sealed";
            return false;
        }
        var inside = top;

        // Apply.
        foreach (var (plan, _) in plans)
            foreach (var p in CliffBuilder.Apply(map, theater, plan, 0, new Random(planSeed)))
            {
                tilePlaced[p.X, p.Y] = true;
                var sub = theater.Tile(map[p].TileIndex)?.GetSubTile(map[p].SubTile);
                zone[p.X, p.Y] = sub != null && sub.TerrainType is 7 or 8 or 15 ? Zone.Cliff : Zone.Open;
            }
        bool reachable = gapCount > 0;
        foreach (var p in inside)
        {
            if (tilePlaced[p.X, p.Y])
                continue;
            targetLevel[p.X, p.Y] = PlateauHeight;
            if (zone[p.X, p.Y] == Zone.Free)
                zone[p.X, p.Y] = reachable ? Zone.Free : Zone.CliffTop;
        }
        foreach (var p in allRamp)
        {
            targetLevel[p.X, p.Y] = PlateauHeight;
            if (zone[p.X, p.Y] == Zone.Free)
                zone[p.X, p.Y] = Zone.Open;
        }
        foreach (var p in allFoot)
            if (map.IsValid(p) && zone[p.X, p.Y] == Zone.Free)
                zone[p.X, p.Y] = Zone.Open;
        foreach (var p in allCliff)
            foreach (var q in Disk(p, 1.5))
                if (map.IsValid(q) && zone[q.X, q.Y] == Zone.Free && !inside.Contains(q))
                    zone[q.X, q.Y] = Zone.Open;
        return true;
    }

    /// <summary>
    /// Plans one loop: closed if it has no gaps, otherwise one open cliff per stretch between gaps,
    /// each beginning and ending in a slope piece.
    /// </summary>
    private bool PlanLoop(ConnectedTileType cliffType, List<CellPos> vertices, List<TerraceGap> gaps, int seed, Func<CellPos, bool> valid,
        HashSet<CellPos> cells, List<(int I, int J)> regionBlocks, List<(ConnectedTilePlan Plan, bool HasRamps)> planned, out string why)
    {
        why = "";
        int n = vertices.Count;
        if (gaps.Count == 0)
        {
            if (!vertices.All(valid))
            {
                why = "vertex blocked";
                return false;
            }
            // The planner occasionally leaves a one-cell hole at the seam: retry from other corners.
            ConnectedTilePlanResult result = null;
            for (int retry = 0; retry < Math.Min(6, n) ; retry++)
            {
                var rotated = vertices.Skip(retry).Concat(vertices.Take(retry)).ToList();
                result = ConnectedTilePlanner.Plan(cliffType, rotated, ConnectedTileSide.Front, seed + retry * 101, useEndPieces: false, closed: true, valid, strictExpansionLimit: 400_000);
                if (!result.Success)
                    continue;
                var cliff = FoundationCells(result.Plan.Placements);
                var seedCell = InnerCell(cells, cliff);
                // Tight limit: a leak that stays small would otherwise pass as sealed.
                var flooded = FloodInside(seedCell, cliff, cells.Count + HoleBlocks(regionBlocks).Count * Block * Block + 40);
                int wrongWay = flooded == null ? -1 : CountWrongWayCliffCells(result.Plan, flooded, cliff);
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    Console.Error.WriteLine($"    closed loop from corner {retry}: {(flooded == null ? "leaks" : wrongWay == 0 ? "ok" : wrongWay + " wrong-way cells")}");
                if (flooded != null && wrongWay == 0)
                    break;
                result = null;
            }
            if (result == null || !result.Success)
            {
                why = "closed loop of " + vertices.Count + " points: " + (result == null ? "not sealed" : result.Status.ToString());
                return false;
            }
            planned.Add((result.Plan, false));
            return true;
        }

        // At most one gap per run, so the stretches follow the run order.
        var ordered = gaps.OrderBy(g => g.Run).ToList();
        for (int g = 0; g < ordered.Count; g++)
        {
            var from = ordered[g];
            var to = ordered[(g + 1) % ordered.Count];
            // From the far end of this gap, through every corner up to the run of the next gap, to
            // its near end. With a single gap that is the whole loop.
            var path = new List<CellPos> { from.B };
            int run = from.Run;
            do
            {
                run = (run + 1) % n;
                path.Add(vertices[run]);
            } while (run != to.Run);
            path.Add(to.A);
            if (!path.All(valid) || !from.Ramp.All(valid) || !from.Foot.All(valid))
            {
                var bad = path.Concat(from.Ramp).Concat(from.Foot).First(c => !valid(c));
                string near = string.Join(",", Disk(bad, 2).Where(map.IsValid).Select(q => zone[q.X, q.Y]).Where(z => z != Zone.Free).Distinct());
                why = "stretch blocked at " + bad + ": " + (!map.IsValid(bad) ? "invalid" : zone[bad.X, bad.Y] != Zone.Free ? zone[bad.X, bad.Y].ToString() : "near " + near)
                    + $" (nearest start {players.Min(pl => pl.Position.DistanceTo(bad)):0.#} away; block {BlockOf(bad)} raised {IsRaisedCell(bad)}, start block {BlockOf(players.OrderBy(pl => pl.Position.DistanceTo(bad)).First().Position)})";
                return false;
            }

            var type = CliffRamps.WithRampEnds(cliffType, theater, from.Edge, to.Edge);
            if (type == null)
            {
                why = "no ramp pieces";
                return false;
            }
            ConnectedTilePlanResult result = null;
            for (int retry = 0; retry < 3 && (result == null || !result.Success); retry++)
                result = ConnectedTilePlanner.Plan(type, path, CliffRamps.SideOf(from.Edge), seed + g + retry * 101, useEndPieces: true, closed: false, valid, strictExpansionLimit: 400_000);
            if (!result.Success)
            {
                why = "stretch of " + path.Count + " points: " + result.Status;
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    Console.Error.WriteLine($"    stretch {from.Edge}->{to.Edge}: {string.Join(" ", path)}  (loop {string.Join(" ", vertices)})");
                return false;
            }
            if (!CliffRamps.IsRampPiece(result.Plan.Placements[0].Tile) || !CliffRamps.IsRampPiece(result.Plan.Placements[^1].Tile))
            {
                why = "no slope piece at the gap";
                return false;
            }
            planned.Add((result.Plan, true));
        }
        return true;
    }

    private static double DistanceAlong(CellPos from, CellPos p) => Math.Abs(p.X - from.X) + Math.Abs(p.Y - from.Y);

    /// <summary>
    /// Ramp gaps for a loop: one where each base route leaves the region, one or two facing the
    /// centre for other regions, none at all now and then (a closed scenery plateau), but always at
    /// least one for a region with a base on it.
    /// </summary>
    private List<TerraceGap> ChooseGaps(List<CellPos> vertices, HashSet<CellPos> cells, bool hasBase, Func<CellPos, bool> room, HashSet<int> excluded = null)
    {
        const int gapLength = 8;
        int n = vertices.Count;
        var gaps = new List<TerraceGap>();
        var usedRuns = new HashSet<int>(excluded ?? new HashSet<int>());

        bool TryGapNear(CellPos near, bool mustFaceCentre)
        {
            // The nearest run with room for the whole ramp.
            TerraceGap best = null;
            double bestDist = double.MaxValue;
            for (int r = 0; r < n; r++)
            {
                if (usedRuns.Contains(r))
                    continue;
                var a = vertices[r];
                var b = vertices[(r + 1) % n];
                // Only straight block edges; runs bent by the map edge get no gap.
                if (a.X != b.X && a.Y != b.Y || DistanceAlong(a, b) < gapLength + 6)
                    continue;
                var mid = new CellPos((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                double d = mid.DistanceTo(near);
                if (d >= bestDist)
                    continue;
                var gap = MakeGap(vertices, r, near, cells, gapLength);
                if (!gap.Ramp.All(room) || !gap.Foot.All(room) || !room(gap.A) || !room(gap.B))
                    continue;
                bestDist = d;
                best = gap;
            }
            if (best == null)
                return false;
            gaps.Add(best);
            usedRuns.Add(best.Run);
            return true;
        }

        var centre = MapCentre();
        if (Environment.GetEnvironmentVariable("MAPGEN_TERRACE_TEST") == "square")
            return gaps;
        if (terraceTestGaps)
        {
            // One gap in the middle of every run that has room.
            for (int r = 0; r < n; r++)
            {
                var a = vertices[r];
                var b = vertices[(r + 1) % n];
                if (a.X != b.X && a.Y != b.Y || DistanceAlong(a, b) < gapLength + 6)
                    continue;
                var mid = new CellPos((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                gaps.Add(MakeGap(vertices, r, mid, cells, gapLength));
            }
            return gaps;
        }
        if (hasBase)
        {
            // Where each base's route to the centre leaves the region.
            foreach (var pl in players.Where(p => cells.Contains(p.Position)))
            {
                var exit = pl.Position;
                double dx = centre.X - pl.Position.X, dy = centre.Y - pl.Position.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                for (double t = 0; t <= len; t += 1)
                {
                    var q = new CellPos((int)Math.Round(pl.Position.X + dx / len * t), (int)Math.Round(pl.Position.Y + dy / len * t));
                    if (!cells.Contains(q))
                        break;
                    exit = q;
                }
                TryGapNear(exit, true);
            }
            if (gaps.Count == 0)
                TryGapNear(centre, true);
            // A second way down, towards the nearest teammate when there is one.
            var owner = players.First(p => cells.Contains(p.Position));
            var mate = players.Where(p => p.Team == owner.Team && p != owner).OrderBy(p => p.Position.DistanceTo(owner.Position)).FirstOrDefault();
            if (mate != null && rng.NextDouble() < 0.7)
                TryGapNear(mate.Position, false);
        }
        else if (cells.Contains(centre))
        {
            // The centre terrace is the contested ground: a way up from every team.
            foreach (var team in players.GroupBy(p => p.Team))
            {
                var tp = team.Select(p => p.Position).ToList();
                var towards = new CellPos((int)Math.Round(tp.Average(p => p.X)), (int)Math.Round(tp.Average(p => p.Y)));
                TryGapNear(towards, true);
            }
            if (gaps.Count == 0)
                TryGapNear(centre, true);
        }
        else
        {
            if (cells.Count < 12 * Block * Block && rng.NextDouble() < 0.25)
                return gaps; // closed scenery
            TryGapNear(centre, true);
            if (rng.NextDouble() < 0.6)
                TryGapNear(players.OrderBy(p => cells.Min(c => c.DistanceTo(p.Position))).First().Position, false);
        }

        // Long loops: the planner cannot build a stretch of dozens of corners in one go, so add gaps
        // until every stretch between gaps is short enough. Big terraces get several ways up anyway.
        const int MaxCornersPerStretch = 8;
        for (int guard = 0; guard < 8 && gaps.Count > 0; guard++)
        {
            var runs = gaps.Select(g => g.Run).OrderBy(r => r).ToList();
            int worstFrom = -1, worstLen = 0;
            for (int g = 0; g < runs.Count; g++)
            {
                int len = (runs[(g + 1) % runs.Count] - runs[g] + n) % n;
                if (runs.Count == 1)
                    len = n;
                if (len > worstLen)
                {
                    worstLen = len;
                    worstFrom = runs[g];
                }
            }
            if (worstLen <= MaxCornersPerStretch)
                break;
            // A gap roughly halfway along the longest stretch, or as near to halfway as a run allows.
            bool added = false;
            for (int offset = 0; offset <= worstLen / 2 && !added; offset++)
                foreach (int sign in new[] { 1, -1 })
                {
                    int run = ((worstFrom + worstLen / 2 + sign * offset) % n + n) % n;
                    var a = vertices[run];
                    var b = vertices[(run + 1) % n];
                    if (usedRuns.Contains(run) || a.X != b.X && a.Y != b.Y)
                        continue;
                    var mid = new CellPos((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    var gap = MakeGap(vertices, run, mid, cells, gapLength);
                    if (!gap.Ramp.All(room) || !gap.Foot.All(room) || !room(gap.A) || !room(gap.B))
                        continue;
                    gaps.Add(gap);
                    usedRuns.Add(run);
                    added = true;
                    break;
                }
            if (!added)
                break;
        }
        return gaps;
    }

    private TerraceGap MakeGap(List<CellPos> vertices, int run, CellPos near, HashSet<CellPos> cells, int gapLength)
    {
        int n = vertices.Count;
        var a = vertices[run];
        var b = vertices[(run + 1) % n];
        bool alongX = a.Y == b.Y;
        int lo = alongX ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
        int hi = alongX ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
        int want = alongX ? near.X : near.Y;
        // Keep 3 cells for the slope pieces at either end of the run.
        int centreAlong = Math.Clamp(want, lo + 3 + gapLength / 2, hi - 3 - gapLength / 2);
        int g0 = centreAlong - gapLength / 2, g1 = centreAlong + gapLength / 2;
        int edgeCoord = alongX ? a.Y : a.X;

        // Which side is low: the cell one step off the run on either side.
        CellPos Off(int s) => alongX ? new CellPos(centreAlong, edgeCoord + s) : new CellPos(edgeCoord + s, centreAlong);
        int low = cells.Contains(Off(1)) ? -1 : 1;
        var edge = alongX ? (low > 0 ? RampEdge.SouthWest : RampEdge.NorthEast) : (low > 0 ? RampEdge.SouthEast : RampEdge.NorthWest);

        CellPos At(int along) => alongX ? new CellPos(along, edgeCoord) : new CellPos(edgeCoord, along);
        // In travel order along the run.
        bool forward = alongX ? b.X > a.X : b.Y > a.Y;
        var gA = forward ? At(g0) : At(g1);
        var gB = forward ? At(g1) : At(g0);

        var ramp = new HashSet<CellPos>();
        var foot = new HashSet<CellPos>();
        for (int along = g0 + 2; along <= g1 - 2; along++)
        {
            for (int k = -1; k <= 2; k++)
                if (along > g0 + 2 && along < g1 - 2)
                    ramp.Add(alongX ? new CellPos(along, edgeCoord + low * k) : new CellPos(edgeCoord + low * k, along));
            for (int k = 3; k <= 5; k++)
                foot.Add(alongX ? new CellPos(along, edgeCoord + low * k) : new CellPos(edgeCoord + low * k, along));
        }
        return new TerraceGap { Run = run, A = gA, B = gB, Edge = edge, Ramp = ramp, Foot = foot };
    }
}
