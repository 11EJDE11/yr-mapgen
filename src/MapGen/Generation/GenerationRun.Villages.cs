using MapGen.Model;
using MapGen.Terrain;

namespace MapGen.Generation;

/// <summary>
/// Texture that the shipped maps have and plain generation lacks: trails between bases and towards
/// the centre, villages (a few civilian buildings on a short road with a fence), and bridges where
/// the routes cross a river.
/// </summary>
internal sealed partial class GenerationRun
{
    // ---------------------------------------------------------------- trail network

    /// <summary>
    /// Beyond each player's own trails: one to the centre building (when there is one) and one to the
    /// nearest teammate, so every trail joins two things and the trails read as a road network.
    /// </summary>
    private void PlaceTrailNetwork(ConnectedTileType roadType)
    {
        var centre = MapCentre();
        var centreBuilding = sharedTech.OrderBy(s => s.Position.DistanceTo(centre)).FirstOrDefault(s => s.Position.DistanceTo(centre) < 8);
        var done = new HashSet<(PlayerSlot, PlayerSlot)>();
        foreach (var pl in players)
        {
            if (centreBuilding != null && rng.NextDouble() < 0.5)
            {
                var (w, h) = theater.Building(centreBuilding.Type).FoundationSize;
                var target = new CellPos(centreBuilding.Position.X + w / 2, centreBuilding.Position.Y + h / 2);
                TryPlaceTrail(roadType, pl.Position, new List<(PlayerSlot, CellPos, double)> { (pl, target, Math.Max(w, h) / 2.0 + 2.5) });
            }

            var mate = players.Where(o => o != pl && o.Team == pl.Team).OrderBy(o => o.Position.DistanceTo(pl.Position)).FirstOrDefault();
            if (mate != null && !done.Contains((mate, pl)) && rng.NextDouble() < 0.6)
            {
                done.Add((pl, mate));
                TryPlaceTrail(roadType, pl.Position, new List<(PlayerSlot, CellPos, double)> { (pl, mate.Position, -1) });
            }
        }
    }

    // ---------------------------------------------------------------- villages

