using MapGen.Model;
using MapGen.Terrain;

namespace MapGen.Generation;

/// <summary>
/// Water and the land/water layouts (the built-in generator's Inland, Continent, Team Continents and
/// Archipelago, plus player islands). A symmetric land mask is built first; every land body that does
/// not touch the map edge becomes an island with a shore loop around it, every enclosed water body a
/// lake with a shore loop inside it, and everything outside the islands is ocean.
/// </summary>
internal sealed partial class GenerationRun
{
    private const int TmpWater = 9;
    private const int TmpBeach = 10;

    // On island layouts the coastline decides every route, so it follows the feature symmetry even
    // when the rest of the terrain is free; lakes on land maps are free scenery.
    private Symmetry coastSym => IsIslandLayout ? sym : terrain;

    private bool IsIslandLayout => settings.Layout is MapLayout.Continent or MapLayout.TeamIslands or MapLayout.PlayerIslands or MapLayout.Archipelago;

    private List<int> waterTiles;
    private ConnectedTileType shoreType;

    private void PlanWater()
    {
        shoreType = connectedTypes?.FirstOrDefault(t => t.IniName == "Shore");
        int waterSet = theater.GeneralInt("WaterSet", -1);
        waterTiles = Enumerable.Range(0, theater.TileSet(waterSet)?.TilesInSet ?? 0)
            .Select(i => theater.Tile(theater.TileId(waterSet, i)))
            .Where(t => t != null && !t.Missing && t.Width == 1 && t.Height == 1 && t.Cells.Count == 1 && t.Cells[0].TerrainType == TmpWater)
            .Select(t => t.Id).ToList();

        // Lunar has no shores (or water).
        if (shoreType == null || waterTiles.Count == 0)
        {
            if (IsIslandLayout)
                throw new GenerationFailedException($"{map.Theater} has no water, so island layouts are not possible");
            return;
        }

        bool[,] land = BuildLandMask();
        if (land == null)
            return;

        var components = Components(land);
        var shoreCells = new HashSet<CellPos>();
        var lakeWater = new HashSet<CellPos>();
        if (riverCells != null && Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
            foreach (var comp in components.Where(c => c.Any(riverCells.Contains)))
                Console.Error.WriteLine($"RIVER component of {comp.Count} cells, {(comp.Any(p => !IsUsable(p) || !map.IsInLocal(p, 3)) ? "touches the edge" : "inside")}");

        foreach (var comp in components)
        {
            bool isLand = land[comp[0].X, comp[0].Y];
            bool touchesEdge = comp.Any(p => !IsUsable(p) || !map.IsInLocal(p, 3));
            if (touchesEdge)
                continue; // the open land of Inland maps, or the ocean around islands

            // One representative per symmetric orbit of bodies: plan it there and copy the outline.
            // A bent body's centroid may lie outside it: the nearest body cell stands in.
            var centroid = new CellPos((int)Math.Round(comp.Average(p => p.X)), (int)Math.Round(comp.Average(p => p.Y)));
            var set = comp.ToHashSet();
            if (!set.Contains(centroid))
                centroid = comp.OrderBy(p => p.DistanceTo(centroid)).First();
            // The symmetries that map this body onto itself (e.g. a lake straddling one mirror axis).
            var stabilizer = Enumerable.Range(0, coastSym.Order).Where(t => set.Contains(coastSym.Apply(t, centroid))).ToList();
            bool selfSymmetric = stabilizer.Count > 1;
            if (!selfSymmetric && !coastSym.IsCanonical(centroid))
                continue;

            bool shoreOk = PlaceShoreLoops(set, centroid, isLand, stabilizer, shoreCells, lakeWater);
            if (riverCells != null && Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1" && set.Overlaps(riverCells))
                Console.Error.WriteLine($"RIVER body of {set.Count} cells ({(isLand ? "land" : "water")}, stabilizer {stabilizer.Count}): shore {(shoreOk ? "ok" : "FAILED")}");
            if (!shoreOk)
            {
                if (isLand && IsIslandLayout && players.Any(pl => set.Contains(pl.Position)))
                    throw new GenerationFailedException("Could not build the shore around a player's island");
                // Half a river is unfair: the other half may well have been built.
                if (riverCells != null && set.Overlaps(riverCells))
                    throw new GenerationFailedException("Could not build the river's shore");
                // A small lake or islet that will not plan is simply left out.
            }
        }

        // Ocean: everything reachable from the map edge without crossing a shore, in island layouts.
        if (IsIslandLayout)
        {
            var ocean = new HashSet<CellPos>();
            var queue = new Queue<CellPos>();
            foreach (var p in map.AllValidCells())
                if (!map.IsInLocal(p, 3) && !shoreCells.Contains(p) && !land[p.X, p.Y])
                {
                    ocean.Add(p);
                    queue.Enqueue(p);
                }
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var o in CellPos.Neighbours8.Take(4))
                {
                    var q = p + o;
                    if (!map.IsValid(q) || shoreCells.Contains(q) || ocean.Contains(q))
                        continue;
                    // Stop at forced land (bases) so a failed shore never floods a player.
                    if (zone[q.X, q.Y] is Zone.Base or Zone.BaseCore)
                        throw new GenerationFailedException("The ocean reached a base");
                    ocean.Add(q);
                    queue.Enqueue(q);
                }
            }
            lakeWater.UnionWith(ocean);
        }

        FillWater(lakeWater);
        if (riverCells != null)
        {
            riverCells.IntersectWith(lakeWater);
            if (riverCells.Count == 0)
                throw new GenerationFailedException("The river could not be built");
        }
    }

