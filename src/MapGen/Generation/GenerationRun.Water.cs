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

        foreach (var comp in components)
        {
            bool isLand = land[comp[0].X, comp[0].Y];
            bool touchesEdge = comp.Any(p => !IsUsable(p) || !map.IsInLocal(p, 3));
            if (touchesEdge)
                continue; // the open land of Inland maps, or the ocean around islands

            // One representative per symmetric orbit of bodies: plan it there and copy the outline.
            var centroid = new CellPos((int)Math.Round(comp.Average(p => p.X)), (int)Math.Round(comp.Average(p => p.Y)));
            var set = comp.ToHashSet();
            // The symmetries that map this body onto itself (e.g. a lake straddling one mirror axis).
            var stabilizer = Enumerable.Range(0, coastSym.Order).Where(t => set.Contains(coastSym.Apply(t, centroid))).ToList();
            bool selfSymmetric = stabilizer.Count > 1;
            if (!selfSymmetric && !coastSym.IsCanonical(centroid))
                continue;

            if (!PlaceShoreLoops(set, centroid, isLand, stabilizer, shoreCells, lakeWater))
            {
                if (isLand && IsIslandLayout && players.Any(pl => set.Contains(pl.Position)))
                    throw new GenerationFailedException("Could not build the shore around a player's island");
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
                islets.Add((PickContestedPoint(BaseRadius + 14), 5 + rng.NextDouble() * (IsBlitz ? 3 : 5)));
            islets.Add((MapCentre(), 6 + rng.NextDouble() * 5));
        }

        // Island size shrinks as the water slider rises.
        double islandPad = (IsBlitz ? 7 : 11) * (1.3 - 0.6 * water);

        // Inland lakes: the slider is the share of the map under water (up to ~30%), so the noise
        // threshold is taken from the noise values' own distribution.
        double lakeThreshold = double.MaxValue;
        double LakeNoise(CellPos c) => 0.8 * ValueNoise(c.X / 22.0, c.Y / 22.0, seed + 7) + 0.2 * ValueNoise(c.X / 9.0, c.Y / 9.0, seed + 8);
        if (settings.Layout == MapLayout.Land)
        {
            var values = map.AllValidCells().Where(IsUsable).Select(p => LakeNoise(coastSym.Canonical(p))).OrderBy(v => v).ToList();
            lakeThreshold = values[(int)Math.Clamp((1 - 0.38 * water) * (values.Count - 1), 0, values.Count - 1)];
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
                    // Land with lakes: the highest values of a broad noise field become water.
                    isLand = LakeNoise(c) < lakeThreshold;
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
        // Free lakes stay off the lanes (island coasts are mirrored, so they need not).
        foreach (var p in map.AllValidCells())
            if (!IsIslandLayout && OnLane(p))
                land[p.X, p.Y] = true;

        // Inland lakes must not cut the players off from each other: open land bridges if they do.
        if (settings.Layout == MapLayout.Land)
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
            var seen = FloodMask(land, players[0].Position);
            var cut = players.Select(pl => pl.Position).FirstOrDefault(p => !seen[p.X, p.Y]);
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

        // Rays every ~12 degrees; the vertex sits two cells inside the body's edge along the ray.
        var vertices = new List<CellPos>();
        int rays = Math.Clamp((int)(Math.Sqrt(body.Count) * 2.2), 10, 36);
        for (int i = 0; i < rays; i++)
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
        if (stabilizer.Count > 1)
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

        if (TryPlanShores(outlines, body, isLand, shoreCells, lakeWater))
            return true;

        // Tight spots (a narrow neck, a nearby cliff): try once more with the outline pulled inwards.
        var shrunk = outlines.Select(o => (o.Vertices.Select(v => new CellPos(
            o.Centre.X + (int)Math.Round((v.X - o.Centre.X) * 0.85), o.Centre.Y + (int)Math.Round((v.Y - o.Centre.Y) * 0.85))).Distinct().ToList(), o.Centre)).ToList();
        return TryPlanShores(shrunk, body, isLand, shoreCells, lakeWater);
    }

    private bool TryPlanShores(List<(List<CellPos> Vertices, CellPos Centre)> outlines, HashSet<CellPos> body, bool isLand,
        HashSet<CellPos> shoreCells, HashSet<CellPos> lakeWater)
    {
        int planSeed = rng.Next();
        var planned = new List<(ConnectedTilePlan Plan, HashSet<CellPos> Inside)>();
        var claimed = new HashSet<CellPos>(shoreCells);

        // The planner asks about cells millions of times: precompute everything but the claims.
        var allowed = CellMask(p => map.IsInLocal(p, 2) && zone[p.X, p.Y] == Zone.Free && (IsIslandLayout || !OnLane(p))
                                    && !NearZone(p, 2, Zone.Base, Zone.BaseCore, Zone.Cliff, Zone.CliffTop, Zone.Tech, Zone.Resource));
        bool Valid(CellPos p) => map.IsValid(p) && allowed[p.X, p.Y] && !claimed.Contains(p);

        foreach (var (outline, centre) in outlines)
        {
            if (outline.Count < 3 || !outline.All(Valid))
                return false;

            ConnectedTilePlan plan = null;
            HashSet<CellPos> inside = null;
            // Try the winding/side that worked last time for this kind of body first.
            foreach (var (reversed, side) in ShoreOrientations(isLand))
            {
                var verts = reversed ? Enumerable.Reverse(outline).ToList() : outline;
                var result = ConnectedTilePlanner.Plan(shoreType, verts, side, planSeed, useEndPieces: false, closed: true, Valid, ShoreSearchLimit);
                if (!result.Success)
                    continue;
                var cells = FoundationCells(result.Plan.Placements);
                var flood = FloodInside(centre, cells, body.Count * 3 + 400);
                if (flood == null || flood.Count < 6 || !ShoreFacesWater(result.Plan, flood, cells, waterInside: !isLand))
                    continue;
                (plan, inside) = (result.Plan, flood);
                workingShoreOrientation[isLand ? 1 : 0] = (reversed, side);
                break;
            }
            if (plan == null)
                return false;

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
