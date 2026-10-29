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

        int perSide = (int)Math.Round(settings.Garrisons * (IsBlitz ? 2 : 4));
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
    /// A winding dirt trail from each base towards its nearest tech or gem field, as on many of the
    /// shipped maps. Trails are cosmetic but keep trees off the route.
    /// </summary>
    private void PlaceTrails()
    {
        if (!settings.Trails)
            return;
        var roadType = RoadType();
        if (roadType == null)
            return;

        for (int k = 0; k < canonicalStarts.Count; k++)
        {
            var start = canonicalStarts[k];
            var targets = structures.Where(s => s.Position.DistanceTo(start) is > 14 and < 45).Select(s => s.Position)
                .Concat(GemFieldCentres().Where(g => g.DistanceTo(start) is > 14 and < 45))
                .OrderBy(t => t.DistanceTo(start)).ToList();
            if (targets.Count == 0)
                continue;
            var target = targets[rng.Next(Math.Min(2, targets.Count))];
            TryPlaceTrail(roadType, start, target);
        }
    }

    private IEnumerable<CellPos> GemFieldCentres()
    {
        var gems = new HashSet<int>(gemOverlays ?? new List<int>());
        var cells = map.AllValidCells().Where(p => overlay[p.X, p.Y] >= 0 && gems.Contains(overlay[p.X, p.Y])).ToList();
        // Rough cluster centres: every 6th cell is plenty for choosing a destination.
        return cells.Where((_, i) => i % 6 == 0);
    }

    private bool TryPlaceTrail(ConnectedTileType roadType, CellPos start, CellPos target)
    {
        double dx = target.X - start.X, dy = target.Y - start.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 10)
            return false;
        double ux = dx / len, uy = dy / len;
        // From just inside the base edge to a few cells short of the target, with a kink in the middle.
        var a = new CellPos(start.X + (int)Math.Round(ux * (BaseRadius - 2)), start.Y + (int)Math.Round(uy * (BaseRadius - 2)));
        var b = new CellPos(target.X - (int)Math.Round(ux * 4), target.Y - (int)Math.Round(uy * 4));
        double bend = (rng.NextDouble() - 0.5) * len * 0.35;
        var mid = new CellPos((a.X + b.X) / 2 + (int)Math.Round(-uy * bend), (a.Y + b.Y) / 2 + (int)Math.Round(ux * bend));
        var path = new List<CellPos> { a, mid, b };

        int seed = rng.Next();
        var plans = new List<(ConnectedTilePlan Plan, int Level)>();
        var claimed = new HashSet<CellPos>();
        for (int t = 0; t < sym.Order; t++)
        {
            var imagePath = path.Select(p => sym.Apply(t, p)).ToList();
            if (!IsFlatGround(imagePath[0]))
                return false;
            int level = GroundLevel(imagePath[0]);
            bool Valid(CellPos p) => IsUsable(p) && IsFlatGround(p) && GroundLevel(p) == level && !claimed.Contains(p)
                                     && zone[p.X, p.Y] is Zone.Free or Zone.Open or Zone.Base;
            if (!imagePath.All(Valid))
                return false;

            var result = ConnectedTilePlanner.Plan(roadType, imagePath, ConnectedTileSide.Front, seed, useEndPieces: false, closed: false, Valid);
            if (!result.Success)
                return false;
            claimed.UnionWith(FoundationCells(result.Plan.Placements));
            plans.Add((result.Plan, level));
        }

        foreach (var (plan, level) in plans)
        {
            foreach (var p in CliffBuilder.Apply(map, theater, plan, level, new Random(seed)))
            {
                tilePlaced[p.X, p.Y] = true;
                if (zone[p.X, p.Y] == Zone.Free)
                    zone[p.X, p.Y] = Zone.Open;
            }
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
    private void PaintGround()
    {
        // Urban/lunar LAT sets do not blend like the natural ones (hard-edged blocks in renders).
        if (map.Theater is not ("TEMPERATE" or "SNOW" or "DESERT"))
            return;

        var grounds = new List<(int Ground, int Transition)>();
        foreach (var (tileKey, latKey) in new[] { ("RoughTile", "ClearToRoughLat"), ("SandTile", "ClearToSandLat"), ("GreenTile", "ClearToGreenLat") })
        {
            int g = theater.GeneralInt(tileKey), l = theater.GeneralInt(latKey);
            if (g < 0 || l < 0)
                continue;
            var gt = theater.Tile(theater.TileId(g, 0));
            var lt = theater.Tile(theater.TileId(l, 0));
            if (gt == null || gt.Missing || lt == null || lt.Missing || theater.TileSet(l).TilesInSet < 16)
                continue;
            grounds.Add((g, l));
        }
        if (grounds.Count == 0)
            return;

        int seed = rng.Next();
        var painted = new int[map.Span, map.Span];
        foreach (var p in map.AllValidCells())
        {
            if (tilePlaced[p.X, p.Y] || !heights.IsFlat(p))
                continue;
            var c = terrain.Canonical(p);
            for (int i = 0; i < grounds.Count && i < 2; i++)
            {
                double n = ValueNoise(c.X / 9.0, c.Y / 9.0, seed + i * 31) * 0.7 + ValueNoise(c.X / 4.0, c.Y / 4.0, seed + i * 31 + 7) * 0.3;
                // Rough ground also gathers around ore fields, as on the shipped maps.
                bool nearOre = i == 0 && Disk(p, 2.5).Any(q => map.IsValid(q) && zone[q.X, q.Y] == Zone.Resource);
                if (n > 0.66 || nearOre && n > 0.45)
                {
                    painted[p.X, p.Y] = i + 1;
                    break;
                }
            }
        }

        foreach (var p in map.AllValidCells())
        {
            int g = painted[p.X, p.Y];
            if (g == 0)
                continue;
            var (ground, transition) = grounds[g - 1];
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