    /// <summary>
    /// Fills water like WAE's "Smoothen Water" script: a random 2x2 water tile wherever four
    /// unfilled water cells fit, a random 1x1 tile elsewhere, so open water does not look tiled.
    /// </summary>
    private void FillWater(HashSet<CellPos> water)
    {
        int waterSet = theater.GeneralInt("WaterSet", -1);
        var bigTiles = Enumerable.Range(0, theater.TileSet(waterSet)?.TilesInSet ?? 0)
            .Select(i => theater.Tile(theater.TileId(waterSet, i)))
            .Where(t => t != null && !t.Missing && t.Width == 2 && t.Height == 2 && t.Cells.Count == 4 && t.Cells.All(c => c.TerrainType == TmpWater))
            .ToList();

        var open = water.Where(p => !tilePlaced[p.X, p.Y]).ToHashSet();
        foreach (var p in open.OrderBy(p => p.Y).ThenBy(p => p.X).ToList())
        {
            if (!open.Contains(p))
                continue;

            var block = new[] { p, p + new CellPos(1, 0), p + new CellPos(0, 1), p + new CellPos(1, 1) };
            if (bigTiles.Count > 0 && block.All(open.Contains))
            {
                var tile = bigTiles[rng.Next(bigTiles.Count)];
                foreach (var cell in tile.Cells)
                    SetWater(new CellPos(p.X + cell.X, p.Y + cell.Y), tile.Id, (byte)cell.Index, open);
            }
            else
            {
                SetWater(p, waterTiles[rng.Next(waterTiles.Count)], 0, open);
            }
        }
    }

    private void SetWater(CellPos p, int tileId, byte subTile, HashSet<CellPos> open)
    {
        ref var c = ref map[p];
        c.TileIndex = tileId;
        c.SubTile = subTile;
        c.Level = 0;
        tilePlaced[p.X, p.Y] = true;
        zone[p.X, p.Y] = Zone.Water;
        open.Remove(p);
    }

    // ---------------------------------------------------------------- the land mask

