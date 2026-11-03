using MapGen.Model;
using MapGen.Terrain;

namespace MapGen.Generation;

/// <summary>
/// Decoration and the smaller gameplay objects: sandbag rings around shared tech, garrisonable
/// civilian buildings, dirt trails, tree cover, rocks, ground patches and craters. Palettes follow
/// World-Altering Editor's YR collections and what the shipped maps use per theater.
/// </summary>
internal sealed partial class GenerationRun
{
    private static readonly Dictionary<string, string[]> TreesByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        // DeciduousTrees + ConiferTrees (WAE TerrainObjectCollections.ini)
        ["TEMPERATE"] = new[] { "TREE01", "TREE02", "TREE03", "TREE04", "TREE05", "TREE06", "TREE07", "TREE08", "TREE09", "TREE10", "TREE11", "TREE12", "TREE13", "TREE14", "TREE15", "TREE25", "TREE26", "TREE27" },
        ["SNOW"] = new[] { "TREE20", "TREE21", "TREE22", "TREE23", "TREE24", "TREE25", "TREE26", "TREE27", "TREE28", "TREE07", "TREE10", "TREE11" },
        ["URBAN"] = new[] { "TREE01", "TREE02", "TREE03", "TREE04", "TREE05", "TREE06", "TREE07", "TREE08", "TREE09", "TREE10", "TREE11", "TREE12" },
        ["NEWURBAN"] = new[] { "TREE01", "TREE02", "TREE03", "TREE04", "TREE05", "TREE06", "TREE20", "TREE21", "TREE22", "TREE23", "TREE24" },
        // Tropical, cacti, exotic and dead trees
        ["DESERT"] = new[] { "TREE20", "TREE21", "TREE22", "TREE23", "TREE24", "TREE28", "TREE29", "TREE30", "TREE31", "TREE32", "TREE33", "TREE34", "TREE35", "TREE36" },
        ["LUNAR"] = Array.Empty<string>(),
    };

    private static readonly Dictionary<string, string[]> RocksByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMPERATE"] = new[] { "TROCK01", "TROCK02", "TROCK03", "TROCK04", "TROCK05" },
        ["SNOW"] = new[] { "TROCK01", "TROCK02", "TROCK03", "TROCK04", "TROCK05" },
        ["URBAN"] = new[] { "TROCK01", "TROCK02", "TROCK03" },
        ["NEWURBAN"] = new[] { "TROCK01", "TROCK02", "TROCK03" },
        ["DESERT"] = new[] { "SROCK01", "SROCK02", "SROCK03", "SROCK04", "SROCK05" },
        ["LUNAR"] = new[] { "LUNRK1", "LUNRK2", "LUNRK3", "LUNRK4", "LUNRK5" },
    };

    private static readonly string[] SmallCraters = { "CRATER03", "CRATER04", "CRATER05", "CRATER06", "CRATER07", "CRATER08", "CRATER09", "CRATER10" };
    private static readonly string[] Burns = { "BURNT01", "BURNT02", "BURNT03", "BURNT04", "BURNT05", "BURNT06" };

    // Neighbour bits shared by LAT transitions and wall frames: NE=1, SE=2, SW=4, NW=8.
    private static readonly (CellPos Offset, int Bit)[] IsoNeighbours =
    {
        (new CellPos(0, -1), 1), (new CellPos(1, 0), 2), (new CellPos(0, 1), 4), (new CellPos(-1, 0), 8),
    };

    private readonly List<StructureObject> sharedTech = new();
    private readonly List<CellPos> wallCells = new();

    /// <summary>Cells covered by trail and village road tiles (their verges are painted, props line them).</summary>
    private readonly HashSet<CellPos> roadCells = new();

    private string[] Trees => TreesByTheater.TryGetValue(map.Theater, out var t)
        ? t.Where(n => theater.TerrainType(n)?.Available == true).ToArray()
        : Array.Empty<string>();

    // ---------------------------------------------------------------- sandbags around shared tech

    /// <summary>
    /// Rings of sandbags (crushable walls) around contested and centre tech, with a gap facing each
    /// team so infantry and engineers can get in.
    /// </summary>
    private void PlaceTechWalls()
    {
        if (!settings.WallsAroundTech || theater.OverlayIndex("GASAND") < 0)
            return;

        var teamStarts = players.GroupBy(p => p.Team).Select(g => g.Select(p => p.Position).ToList()).ToList();
        foreach (var s in sharedTech)
        {
            var (w, h) = theater.Building(s.Type).FoundationSize;
            var ring = new List<CellPos>();
            for (int dx = -2; dx <= w + 1; dx++)
                for (int dy = -2; dy <= h + 1; dy++)
                    if (dx == -2 || dy == -2 || dx == w + 1 || dy == h + 1)
                        ring.Add(new CellPos(s.Position.X + dx, s.Position.Y + dy));

            var gaps = new HashSet<CellPos>();
            foreach (var starts in teamStarts)
            {
                var nearestStart = starts.OrderBy(p => p.DistanceTo(s.Position)).First();
                var gapCentre = ring.OrderBy(c => c.DistanceTo(nearestStart)).First();
                foreach (var c in ring.Where(c => c.DistanceTo(gapCentre) <= 1.5))
                    gaps.Add(c);
            }

            foreach (var c in ring)
            {
                if (gaps.Contains(c) || !IsUsable(c) || !IsFlatGround(c) || zone[c.X, c.Y] is not (Zone.Free or Zone.Open or Zone.Base))
                    continue;
                zone[c.X, c.Y] = Zone.Wall;
                wallCells.Add(c);
            }
        }
    }

    private void WriteWalls()
    {
        int sandbags = theater.OverlayIndex("GASAND");
        var set = wallCells.ToHashSet();
        foreach (var c in wallCells)
        {
            int frame = 0;
            foreach (var (o, bit) in IsoNeighbours)
                if (set.Contains(c + o))
                    frame |= bit;
            overlay[c.X, c.Y] = (short)sandbags;
            overlayData[c.X, c.Y] = (byte)frame;
        }
    }

    // ---------------------------------------------------------------- garrisonable buildings

    private void PlaceGarrisons()
    {
        if (settings.Garrisons <= 0 || theater.Garrisons.Count == 0)
            return;

        var palette = theater.Garrisons
            .Select(n => theater.Building(n))
            .Where(b => b != null && b.FoundationSize.W == b.FoundationSize.H) // square ones mirror exactly
            .ToList();
        if (palette.Count == 0)
            return;

        int perSide = (int)Math.Round(settings.Garrisons * 4);
        int placed = 0;
        for (int attempt = 0; attempt < perSide * 40 && placed < perSide; attempt++)
        {
            var b = palette[rng.Next(palette.Count)];
            var (w, h) = b.FoundationSize;
            // Between the sides (where fights happen), or near a player's outer fields.
            var at = rng.NextDouble() < 0.6
                ? PickContestedPoint(BaseRadius + 5)
                : new CellPos(rng.Next(1, map.Span), rng.Next(1, map.Span));
            if (!IsUsable(at) || players.Any(p => p.Position.DistanceTo(at) < BaseRadius + 4))
                continue;
            var origin = new CellPos(at.X - w / 2, at.Y - h / 2);
            if (TryPlaceStructure(b.Name, origin, -1, z => z is Zone.Free or Zone.Open, margin: 1))
                placed++;
        }
    }

    // ---------------------------------------------------------------- dirt trails

    private ConnectedTileType RoadType()
    {
        string name = map.Theater switch
        {
            "SNOW" => "SnowDirtRoads",
            "URBAN" or "NEWURBAN" => "SmallPavedRoads",
            _ => "DirtRoads",
        };
        return connectedTypes?.FirstOrDefault(t => t.IniName == name);
    }

    /// <summary>
    /// Dirt trails from each base to the tech and gem fields placed for that player, as on many of
    /// the shipped maps. Targets in roughly the same direction share one trail that calls at each of
    /// them in turn; targets in different directions get trails of their own. Shared and contested
    /// features get no trail. Trails are cosmetic but keep trees off the route.
    /// </summary>
    private void PlaceTrails()
    {
        if (!settings.Trails)
            return;
        var roadType = RoadType();
        if (roadType == null)
            return;

        const int MaxTrailsPerPlayer = 3;
        foreach (var pl in players)
        {
            var start = pl.Position;
            var targets = playerTargets.Where(t => t.Player == pl && t.Centre.DistanceTo(start) > BaseCoreRadius + 3)
                .DistinctBy(t => t.Centre).OrderBy(t => t.Centre.DistanceTo(start)).ToList();

            foreach (var cluster in ClusterByDirection(start, targets).Take(MaxTrailsPerPlayer))
            {
                // The whole chain first; if the planner cannot route it, settle for the nearest target.
                if (!TryPlaceTrail(roadType, start, cluster) && cluster.Count > 1)
                    TryPlaceTrail(roadType, start, cluster.Take(1).ToList());
            }
        }
        PlaceTrailNetwork(roadType);
    }

    /// <summary>
    /// Groups targets (nearest first) that lie within a narrow angle of each other as seen from the
    /// start and are not far apart, so one trail serves a tech building and the gem field behind it.
    /// </summary>
    private static List<List<(PlayerSlot Player, CellPos Centre, double Radius)>> ClusterByDirection(CellPos start, List<(PlayerSlot Player, CellPos Centre, double Radius)> targets)
    {
        const double MaxAngle = 35 * Math.PI / 180;
        const double MaxGap = 16;
        var clusters = new List<List<(PlayerSlot Player, CellPos Centre, double Radius)>>();
        foreach (var t in targets)
        {
            double angle = Math.Atan2(t.Centre.Y - start.Y, t.Centre.X - start.X);
            var home = clusters.FirstOrDefault(c =>
            {
                var last = c[^1];
                double a = Math.Atan2(last.Centre.Y - start.Y, last.Centre.X - start.X);
                double diff = Math.Abs(Math.IEEERemainder(angle - a, 2 * Math.PI));
                return diff < MaxAngle && last.Centre.DistanceTo(t.Centre) < MaxGap;
            });
            if (home != null)
                home.Add(t);
            else
                clusters.Add(new List<(PlayerSlot Player, CellPos Centre, double Radius)> { t });
        }
        return clusters;
    }

    /// <summary>
    /// Lays one trail from just outside the start position past each target in turn, trying a few
    /// bends before giving up. The trail ends at the edge of the last target.
    /// </summary>
    private bool TryPlaceTrail(ConnectedTileType roadType, CellPos start, List<(PlayerSlot Player, CellPos Centre, double Radius)> targets)
    {
        var first = targets[0].Centre;
        double dx = first.X - start.X, dy = first.Y - start.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 6)
            return false;
        double ux = dx / len, uy = dy / len;

        // A few cells out from the start position: the construction yard and starting units sit there.
        var a = new CellPos(start.X + (int)Math.Round(ux * 4), start.Y + (int)Math.Round(uy * 4));

        // Calling points: at the near edge of each target, approached from the previous point.
        var stops = new List<CellPos>();
        var from = a;
        foreach (var t in targets)
        {
            double ex = t.Centre.X - from.X, ey = t.Centre.Y - from.Y, el = Math.Sqrt(ex * ex + ey * ey);
            if (el < t.Radius + 2)
                break;
            double back = t.Radius + 1;
            var edge = new CellPos(t.Centre.X - (int)Math.Round(ex / el * back), t.Centre.Y - (int)Math.Round(ey / el * back));
            if (edge == from)
                break;
            stops.Add(edge);
            from = edge;
        }
        // A trail of a cell or two (tech right outside the base) looks odd: leave it out.
        if (stops.Count == 0 || a.DistanceTo(stops[^1]) < 6)
            return false;

        // A kink in the first leg, in one direction, the other, or none.
        double bend = (rng.NextDouble() * 0.5 + 0.5) * a.DistanceTo(stops[0]) * 0.35;
        foreach (double b in new[] { bend, -bend, 0 })
        {
            var path = new List<CellPos> { a };
            if (b != 0)
            {
                var mid = new CellPos((a.X + stops[0].X) / 2 + (int)Math.Round(-uy * b), (a.Y + stops[0].Y) / 2 + (int)Math.Round(ux * b));
                if (mid != a && mid != stops[0])
                    path.Add(mid);
            }
            path.AddRange(stops);
            if (TryPlaceTrailPath(roadType, path))
                return true;
        }
        return false;
    }

    /// <summary>One trail for one player. Trails are cosmetic tiles, so they may run through the base, core included.</summary>
    private bool TryPlaceTrailPath(ConnectedTileType roadType, List<CellPos> path)
    {
        if (!IsFlatGround(path[0]))
            return false;
        int level = GroundLevel(path[0]);
        bool Valid(CellPos p) => IsUsable(p) && IsFlatGround(p) && GroundLevel(p) == level
                                 && zone[p.X, p.Y] is Zone.Free or Zone.Open or Zone.Base or Zone.BaseCore;
        if (!path.All(Valid))
            return false;

        int seed = rng.Next();
        var result = ConnectedTilePlanner.Plan(roadType, path, ConnectedTileSide.Front, seed, useEndPieces: false, closed: false, Valid);
        if (!result.Success)
            return false;

        foreach (var p in CliffBuilder.Apply(map, theater, result.Plan, level, new Random(seed)))
        {
            tilePlaced[p.X, p.Y] = true;
            if (zone[p.X, p.Y] == Zone.Free)
                zone[p.X, p.Y] = Zone.Open;
            roadCells.Add(p);
        }
        return true;
    }

    // ---------------------------------------------------------------- scattered trees and rocks

    /// <summary>Single trees and the odd rock over open ground (symmetric, since both block movement).</summary>
    private void PlaceScatter()
    {
        var rocks = RocksByTheater.TryGetValue(map.Theater, out var r)
            ? r.Select(theater.OverlayIndex).Where(i => i >= 0).ToArray()
            : Array.Empty<int>();
        double treeChance = 0.018 * settings.Trees;
        double rockChance = 0.003;

        foreach (var p in map.AllValidCells())
        {
            if (!IsUsable(p) || !terrain.IsCanonical(p) || zone[p.X, p.Y] != Zone.Free || OnLane(p))
                continue;
            if (players.Any(pl => pl.Position.DistanceTo(p) < BaseRadius))
                continue;

            double roll = rng.NextDouble();
            bool tree = roll < treeChance && Trees.Length > 0;
            bool rock = !tree && roll < treeChance + rockChance && rocks.Length > 0 && IsFlatGround(p);
            if (!tree && !rock)
                continue;

            int rockType = rock ? rocks[rng.Next(rocks.Length)] : -1;
            foreach (var q in terrain.Orbit(p))
            {
                if (!map.IsValid(q) || zone[q.X, q.Y] != Zone.Free)
                    continue;
                if (tree)
                {
                    zone[q.X, q.Y] = Zone.Obstacle;
                }
                else
                {
                    zone[q.X, q.Y] = Zone.Rock;
                    overlay[q.X, q.Y] = (short)rockType;
                    overlayData[q.X, q.Y] = 0;
                }
            }
        }
    }

    // ---------------------------------------------------------------- ground patches and craters

    /// <summary>
    /// Patches of the theater's LAT grounds (rough grass, sand...) with automatic transition tiles,
    /// following WAE's AutoLAT rules: a painted cell uses the ground tile when all four iso neighbours
    /// are the same ground, otherwise transition tile N where N has a bit per differing neighbour.
    /// </summary>
    /// <summary>The LAT grounds of a theater, by role. What each looks like differs per theater (see PaintGround).</summary>
    private enum Ground { None, Rough, Sand, Green, Pave }

    private (int Ground, int Lat)? GroundSets(Ground g)
    {
        var (tileKey, latKey) = g switch
        {
            Ground.Rough => ("RoughTile", "ClearToRoughLat"),
            Ground.Sand => ("SandTile", "ClearToSandLat"),
            Ground.Green => ("GreenTile", "ClearToGreenLat"),
            _ => ("PaveTile", "ClearToPaveLat"),
        };
        int t = theater.GeneralInt(tileKey), l = theater.GeneralInt(latKey);
        if (t < 0 || l < 0)
            return null;
        var gt = theater.Tile(theater.TileId(t, 0));
        var lt = theater.Tile(theater.TileId(l, 0));
        if (gt == null || gt.Missing || lt == null || lt.Missing || theater.TileSet(l).TilesInSet < 16)
            return null;
        return (t, l);
    }

    /// <summary>
    /// Ground variety and blending. Patches of the theater's other grounds from noise, a verge of
    /// the matching ground round shores and roads so their built-in edges blend into the clear
    /// ground, and the LAT transition tiles along every edge. Per theater the sets look like:
    ///   Temperate: Rough = dark grass, Sand = dirt, Green = sand, Pave = concrete.
    ///   Urban (clear is concrete): Rough = dirt, Sand = gravel, Green = grass, Pave = brick.
    ///   NewUrban (clear is grass): Rough = dark grass, Sand = dirt, Green = sand, Pave = earth.
    ///   Snow: Rough = grass, Sand = rock, Green = ice, Pave = pavement.
    ///   Desert: Rough = scrub, Sand = rough sand, Green = light sand, Pave = concrete.
    /// </summary>
    private void PaintGround()
    {
        if (Environment.GetEnvironmentVariable("MAPGEN_GROUND_TEST") == "1")
        {
            PaintGroundTest();
            return;
        }
        if (map.Theater == "LUNAR")
            return;

        // Noise patches: (ground, scale, threshold). Lower threshold = more of it.
        var patches = map.Theater switch
        {
            "URBAN" => new[] { (Ground.Green, 14.0, 0.56), (Ground.Rough, 7.0, 0.72), (Ground.Pave, 9.0, 0.74), (Ground.Sand, 6.0, 0.78) },
            "NEWURBAN" => new[] { (Ground.Rough, 9.0, 0.66), (Ground.Sand, 7.0, 0.74), (Ground.Pave, 8.0, 0.78) },
            "SNOW" => new[] { (Ground.Rough, 9.0, 0.68), (Ground.Sand, 7.0, 0.72), (Ground.Green, 10.0, 0.76) },
            "DESERT" => new[] { (Ground.Sand, 9.0, 0.64), (Ground.Rough, 7.0, 0.74), (Ground.Green, 11.0, 0.72) },
            _ => new[] { (Ground.Rough, 9.0, 0.66), (Ground.Sand, 7.0, 0.70), (Ground.Green, 12.0, 0.80) },
        };
        // Verges: the ground that matches a shore's beach and a road's shoulder.
        Ground shoreVerge = map.Theater switch { "TEMPERATE" => Ground.Green, "URBAN" => Ground.Green, "SNOW" => Ground.Green, _ => Ground.None };
        Ground roadVerge = map.Theater switch { "NEWURBAN" => Ground.Sand, "TEMPERATE" => Ground.Sand, "DESERT" => Ground.Sand, "SNOW" => Ground.Rough, _ => Ground.None };

        var sets = new Dictionary<Ground, (int Ground, int Lat)>();
        foreach (var g in new[] { Ground.Rough, Ground.Sand, Ground.Green, Ground.Pave })
            if (GroundSets(g) is { } gs)
                sets[g] = gs;

        int seed = rng.Next();
        var painted = new Ground[map.Span, map.Span];
        bool Paintable(CellPos p) => map.IsValid(p) && !tilePlaced[p.X, p.Y] && heights.IsFlat(p);

        foreach (var p in map.AllValidCells())
        {
            if (!Paintable(p))
                continue;
            var c = terrain.Canonical(p);
            for (int i = 0; i < patches.Length; i++)
            {
                var (g, scale, threshold) = patches[i];
                if (!sets.ContainsKey(g))
                    continue;
                double n = ValueNoise(c.X / scale, c.Y / scale, seed + i * 31) * 0.7 + ValueNoise(c.X / (scale * 0.45), c.Y / (scale * 0.45), seed + i * 31 + 7) * 0.3;
                // Rough ground also gathers around ore fields, as on the shipped maps.
                bool nearOre = i == 0 && Disk(p, 2.5).Any(q => map.IsValid(q) && zone[q.X, q.Y] == Zone.Resource);
                if (n > threshold || nearOre && n > threshold - 0.2)
                {
                    painted[p.X, p.Y] = g;
                    break;
                }
            }
        }

        // Verges override patches: one cell always, a second one where the noise says so, for an
        // edge that is not a straight band.
        void Verge(Func<CellPos, bool> source, Ground g)
        {
            if (g == Ground.None || !sets.ContainsKey(g))
                return;
            var ring = new List<CellPos>();
            foreach (var p in map.AllValidCells())
            {
                if (!Paintable(p))
                    continue;
                bool near1 = CellPos.Neighbours8.Any(o => source(p + o));
                bool near2 = !near1 && Disk(p, 2.3).Any(source) && ValueNoise(p.X / 5.0, p.Y / 5.0, seed + 99) > 0.45;
                if (near1 || near2)
                    ring.Add(p);
            }
            foreach (var p in ring)
                painted[p.X, p.Y] = g;
        }
        Verge(q => map.IsValid(q) && zone[q.X, q.Y] == Zone.Water, shoreVerge);
        Verge(q => roadCells.Contains(q), roadVerge);

        foreach (var p in map.AllValidCells())
        {
            var g = painted[p.X, p.Y];
            if (g == Ground.None)
                continue;
            var (ground, transition) = sets[g];
            int mask = 0;
            foreach (var (o, bit) in IsoNeighbours)
            {
                var q = p + o;
                if (!map.IsValid(q) || painted[q.X, q.Y] != g)
                    mask |= bit;
            }
            ref var cell = ref map[p];
            cell.TileIndex = mask == 0 ? theater.TileId(ground, 0) : theater.TileId(transition, mask);
            cell.SubTile = 0;
        }
    }

    /// <summary>Debugging aid: a disc of each LAT ground with its transition ring, in a row across the map.</summary>
    private void PaintGroundTest()
    {
        var keys = new[] { ("RoughTile", "ClearToRoughLat"), ("SandTile", "ClearToSandLat"), ("GreenTile", "ClearToGreenLat"), ("PaveTile", "ClearToPaveLat") };
        var centre = MapCentre();
        for (int i = 0; i < keys.Length; i++)
        {
            int g = theater.GeneralInt(keys[i].Item1), l = theater.GeneralInt(keys[i].Item2);
            if (g < 0 || l < 0)
                continue;
            var at = new CellPos(centre.X + (i - 1) * 14 - 7, centre.Y - (i - 1) * 14 + 7);
            var disc = Disk(at, 6).Where(p => map.IsValid(p) && !tilePlaced[p.X, p.Y] && heights.IsFlat(p)).ToHashSet();
            foreach (var p in disc)
            {
                int mask = 0;
                foreach (var (o, bit) in IsoNeighbours)
                    if (!disc.Contains(p + o))
                        mask |= bit;
                ref var cell = ref map[p];
                cell.TileIndex = mask == 0 ? theater.TileId(g, 0) : theater.TileId(l, mask);
                cell.SubTile = 0;
            }
            Console.Error.WriteLine($"GROUND {keys[i].Item1}: set {g} '{theater.TileSet(g)?.Name}' with LAT {l} '{theater.TileSet(l)?.Name}' at {at}");
        }
    }

    private void PlaceSmudges()
    {
        foreach (var p in map.AllValidCells())
        {
            if (!IsUsable(p) || zone[p.X, p.Y] is not (Zone.Free or Zone.Open) || tilePlaced[p.X, p.Y] || !heights.IsFlat(p))
                continue;
            if (rng.NextDouble() > 0.004)
                continue;
            var list = rng.NextDouble() < 0.7 ? SmallCraters : Burns;
            map.Smudges.Add(new SmudgeObject { Type = list[rng.Next(list.Length)], Position = p });
        }
    }
}