    private static readonly Dictionary<string, string[]> FencesByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMPERATE"] = new[] { "CAFNCW", "CAFNCB" },
        ["SNOW"] = new[] { "CAFNCW", "CAFNCB" },
        ["DESERT"] = new[] { "CAFNCB", "CAFNCP" },
        ["URBAN"] = new[] { "CAFNCP", "CAFNCB" },
        ["NEWURBAN"] = new[] { "CAFNCP", "CAFNCB" },
    };

    /// <summary>
    /// A village: three to five civilian buildings either side of a trail, out on the open ground
    /// between the bases, with a fence round one of them. Villages without a trail nearby are a
    /// cluster of houses (no road stub that leads nowhere). Symmetric, like garrisons.
    /// </summary>
    private void PlaceVillages()
    {
        if (settings.Villages <= 0)
            return;
        var palette = theater.Garrisons.Select(n => theater.Building(n))
            .Where(b => b != null && b.FoundationSize.W <= 3 && b.FoundationSize.H <= 3).ToList();
        if (palette.Count == 0)
            return;

        bool Site(CellPos at) => IsUsable(at) && players.All(p => p.Position.DistanceTo(at) >= BaseRadius + 8) && at.DistanceTo(MapCentre()) >= 12;
        var roadSites = roadCells.Where(Site).OrderBy(_ => rng.Next()).ToList();

        int perSide = (int)Math.Round(settings.Villages * 2);
        int placed = 0;
        for (int attempt = 0; attempt < perSide * 30 && placed < perSide; attempt++)
        {
            bool onRoad = roadSites.Count > 0 && rng.NextDouble() < 0.75;
            var at = onRoad ? roadSites[rng.Next(roadSites.Count)] : PickContestedPoint(BaseRadius + 10);
            if (!Site(at) || !onRoad && (zone[at.X, at.Y] != Zone.Free || OnRoute(at)))
                continue;
            if (TryPlaceVillage(at, onRoad, palette))
                placed++;
        }
    }

    private bool TryPlaceVillage(CellPos at, bool onRoad, List<Theater.BuildingInfo> palette)
    {
        // Along the road where there is one (its local direction), otherwise any direction.
        double ux, uy;
        if (onRoad)
        {
            var near = roadCells.Where(r => r.DistanceTo(at) < 6).ToList();
            double sx = 0, sy = 0, sxy = 0, mx = near.Average(r => r.X), my = near.Average(r => r.Y);
            foreach (var r in near)
            {
                sx += (r.X - mx) * (r.X - mx);
                sy += (r.Y - my) * (r.Y - my);
                sxy += (r.X - mx) * (r.Y - my);
            }
            double theta = 0.5 * Math.Atan2(2 * sxy, sx - sy);
            ux = Math.Cos(theta);
            uy = Math.Sin(theta);
        }
        else
        {
            double angle = rng.NextDouble() * Math.PI;
            ux = Math.Cos(angle);
            uy = Math.Sin(angle);
        }
        int half = 5 + rng.Next(0, 3);
        var roadNear = onRoad ? roadCells.Where(r => r.DistanceTo(at) < half + 4).ToList() : new List<CellPos>();

        // Buildings either side of the line, spaced along it.
        int count = 3 + rng.Next(0, 3);
        int built = 0;
        var houses = new List<List<StructureObject>>();
        for (int i = 0; i < count; i++)
        {
            double f = (i + 0.5) / count * 2 - 1;      // -1..1 along the line
            int side = (i % 2 == 0) ? 1 : -1;
            var building = palette[rng.Next(palette.Count)];
            var (w, h) = building.FoundationSize;
            double off = 2.5 + Math.Max(w, h) / 2.0;
            var centre = new CellPos(
                (int)Math.Round(at.X + ux * half * f - uy * side * off),
                (int)Math.Round(at.Y + uy * half * f + ux * side * off));
            var origin = new CellPos(centre.X - w / 2, centre.Y - h / 2);
            int before = structures.Count;
            if (TryPlaceStructure(building.Name, origin, -1, z => z is Zone.Free or Zone.Open, margin: 1))
            {
                built++;
                houses.Add(structures.Skip(before).ToList());
                villageHouses.AddRange(structures.Skip(before));
            }
        }
        if (built < 2)
            return false;

        // A fence round one house (a ring two cells out, with a gap towards the road).
        if (houses.Count > 0 && FencesByTheater.TryGetValue(map.Theater, out var fences))
        {
            int fence = fences.Select(theater.OverlayIndex).FirstOrDefault(i => i >= 0, -1);
            if (fence >= 0 && rng.NextDouble() < 0.7)
                foreach (var copy in houses[rng.Next(houses.Count)])
                    PlaceFence(copy, fence, roadNear.Count > 0 ? roadNear : new List<CellPos> { at });
        }
        return true;
    }

    /// <summary>A fence ring round one building, two cells out, with a gap on the side nearest the road.</summary>
    private void PlaceFence(StructureObject s, int fence, List<CellPos> roadCells)
    {
        var (w, h) = theater.Building(s.Type).FoundationSize;
        var ring = new List<CellPos>();
        for (int dx = -2; dx <= w + 1; dx++)
            for (int dy = -2; dy <= h + 1; dy++)
                if (dx == -2 || dy == -2 || dx == w + 1 || dy == h + 1)
                    ring.Add(new CellPos(s.Position.X + dx, s.Position.Y + dy));
        var gapCentre = roadCells.Count > 0 ? ring.OrderBy(c => roadCells.Min(r => r.DistanceTo(c))).First() : ring[0];
        var cells = ring.Where(c => c.DistanceTo(gapCentre) > 1.5 && IsUsable(c) && IsFlatGround(c) && zone[c.X, c.Y] is Zone.Free or Zone.Open).ToList();
        var set = cells.ToHashSet();
        foreach (var c in cells)
        {
            int frame = 0;
            foreach (var (o, bit) in IsoNeighbours)
                if (set.Contains(c + o))
                    frame |= bit;
            overlay[c.X, c.Y] = (short)fence;
            overlayData[c.X, c.Y] = (byte)frame;
            zone[c.X, c.Y] = Zone.Wall;
        }
    }

    // ---------------------------------------------------------------- bridges

    /// <summary>Whether this map gets a river (decided with the map; dropped if the starts cannot keep clear of it).</summary>
    private bool riverPlanned;

    /// <summary>Cells under a river (water the routes must cross): set while the land mask is built.</summary>
    private HashSet<CellPos> riverCells;

    /// <summary>Water or beach: what a bridge deck spans (the ramp pieces sit on the dry ground beyond).</summary>
    private bool IsWaterTile(CellPos p) => map.IsValid(p) && zone[p.X, p.Y] == Zone.Water;

    /// <summary>
    /// Low bridges where each player's route to the centre crosses the river: three lanes wide, a
    /// ramp piece on the land at either end, deck pieces over the water. The bridge cells stay
    /// walkable for the connectivity check and the fairness analyser.
    /// </summary>
    private void PlaceBridges()
    {
        if (riverCells == null || riverCells.Count == 0)
            return;
        var ewStart = theater.OverlayIndex("LOBRDB21"); var ewEnd = theater.OverlayIndex("LOBRDB19");
        var nsStart = theater.OverlayIndex("LOBRDB23"); var nsEnd = theater.OverlayIndex("LOBRDB25");
        var ew = new[] { "LOBRDB01", "LOBRDB02", "LOBRDB03", "LOBRDB04" }.Select(theater.OverlayIndex).Where(i => i >= 0).ToList();
        var ns = new[] { "LOBRDB10", "LOBRDB11", "LOBRDB12", "LOBRDB13" }.Select(theater.OverlayIndex).Where(i => i >= 0).ToList();
        if (ewStart < 0 || ewEnd < 0 || nsStart < 0 || nsEnd < 0 || ew.Count == 0 || ns.Count == 0)
            return;

        var bridged = new HashSet<CellPos>();
        var centre = MapCentre();
        int built = 0;
        if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
        {
            Console.Error.WriteLine($"BRIDGES river cells {riverCells.Count}");
            var pl0 = players[0];
            double ddx = centre.X - pl0.Position.X, ddy = centre.Y - pl0.Position.Y, ll = Math.Max(1, Math.Sqrt(ddx * ddx + ddy * ddy));
            var line = new List<string>();
            for (double t = 0; t <= ll; t += 2)
            {
                var q = new CellPos((int)Math.Round(pl0.Position.X + ddx / ll * t), (int)Math.Round(pl0.Position.Y + ddy / ll * t));
                line.Add(map.IsValid(q) ? (IsWaterTile(q) ? "~" : zone[q.X, q.Y] == Zone.Water ? "s" : riverCells.Contains(q) ? "r" : ".") : "#");
            }
            Console.Error.WriteLine("BRIDGES route of player 0 to centre: " + string.Join("", line) + " (~ water, s shore, r river cell now land)");
        }
        // Each player's routes: to the centre, to the nearest enemy, and to that enemy's flanks (a
        // crossing away from the centre ford on either side). Wherever they first meet water.
        var routes = new List<(PlayerSlot Player, CellPos To)>();
        foreach (var pl in players)
        {
            routes.Add((pl, centre));
            var enemy = players.Where(o => o.Team != pl.Team).OrderBy(o => o.Position.DistanceTo(pl.Position)).FirstOrDefault();
            if (enemy == null)
                continue;
            routes.Add((pl, enemy.Position));
            // Flanks: perpendicular to the line to the enemy, a sixth of the map across.
            double dx = enemy.Position.X - pl.Position.X, dy = enemy.Position.Y - pl.Position.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double flank = map.Span / 6.0;
            foreach (int side in new[] { -1, 1 })
                routes.Add((pl, new CellPos((int)Math.Round(enemy.Position.X - dy / len * side * flank), (int)Math.Round(enemy.Position.Y + dx / len * side * flank))));
        }
        foreach (var (pl, to) in routes)
        {
            double dx = to.X - pl.Position.X, dy = to.Y - pl.Position.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            CellPos crossing = default;
            for (double t = 0; t <= len; t += 0.5)
            {
                var q = new CellPos((int)Math.Round(pl.Position.X + dx / len * t), (int)Math.Round(pl.Position.Y + dy / len * t));
                if (IsWaterTile(q))
                {
                    crossing = q;
                    break;
                }
            }
            // Only the river gets bridges; lakes are routed round. (The first water a route meets is
            // usually the river's beach, which is not in the river's open-water cells.)
            if (crossing != default && !Disk(crossing, 3).Any(riverCells.Contains))
                crossing = default;
            if (crossing == default || bridged.Any(b => b.DistanceTo(crossing) < 12))
            {
                if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    Console.Error.WriteLine($"BRIDGE player {pl.Waypoint}: {(crossing == default ? "route never meets water" : "already bridged nearby")}");
                continue;
            }

            // The shorter of the two axis-aligned spans through the crossing.
            var spanX = WaterSpan(crossing, new CellPos(1, 0));
            var spanY = WaterSpan(crossing, new CellPos(0, 1));
            if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                Console.Error.WriteLine($"BRIDGE player {pl.Waypoint}: crossing {crossing}, spans x {spanX.Count} y {spanY.Count}");
            foreach (var (span, alongX) in new[] { (spanX, true), (spanY, false) }.OrderBy(s => s.Item1.Count))
            {
                if (span.Count < 2 || span.Count > 20)
                    continue;
                // Slide across the river's width to find three parallel lanes that all fit.
                if (TryLayBridge(span, alongX, alongX ? (ewStart, ewEnd, ew) : (nsStart, nsEnd, ns), bridged))
                {
                    built++;
                    break;
                }
            }
        }
        if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
            Console.Error.WriteLine($"BRIDGES {built} built over {riverCells.Count} river cells");
    }

    /// <summary>Water cells in a straight line through <paramref name="at"/> along <paramref name="step"/>, in order.</summary>
    private List<CellPos> WaterSpan(CellPos at, CellPos step)
    {
        var cells = new List<CellPos>();
        var p = at;
        while (IsWaterTile(p - step))
            p -= step;
        while (IsWaterTile(p))
        {
            cells.Add(p);
            p += step;
        }
        return cells;
    }

    private bool TryLayBridge(List<CellPos> span, bool alongX, (int Start, int End, List<int> Pieces) set, HashSet<CellPos> bridged)
    {
        var step = alongX ? new CellPos(1, 0) : new CellPos(0, 1);
        var across = alongX ? new CellPos(0, 1) : new CellPos(1, 0);
        var first = span[0] - step;
        var last = span[^1] + step;
        // The ramp pieces sit on dry ground at water level with nothing on it: the land side of a
        // shore tile, or flat clear ground.
        bool LandOk(CellPos p) => IsUsable(p) && zone[p.X, p.Y] is Zone.Free or Zone.Open && map[p].Level == 0
                                  && (tilePlaced[p.X, p.Y] || IsFlatGround(p));

        foreach (int shift in new[] { 0, -1, 1, -2, 2 })
        {
            var lanes = new List<List<CellPos>>();
            bool ok = true;
            for (int lane = -1; lane <= 1 && ok; lane++)
            {
                // Each lane spans its own run of open water (a river crossed at an angle has lanes of
                // slightly different length); the ends then sit on that lane's banks.
                var offset = new CellPos(across.X * (lane + shift), across.Y * (lane + shift));
                var own = WaterSpan(span[span.Count / 2] + offset, step);
                if (own.Count < 2 || Math.Abs(own.Count - span.Count) > 2)
                {
                    ok = false;
                    lanes.Add(new List<CellPos>());
                    continue;
                }
                var cells = new List<CellPos> { own[0] - step };
                cells.AddRange(own);
                cells.Add(own[^1] + step);
                // Well clear of every other bridge: two side by side read as one overlapping mess.
                ok = LandOk(cells[0]) && LandOk(cells[^1]) && cells.All(c => !bridged.Any(b => b.DistanceTo(c) < 9));
                if (!ok && Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
                    Console.Error.WriteLine($"    lane {lane} shift {shift}: ends {cells[0]}={zone[cells[0].X, cells[0].Y]}/level {map[cells[0]].Level}/{(IsUsable(cells[0]) ? "usable" : "unusable")}, {cells[^1]}={zone[cells[^1].X, cells[^1].Y]}/level {map[cells[^1]].Level}; span {own.Count}");
                lanes.Add(cells);
            }
            if (!ok)
                continue;

            // Open water on both sides of the bridge, the length of its middle: a bridge along the
            // edge of a lake, with land just beside it, is a bridge to nowhere.
            int deck = lanes[1].Count - 2;
            int wet = 0, checkedCells = 0;
            for (int i = 1 + deck / 4; i <= deck - deck / 4; i++)
            {
                var left = lanes[0][Math.Min(i, lanes[0].Count - 2)] - new CellPos(across.X * 3, across.Y * 3);
                var right = lanes[2][Math.Min(i, lanes[2].Count - 2)] + new CellPos(across.X * 3, across.Y * 3);
                checkedCells++;
                if (IsWaterTile(left) && IsWaterTile(right))
                    wet++;
            }
            if (checkedCells == 0 || wet < checkedCells * 0.75)
                continue;

            for (int lane = 0; lane < 3; lane++)
            {
                var cells = lanes[lane];
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    overlay[c.X, c.Y] = (short)(i == 0 ? set.Start : i == cells.Count - 1 ? set.End : set.Pieces[rng.Next(set.Pieces.Count)]);
                    overlayData[c.X, c.Y] = (byte)lane;
                    // Walkable: the bridge deck carries traffic over the water.
                    zone[c.X, c.Y] = Zone.Open;
                    bridged.Add(c);
                }
            }
            return true;
        }
        return false;
    }
}