    /// <summary>Symmetric land/water mask (true = land) for the layout, or null when there is no water.</summary>
    private bool[,] BuildLandMask()
    {
        double water = Math.Clamp(settings.Water, 0, 1);
        if (settings.Layout == MapLayout.Land && water <= 0)
            return null;

        int n = map.Span;
        var land = new bool[n, n];
        int seed = rng.Next();
        double cx = (map.Width + map.Height) / 2.0;

        // Contested islets between the players in the archipelago and player-island layouts.
        var islets = new List<(CellPos At, double R)>();
        if (settings.Layout is MapLayout.Archipelago or MapLayout.PlayerIslands)
        {
            int count = settings.Layout == MapLayout.Archipelago ? 3 + rng.Next(3) : 1 + rng.Next(2);
            for (int i = 0; i < count; i++)
                islets.Add((PickContestedPoint(BaseRadius + 14), 5 + rng.NextDouble() * 5));
            islets.Add((MapCentre(), 6 + rng.NextDouble() * 5));
        }

        // Island size shrinks as the water slider rises.
        double islandPad = 11 * (1.3 - 0.6 * water);

        // Inland lakes: the slider is the share of the map under water (up to ~30%), so the noise
        // threshold is taken from the noise values' own distribution.
        double lakeThreshold = double.MaxValue;
        double LakeNoise(CellPos c) => 0.8 * ValueNoise(c.X / 22.0, c.Y / 22.0, seed + 7) + 0.2 * ValueNoise(c.X / 9.0, c.Y / 9.0, seed + 8);
        if (settings.Layout == MapLayout.Land)
        {
            var values = map.AllValidCells().Where(IsUsable).Select(p => LakeNoise(coastSym.Canonical(p))).OrderBy(v => v).ToList();
            lakeThreshold = values[(int)Math.Clamp((1 - 0.38 * water) * (values.Count - 1), 0, values.Count - 1)];
        }

        // A river between the sides: a gently winding band through the centre, across the line
        // between the teams, ending inside the playable area so it is a lake with shores. The centre
        // stays dry (a ford at the most contested spot); the other routes get bridges. Units are
        // screen half-steps (one unit is about 1.4 cells).
        // The river (decided with the map) runs along the y axis through the centre: x = c/2 plus a
        // gentle wobble. The starts were placed clear of it.
        bool river = riverPlanned;
        int c0 = map.Width + map.Height;
        const double Wobble = 6;
        double halfWidth = 5.0 + 2.0 * water;
        double Off(CellPos c) => c.X - c0 / 2.0;
        double Along(CellPos c) => c.Y - c0 / 2.0;
        double need = halfWidth + Wobble + BaseRadius + 6;
        if (river && players.Any(pl => Math.Abs(Off(pl.Position)) < need))
        {
            if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                Console.Error.WriteLine($"RIVER skipped: a base is too close to the middle of the map (offsets {string.Join(" ", players.Select(pl => Math.Abs(Off(pl.Position)).ToString("0")))}, need {need:0})");
            river = false;
        }
        else if (river && Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
            Console.Error.WriteLine($"RIVER along y, half width {halfWidth:0.#}");
        riverCells = river ? new HashSet<CellPos>() : null;
        // How far along its axis the river runs before the playable edge (shortest side).
        double riverReach = river ? map.AllValidCells().Where(p => map.IsInLocal(p, 7)).Max(p => Math.Abs(Along(p))) : 0;
        bool InRiver(CellPos p)
        {
            if (!river || !map.IsInLocal(p, 7))
                return false;
            // The band is symmetric by construction; the wobble is sampled on the canonical cell so the
            // copies match (for a rotation that is the same |along|).
            var c = coastSym.Canonical(p);
            double along = Math.Abs(Along(c));
            // The ford: a strip across the whole river at the centre, so the two halves are separate
            // bodies with their own shores. Straight there, winding further out.
            if (along < 7)
                return false;
            double wobble = (ValueNoise(along / 22.0, 0.37, seed + 11) * 2 - 1) * Wobble * Math.Clamp((along - 7) / 14.0, 0, 1);
            // The ends taper to a point over the last dozen cells before the playable edge.
            double toEdge = riverReach - along;
            double width = halfWidth * Math.Clamp(toEdge / 12.0, 0.25, 1);
            return toEdge > 0 && Math.Abs(Off(p) - wobble) < width;
        }

        foreach (var p in map.AllValidCells())
        {
            var c = coastSym.Canonical(p);
            double noise = ValueNoise(c.X / 9.0, c.Y / 9.0, seed) - 0.5;
            bool isLand;
            switch (settings.Layout)
            {
                case MapLayout.Continent:
                {
                    // An ellipse over the playable area (in screen space), roughed up by noise.
                    var (sx, sy) = map.ToScreen(c);
                    double nx = (sx / 60.0 - (map.LocalX + map.LocalWidth / 2.0)) / (map.LocalWidth / 2.0);
                    double ny = (sy / 30.0 - (map.LocalY + map.LocalHeight / 2.0)) / (map.LocalHeight / 2.0);
                    double r = Math.Sqrt(nx * nx + ny * ny);
                    isLand = r < 0.88 - 0.25 * water + noise * 0.25;
                    break;
                }
                case MapLayout.TeamIslands:
                {
                    // Each team's starts joined into one island, with open water between teams.
                    var perTeam = new List<double>();
                    foreach (var team in players.GroupBy(pl => pl.Team))
                    {
                        double d = double.MaxValue;
                        var members = team.Select(pl => pl.Position).ToList();
                        for (int i = 0; i < members.Count; i++)
                        {
                            d = Math.Min(d, c.DistanceTo(members[i]));
                            for (int j = i + 1; j < members.Count; j++)
                                d = Math.Min(d, SegmentDistance(c, members[i], members[j]));
                        }
                        perTeam.Add(d);
                    }
                    perTeam.Sort();
                    isLand = perTeam[0] < BaseRadius + islandPad + noise * 8 && (perTeam.Count < 2 || perTeam[1] - perTeam[0] > 10);
                    break;
                }
                case MapLayout.PlayerIslands:
                case MapLayout.Archipelago:
                {
                    // Each player's own island (a channel keeps neighbours apart), plus islets.
                    var byPlayer = players.Select(pl => c.DistanceTo(pl.Position)).OrderBy(d => d).ToList();
                    double best = byPlayer[0] - (BaseRadius + islandPad * 0.8);
                    bool apart = byPlayer.Count < 2 || byPlayer[1] - byPlayer[0] > 8;
                    if (!apart)
                        best = double.MaxValue;
                    foreach (var (at, r) in islets)
                        best = Math.Min(best, Math.Min(c.DistanceTo(at), c.DistanceTo(coastSym.Canonical(at))) - r);
                    isLand = best + noise * 7 < 0;
                    break;
                }
                default:
                {
                    // Land with lakes: the highest values of a broad noise field become water. No lake
                    // within reach of the river: a merged body would not get a shore.
                    isLand = LakeNoise(c) < lakeThreshold;
                    if (river && Math.Abs(Off(p)) < halfWidth + Wobble + 10)
                        isLand = true;
                    if (InRiver(p))
                    {
                        isLand = false;
                        riverCells.Add(p);
                    }
                    break;
                }
            }

            land[p.X, p.Y] = isLand;
        }

        // Bases are always land, with room to build. In island layouts the map edge is always water;
        // inland lakes stay clear of the edge so each one is enclosed by land and gets a shore.
        foreach (var p in map.AllValidCells())
        {
            if (players.Any(pl => pl.Position.DistanceTo(p) < BaseRadius + 6))
                land[p.X, p.Y] = true;
            // The centre is the main contested spot on land maps; keep it dry.
            else if (settings.Layout == MapLayout.Land && p.DistanceTo(MapCentre()) < 10)
                land[p.X, p.Y] = true;
            else if (!map.IsInLocal(p, IsIslandLayout ? 4 : 6))
                land[p.X, p.Y] = !IsIslandLayout;
        }

        Smooth(land);
        // Free lakes stay off the lanes (island coasts are mirrored, so they need not), and off the
        // terraces and their borders, where the cliffs go.
        foreach (var p in map.AllValidCells())
            if (!IsIslandLayout && OnLane(p) && !(riverCells?.Contains(p) ?? false) || raised != null && (IsRaisedCell(p) || nearTerraceEdge[p.X, p.Y]))
                land[p.X, p.Y] = true;
            else if (riverCells != null && riverCells.Contains(p) && players.All(pl => pl.Position.DistanceTo(p) >= BaseRadius + 6))
                land[p.X, p.Y] = false;

        if (riverCells != null && Environment.GetEnvironmentVariable("MAPGEN_DEBUG_MASK") == "1")
        {
            Console.Error.WriteLine($"RIVER mask ({(splitTopBottom ? "top/bottom split" : "left/right split")}, centre {MapCentre()}, starts {string.Join(" ", players.Select(pl => pl.Position))}):");
            for (int y = 0; y < map.Span; y += 3)
            {
                var row = new System.Text.StringBuilder();
                for (int x = 0; x < map.Span; x += 2)
                {
                    var q = new CellPos(x, y);
                    row.Append(!map.IsValid(q) ? ' ' : players.Any(pl => pl.Position.DistanceTo(q) < 3) ? 'S' : q.DistanceTo(MapCentre()) < 3 ? 'C' : riverCells.Contains(q) ? (land[x, y] ? 'r' : '~') : land[x, y] ? '.' : 'o');
                }
                Console.Error.WriteLine(row.ToString());
            }
        }

        // Inland lakes must not cut the players off from each other: open land bridges if they do.
        // A river is meant to: its crossings are the bridges.
        if (settings.Layout == MapLayout.Land && riverCells == null)
            BridgeLakes(land);

        return land;
    }

    private static double SegmentDistance(CellPos p, CellPos a, CellPos b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, wx = p.X - a.X, wy = p.Y - a.Y;
        double t = Math.Clamp((vx * wx + vy * wy) / Math.Max(1e-9, vx * vx + vy * vy), 0, 1);
        double dx = wx - t * vx, dy = wy - t * vy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Majority smoothing (kept symmetric by evaluating at canonical cells only).</summary>
    private void Smooth(bool[,] land)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var copy = (bool[,])land.Clone();
            foreach (var p in map.AllValidCells())
            {
                if (!coastSym.IsCanonical(p))
                    continue;
                int count = 0, total = 0;
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        var q = new CellPos(p.X + dx, p.Y + dy);
                        if (!map.IsValid(q))
                            continue;
                        total++;
                        if (copy[q.X, q.Y]) count++;
                    }
                bool v = count * 2 > total;
                if (players.Any(pl => pl.Position.DistanceTo(p) < BaseRadius + 6))
                    v = true;
                foreach (var q in coastSym.Orbit(p))
                    if (map.IsValid(q))
                        land[q.X, q.Y] = v;
            }
        }
    }

    private void BridgeLakes(bool[,] land)
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            // Every start, and the centre (a lake round it would leave the centre building on an islet).
            var seen = FloodMask(land, players[0].Position);
            var cut = players.Select(pl => pl.Position).Append(MapCentre()).FirstOrDefault(p => !seen[p.X, p.Y]);
            if (cut == default)
                return;

            // Shortest route through the water, then make it (and its images) a wide land bridge.
            var from = players[0].Position;
            var dist = new Dictionary<CellPos, double> { [from] = 0 };
            var prev = new Dictionary<CellPos, CellPos>();
            var queue = new PriorityQueue<CellPos, double>();
            queue.Enqueue(from, 0);
            while (queue.TryDequeue(out var p, out double d))
            {
                if (p == cut) break;
                if (d > dist[p]) continue;
                foreach (var o in CellPos.Neighbours8.Take(4))
                {
                    var q = p + o;
                    if (!IsUsable(q)) continue;
                    double nd = d + (land[q.X, q.Y] ? 1 : 10);
                    if (!dist.TryGetValue(q, out double old) || nd < old)
                    {
                        dist[q] = nd;
                        prev[q] = p;
                        queue.Enqueue(q, nd);
                    }
                }
            }
            if (!prev.ContainsKey(cut))
                return;
            for (var p = cut; p != from; p = prev[p])
                foreach (var q in Disk(p, 4))
                    foreach (var img in coastSym.Orbit(q))
                        if (map.IsValid(img))
                            land[img.X, img.Y] = true;
        }
    }

    private bool[,] FloodMask(bool[,] land, CellPos from)
    {
        var seen = new bool[map.Span, map.Span];
        var queue = new Queue<CellPos>();
        seen[from.X, from.Y] = true;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var o in CellPos.Neighbours8.Take(4))
            {
                var q = p + o;
                if (!map.IsValid(q) || seen[q.X, q.Y] || !land[q.X, q.Y]) continue;
                seen[q.X, q.Y] = true;
                queue.Enqueue(q);
            }
        }
        return seen;
    }

    /// <summary>Connected components of equal mask value (4-connected) over usable cells.</summary>
    private List<List<CellPos>> Components(bool[,] land)
    {
        var seen = new bool[map.Span, map.Span];
        var result = new List<List<CellPos>>();
        foreach (var start in map.AllValidCells())
        {
            if (seen[start.X, start.Y] || !IsUsable(start))
                continue;
            bool value = land[start.X, start.Y];
            var comp = new List<CellPos>();
            var queue = new Queue<CellPos>();
            seen[start.X, start.Y] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                comp.Add(p);
                foreach (var o in CellPos.Neighbours8.Take(4))
                {
                    var q = p + o;
                    if (!map.IsValid(q) || seen[q.X, q.Y] || land[q.X, q.Y] != value) continue;
                    if (!IsUsable(q)) { comp.Add(q); seen[q.X, q.Y] = true; continue; }
                    seen[q.X, q.Y] = true;
                    queue.Enqueue(q);
                }
            }
            result.Add(comp);
        }
        return result;
    }

    // ---------------------------------------------------------------- shore loops

    /// <summary>
    /// Plans the shore around a body (island or lake) and its symmetric copies. The outline is a
    /// star-shaped polygon from rays cast out of the centre, made exactly symmetric before planning.
    /// </summary>
    private bool PlaceShoreLoops(HashSet<CellPos> body, CellPos centroid, bool isLand, List<int> stabilizer,
        HashSet<CellPos> shoreCells, HashSet<CellPos> lakeWater)
    {
        // Lakes smaller than this never fit a shore loop (the pieces eat the water), and each failure is slow.
        if (body.Count < (isLand ? 40 : 85) || !body.Contains(centroid))
            return false;

        // Rays every ~12 degrees; the vertex sits two cells inside the body's edge along the ray. A long
        // thin body (a river) is not star-shaped from its centroid: it gets a ribbon outline instead.
        bool isRiver = riverCells != null && body.Overlaps(riverCells);
        var vertices = RibbonOutline(body, isLand, force: isRiver) ?? new List<CellPos>();
        bool useRays = vertices.Count == 0;
        if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1" && body.Count > 150)
            Console.Error.WriteLine($"  shore outline for {body.Count} cells: {(useRays ? "rays" : "ribbon " + vertices.Count)}");
        int rays = Math.Clamp((int)(Math.Sqrt(body.Count) * 2.2), 10, 36);
        for (int i = 0; i < rays && useRays; i++)
        {
            double a = i * 2 * Math.PI / rays;
            double dx = Math.Cos(a), dy = Math.Sin(a);
            CellPos last = centroid;
            for (double r = 1; r < 200; r += 0.5)
            {
                var q = new CellPos(centroid.X + (int)Math.Round(dx * r), centroid.Y + (int)Math.Round(dy * r));
                if (!body.Contains(q)) break;
                last = q;
            }
            double back = Math.Max(0, last.DistanceTo(centroid) - (isLand ? 2 : 1));
            vertices.Add(new CellPos(centroid.X + (int)Math.Round(dx * back), centroid.Y + (int)Math.Round(dy * back)));
        }

        var outlines = new List<(List<CellPos> Vertices, CellPos Centre)>();
        if (!useRays)
        {
            // A ribbon follows the body, which is symmetric already; mirroring its vertices would only
            // zigzag the outline. Copies of a non-self-symmetric body still need their own outlines.
            if (stabilizer.Count > 1)
                outlines.Add((vertices, centroid));
            else
                for (int t = 0; t < coastSym.Order; t++)
                    outlines.Add((vertices.Select(v => coastSym.Apply(t, v)).ToList(), coastSym.Apply(t, centroid)));
            outlines = outlines.DistinctBy(o => o.Centre).ToList();
        }
        else if (stabilizer.Count > 1)
        {
            // Every vertex together with its images under the body's own symmetries, thinned so
            // neighbours are at least 4 cells apart: the outline is (nearly exactly) symmetric.
            var all = OrderCounterClockwise(vertices.SelectMany(v => stabilizer.Select(t => coastSym.Apply(t, v))).Distinct().ToList());
            var thinned = new List<CellPos>();
            foreach (var v in all)
                if (thinned.Count == 0 || v.DistanceTo(thinned[^1]) >= 4)
                    thinned.Add(v);
            while (thinned.Count > 3 && thinned[^1].DistanceTo(thinned[0]) < 4)
                thinned.RemoveAt(thinned.Count - 1);
            outlines.Add((thinned, centroid));
        }
        else
        {
            for (int t = 0; t < coastSym.Order; t++)
                outlines.Add((OrderCounterClockwise(vertices.Select(v => coastSym.Apply(t, v)).Distinct().ToList()), coastSym.Apply(t, centroid)));
            outlines = outlines.DistinctBy(o => o.Centre).ToList();
        }

        // Tight spots (a narrow neck, a nearby cliff): try again with the outline pulled inwards. A
        // river is worth several goes, each with a fresh planner seed.
        var shrunk = outlines.Select(o => (o.Vertices.Select(v => new CellPos(
            o.Centre.X + (int)Math.Round((v.X - o.Centre.X) * 0.85), o.Centre.Y + (int)Math.Round((v.Y - o.Centre.Y) * 0.85))).Distinct().ToList(), o.Centre)).ToList();
        int rounds = isRiver ? 3 : 1;
        for (int round = 0; round < rounds; round++)
        {
            if (TryPlanShores(outlines, body, isLand, shoreCells, lakeWater))
                return true;
            if (TryPlanShores(shrunk, body, isLand, shoreCells, lakeWater))
                return true;
        }
        return false;
    }

    /// <summary>
    /// For a body much longer than it is wide: vertices along one edge, then back along the other,
    /// sampled every few cells along the body's principal axis. Null for a compact body.
    /// </summary>
    private static List<CellPos> RibbonOutline(HashSet<CellPos> body, bool isLand, bool force = false)
    {
        double cx = body.Average(p => p.X), cy = body.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in body)
        {
            sxx += (p.X - cx) * (p.X - cx);
            syy += (p.Y - cy) * (p.Y - cy);
            sxy += (p.X - cx) * (p.Y - cy);
        }
        // Principal axis of the covariance.
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ax = Math.Cos(theta), ay = Math.Sin(theta);
        double l1 = 0, l2 = 0;
        foreach (var p in body)
        {
            double a = (p.X - cx) * ax + (p.Y - cy) * ay, b = -(p.X - cx) * ay + (p.Y - cy) * ax;
            l1 += a * a;
            l2 += b * b;
        }
        if (l2 <= 0 || !force && Math.Sqrt(l1 / l2) < 2.5)
            return null;

        // Slabs along the axis: the extreme perpendicular offsets in each.
        const double Step = 6;
        var slabs = new SortedDictionary<int, (double Min, double Max)>();
        foreach (var p in body)
        {
            double a = (p.X - cx) * ax + (p.Y - cy) * ay, b = -(p.X - cx) * ay + (p.Y - cy) * ax;
            int k = (int)Math.Floor(a / Step);
            slabs[k] = slabs.TryGetValue(k, out var e) ? (Math.Min(e.Min, b), Math.Max(e.Max, b)) : (b, b);
        }
        double inset = isLand ? 2 : 2.5;
        var top = new List<CellPos>();
        var bottom = new List<CellPos>();
        foreach (var (k, (min, max)) in slabs)
        {
            if (max - min < 2 * inset + 1)
                continue;
            double a = (k + 0.5) * Step;
            top.Add(new CellPos((int)Math.Round(cx + a * ax - (max - inset) * ay), (int)Math.Round(cy + a * ay + (max - inset) * ax)));
            bottom.Add(new CellPos((int)Math.Round(cx + a * ax - (min + inset) * ay), (int)Math.Round(cy + a * ay + (min + inset) * ax)));
        }
        if (top.Count < 2)
            return null;
        bottom.Reverse();
        var outline = top.Concat(bottom).ToList();
        // No two consecutive vertices alike, and only ones inside the body.
        var result = new List<CellPos>();
        foreach (var v in outline)
            if (body.Contains(v) && (result.Count == 0 || result[^1] != v))
                result.Add(v);
        return result.Count >= 4 ? result : null;
    }

    private bool TryPlanShores(List<(List<CellPos> Vertices, CellPos Centre)> outlines, HashSet<CellPos> body, bool isLand,
        HashSet<CellPos> shoreCells, HashSet<CellPos> lakeWater)
    {
        int planSeed = rng.Next();
        var planned = new List<(ConnectedTilePlan Plan, HashSet<CellPos> Inside)>();
        var claimed = new HashSet<CellPos>(shoreCells);

        // The planner asks about cells millions of times: precompute everything but the claims.
        // Lanes stay clear of lakes; a river crosses them by design (its bridges are the crossings).
        bool isRiver = riverCells != null && body.Overlaps(riverCells);
        var allowed = CellMask(p => map.IsInLocal(p, 2) && zone[p.X, p.Y] == Zone.Free && (IsIslandLayout || isRiver || !OnLane(p))
                                    && !NearZone(p, 2, Zone.Base, Zone.BaseCore, Zone.Cliff, Zone.CliffTop, Zone.Tech, Zone.Resource));
        bool Valid(CellPos p) => map.IsValid(p) && allowed[p.X, p.Y] && !claimed.Contains(p);

        foreach (var (outline, centre) in outlines)
        {
            if (outline.Count < 3 || !outline.All(Valid))
            {
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1" && body.Count > 150)
                {
                    var bad = outline.FirstOrDefault(v => !Valid(v));
                    Console.Error.WriteLine($"  outline vertex {bad} invalid: {(map.IsValid(bad) ? zone[bad.X, bad.Y].ToString() + (OnLane(bad) ? " lane" : "") + (map.IsInLocal(bad, 2) ? "" : " edge") + (claimed.Contains(bad) ? " claimed" : "") : "off map")}");
                }
                return false;
            }

            ConnectedTilePlan plan = null;
            HashSet<CellPos> inside = null;
            // Try the winding/side that worked last time for this kind of body first.
            foreach (var (reversed, side) in ShoreOrientations(isLand))
            {
                var verts = reversed ? Enumerable.Reverse(outline).ToList() : outline;
                // A river is long and there are only two of them: worth a much deeper search than a lake.
                int limit = isRiver && int.TryParse(Environment.GetEnvironmentVariable("MAPGEN_RIVER_SHORE_LIMIT"), out int l) ? l : isRiver ? ShoreSearchLimit * 10 : ShoreSearchLimit;
                var result = ConnectedTilePlanner.Plan(shoreType, verts, side, planSeed, useEndPieces: false, closed: true, Valid, limit);
                bool dbg = Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1" && body.Count > 150;
                if (!result.Success)
                {
                    if (dbg)
                        Console.Error.WriteLine($"    shore {(reversed ? "rev" : "fwd")}/{side}: planner {result.Status}");
                    continue;
                }
                var cells = FoundationCells(result.Plan.Placements);
                var flood = FloodInside(centre, cells, body.Count * 3 + 400);
                if (flood == null || flood.Count < 6 || !ShoreFacesWater(result.Plan, flood, cells, waterInside: !isLand))
                {
                    if (dbg)
                        Console.Error.WriteLine($"    shore {(reversed ? "rev" : "fwd")}/{side}: {(flood == null ? "flood leaks" : flood.Count < 6 ? "flood tiny" : "faces wrong way (" + ShoreViolations(result.Plan, flood, cells, !isLand) + " of " + result.Plan.Placements.Count + ")")}");
                    continue;
                }
                (plan, inside) = (result.Plan, flood);
                workingShoreOrientation[isLand ? 1 : 0] = (reversed, side);
                break;
            }
            if (plan == null)
            {
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1" && body.Count > 150)
                    Console.Error.WriteLine($"  no shore plan for outline of {outline.Count} vertices");
                return false;
            }

            claimed.UnionWith(FoundationCells(plan.Placements));
            planned.Add((plan, inside));
        }

        foreach (var (plan, inside) in planned)
        {
            foreach (var p in CliffBuilder.Apply(map, theater, plan, 0, new Random(planSeed)))
            {
                tilePlaced[p.X, p.Y] = true;
                shoreCells.Add(p);
                var sub = theater.Tile(map[p].TileIndex)?.GetSubTile(map[p].SubTile);
                zone[p.X, p.Y] = sub != null && sub.TerrainType is TmpWater or TmpBeach ? Zone.Water : Zone.Open;
            }
            if (!isLand)
                lakeWater.UnionWith(inside);
        }
        return true;
    }

    // A shore that can be planned is found well within this many expanded nodes per segment; the
    // planner's default (25,000) mostly spends its time proving that a tight spot has no solution.
    private const int ShoreSearchLimit = 3_000;

    // Winding and side that last produced a correct shore, for lakes [0] and islands [1].
    private readonly (bool Reversed, ConnectedTileSide Side)?[] workingShoreOrientation = new (bool, ConnectedTileSide)?[2];

    private IEnumerable<(bool Reversed, ConnectedTileSide Side)> ShoreOrientations(bool isLand)
    {
        var known = workingShoreOrientation[isLand ? 1 : 0];
        if (known != null)
            yield return known.Value;
        foreach (bool reversed in new[] { false, true })
            foreach (var side in new[] { ConnectedTileSide.Front, ConnectedTileSide.Back })
                if (known == null || known.Value != (reversed, side))
                    yield return (reversed, side);
    }

    /// <summary>A predicate evaluated once for every usable cell.</summary>
    private bool[,] CellMask(Func<CellPos, bool> predicate)
    {
        var mask = new bool[map.Span, map.Span];
        foreach (var p in map.AllValidCells())
            mask[p.X, p.Y] = IsUsable(p) && predicate(p);
        return mask;
    }

    /// <summary>Shore tiles must have their water cells on the water side and land cells on the land side.</summary>
    private bool ShoreFacesWater(ConnectedTilePlan plan, HashSet<CellPos> inside, HashSet<CellPos> shoreCells, bool waterInside)
        => ShoreViolations(plan, inside, shoreCells, waterInside) <= Math.Max(4, plan.Placements.Count / 10);

    /// <summary>
    /// Shore cells facing the wrong way. A shore planned inside-out gets most of its cells wrong; a few
    /// odd tiles (some theaters' variants classify a corner cell differently) are tolerated.
    /// </summary>
    private int ShoreViolations(ConnectedTilePlan plan, HashSet<CellPos> inside, HashSet<CellPos> shoreCells, bool waterInside)
    {
        int violations = 0;
        foreach (var placement in plan.Placements)
        {
            var set = theater.TileSet(placement.Tile.TileSetIndex);
            var tile = theater.Tile(set.StartTile + placement.Tile.IndicesInTileSet[0]);
            if (tile == null)
                continue;
            foreach (var cell in tile.Cells)
            {
                var p = new CellPos(placement.Location.X + cell.X, placement.Location.Y + cell.Y);
                bool wet = cell.TerrainType == TmpWater;
                bool dry = cell.TerrainType is not (TmpWater or TmpBeach);
                foreach (var o in CellPos.Neighbours8.Take(4))
                {
                    var q = p + o;
                    if (shoreCells.Contains(q))
                        continue;
                    bool waterSide = inside.Contains(q) == waterInside;
                    if (wet && !waterSide || dry && waterSide)
                    {
                        violations++;
                        break;
                    }
                }
            }
        }
        return violations;
    }
}
