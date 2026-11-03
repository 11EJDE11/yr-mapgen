using MapGen.Model;

namespace MapGen.Generation;

/// <summary>
/// Dressing: the small things that make a map read as a place rather than a layout. Lamps, props
/// round villages and tech, telephone poles along the trails, parked civilian cars, clutter
/// overlays, concrete walls round some contested oil, rocks at the cliff feet, battle damage near
/// the contested ground, and a little variety in the lighting.
/// </summary>
internal sealed partial class GenerationRun
{
    // Visible street lamps (all theaters) and the invisible coloured lights that give a glow at night.
    private static readonly string[] VisibleLamps = { "GALITE" };
    private static readonly string[] NightGlow = { "INYELWLAMP", "INORANLAMP", "INBLULMP", "INGRNLMP" };

    // Props by theater: placed round village houses and tech buildings.
    private static readonly Dictionary<string, string[]> PropsByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMPERATE"] = new[] { "CAMISC01", "CAMISC02", "CAMISC04", "CAPARK01", "CAFARM02", "CAWT01", "CAMSC06", "CAMSC11", "CASIN03E", "CASIN03S" },
        ["SNOW"] = new[] { "CAMISC01", "CAMISC02", "CAMISC04", "CAFARM02", "CAWT01", "CAMSC06", "CAMSC11", "CAPARK01" },
        ["DESERT"] = new[] { "CAMISC01", "CAMISC02", "CAMSC06", "CAMSC08", "CAMSC09", "CAMSC11", "CAWT01", "CAMISC05" },
        ["URBAN"] = new[] { "CAMISC03", "CAMISC04", "CASTRT01", "CASTRT05", "CAURB01", "CAURB02", "CAPARK01", "CABARR01", "CABARR02", "CAMISC01", "CAPARS07" },
        ["NEWURBAN"] = new[] { "CAMISC03", "CAMISC04", "CASTRT01", "CASTRT05", "CAURB01", "CAURB02", "CAPARK01", "CAMISC01", "CAMSC11" },
        ["LUNAR"] = new[] { "CAMISC01", "CAMISC02", "CAMISC05" },
    };

    // Poles and lights that line a road, by theater: an E/W pair for roads running along x, N/S for y.
    private static readonly Dictionary<string, (string[] AlongX, string[] AlongY)> RoadsideByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMPERATE"] = (new[] { "CAPOL01E", "CAPOL01W" }, new[] { "CAPOL01N", "CAPOL01S" }),
        ["SNOW"] = (new[] { "CAPOL01E", "CAPOL01W" }, new[] { "CAPOL01N", "CAPOL01S" }),
        ["DESERT"] = (new[] { "CAPOL01E", "CAPOL01W" }, new[] { "CAPOL01N", "CAPOL01S" }),
        ["URBAN"] = (new[] { "CALIT01E", "CALIT01W" }, new[] { "CALIT01N", "CALIT01S" }),
        ["NEWURBAN"] = (new[] { "CALIT03E", "CALIT03W" }, new[] { "CALIT03N", "CALIT03S" }),
    };

    private static readonly string[] Flags = { "CAUSFGL", "CARUFGL", "CAFRFGL", "CAGBFGL", "CAGEFGL", "CAKOFGL", "CALBFGL", "CASKFGL", "CAIRFGL", "CACUFGL" };

    private static readonly string[] Clutter = { "DRUM01", "DRUM02", "PALET01", "PALET02", "PALET03", "PALET04", "CRAT01", "CRAT02", "CRAT03", "CRAT04" };

    // Civilian vehicles that exist in Yuri's Revenge, parked as decoration.
    // (the types the shipped maps use most).
    private static readonly string[] CivilianCars = { "CAR", "STANG", "PTRUCK", "SUVB", "PICK", "SUVW", "CONA", "EUROC", "BUS", "TAXI", "LIMO", "TRUCKA", "JEEP", "WINI" };

    /// <summary>Buildings ringed with a concrete wall: the connectivity check must not try to reach them.</summary>
    private readonly HashSet<StructureObject> walledOff = new();

    private readonly List<StructureObject> villageHouses = new();

    private void PlaceDressing()
    {
        PlaceOilWalls();
        PlaceLamps();
        PlaceProps();
        PlaceRoadsidePoles();
        PlaceClutter();
        PlaceCars();
        PlaceCliffRocks();
    }

    private bool Available(string name) => theater.Building(name)?.Available == true;

    /// <summary>
    /// A free, flat 1x1 spot near a point: not on a road, and not near a fence or wall (a prop in a
    /// fence's gap would shut the building in).
    /// </summary>
    private IEnumerable<CellPos> SpotsNear(CellPos at, double radius)
        => Disk(at, radius).Where(p => IsUsable(p) && IsFlatGround(p) && !roadCells.Contains(p) && zone[p.X, p.Y] is Zone.Free or Zone.Open
                                       && overlay[p.X, p.Y] < 0 && !terrainObjects.Any(t => t.Position == p) && !NearZone(p, 2, Zone.Wall)).OrderBy(_ => rng.Next());

    private bool TryPlaceProp(string name, CellPos at)
    {
        if (!Available(name))
            return false;
        var (w, h) = theater.Building(name).FoundationSize;
        return TryPlaceStructure(name, new CellPos(at.X - w / 2, at.Y - h / 2), -1, z => z is Zone.Free or Zone.Open, margin: 0);
    }

    // ---------------------------------------------------------------- walls round contested oil

    /// <summary>
    /// Some contested oil derricks come walled in: a ring of concrete wall with no gap, so whoever
    /// wants them has to break in first. Each derrick and all of its copies, or none.
    /// </summary>
    private void PlaceOilWalls()
    {
        if (!settings.WallsAroundTech)
            return;
        var wallTypes = new[] { "GAWALL", "NAWALL" }.Select(theater.OverlayIndex).Where(i => i >= 0).ToList();
        if (wallTypes.Count == 0)
            return;

        // Contested oils come in symmetric groups: a decision per group.
        var oils = sharedTech.Where(s => IsOil(s.Type)).ToList();
        var seen = new HashSet<StructureObject>();
        foreach (var oil in oils)
        {
            if (seen.Contains(oil))
                continue;
            var group = oils.Where(o => sym.Orbit(oil.Position).Any(img => img.DistanceTo(o.Position) < 3)).ToList();
            if (group.Count == 0)
                group.Add(oil);
            foreach (var g in group)
                seen.Add(g);
            if (rng.NextDouble() > 0.35)
                continue;

            int wall = wallTypes[rng.Next(wallTypes.Count)];
            var (w, h) = theater.Building(oil.Type).FoundationSize;
            var rings = new List<List<CellPos>>();
            bool ok = true;
            foreach (var s in group)
            {
                var ring = new List<CellPos>();
                for (int dx = -1; dx <= w; dx++)
                    for (int dy = -1; dy <= h; dy++)
                        if (dx == -1 || dy == -1 || dx == w || dy == h)
                            ring.Add(new CellPos(s.Position.X + dx, s.Position.Y + dy));
                if (!ring.All(c => IsUsable(c) && IsFlatGround(c) && zone[c.X, c.Y] is Zone.Free or Zone.Open && overlay[c.X, c.Y] < 0))
                {
                    ok = false;
                    break;
                }
                rings.Add(ring);
            }
            if (!ok)
                continue;

            for (int i = 0; i < group.Count; i++)
            {
                var set = rings[i].ToHashSet();
                foreach (var c in rings[i])
                {
                    int frame = 0;
                    foreach (var (o, bit) in IsoNeighbours)
                        if (set.Contains(c + o))
                            frame |= bit;
                    overlay[c.X, c.Y] = (short)wall;
                    overlayData[c.X, c.Y] = (byte)frame;
                    zone[c.X, c.Y] = Zone.Wall;
                }
                walledOff.Add(group[i]);
            }
        }
    }

    // ---------------------------------------------------------------- lamps

    /// <summary>
    /// At dusk and at night, a light by the centre building and by the villages: a street lamp, or a
    /// coloured glow. Not by day (lamps then only wash the ground out) and not many: lights add up.
    /// </summary>
    private void PlaceLamps()
    {
        // Only at night: at dusk the lights already wash the ground out (snow most of all).
        if (settings.Time != TimeOfDay.Night)
            return;
        var lamp = VisibleLamps.FirstOrDefault(Available);
        var glows = NightGlow.Where(Available).ToList();
        var spots = new List<CellPos>();
        var centre = MapCentre();
        var central = sharedTech.OrderBy(s => s.Position.DistanceTo(centre)).FirstOrDefault();
        if (central != null && central.Position.DistanceTo(centre) < 10)
            spots.Add(central.Position);
        // One per village: the houses were placed in groups, so one in every few.
        for (int i = 0; i < villageHouses.Count; i += 4)
            spots.Add(villageHouses[i].Position);

        foreach (var at in spots)
        {
            bool glow = settings.Time == TimeOfDay.Night && glows.Count > 0 && rng.NextDouble() < 0.5;
            string type = glow ? glows[rng.Next(glows.Count)] : lamp;
            if (type == null)
                continue;
            foreach (var p in SpotsNear(at, 4).Take(6))
                if (TryPlaceProp(type, p))
                    break;
        }
    }

    // ---------------------------------------------------------------- props

    /// <summary>Barrels, mailboxes, benches, silos and the like round village houses and tech, and a flag at some tech.</summary>
    private void PlaceProps()
    {
        if (!PropsByTheater.TryGetValue(map.Theater, out var names))
            return;
        var props = names.Where(Available).ToList();
        if (props.Count == 0)
            return;

        foreach (var house in villageHouses)
        {
            int count = rng.Next(0, 3);
            for (int i = 0; i < count; i++)
                foreach (var p in SpotsNear(house.Position, 3.5).Take(5))
                    if (TryPlaceProp(props[rng.Next(props.Count)], p))
                        break;
        }

        var flags = Flags.Where(Available).ToList();
        foreach (var s in sharedTech.Where(s => !walledOff.Contains(s)))
        {
            if (rng.NextDouble() < 0.5)
                foreach (var p in SpotsNear(s.Position, 4).Take(5))
                    if (TryPlaceProp(props[rng.Next(props.Count)], p))
                        break;
            if (flags.Count > 0 && rng.NextDouble() < 0.3)
                foreach (var p in SpotsNear(s.Position, 4).Take(5))
                    if (TryPlaceProp(flags[rng.Next(flags.Count)], p))
                        break;
        }
    }

    // ---------------------------------------------------------------- poles along the trails

    /// <summary>Telephone poles (street lights in the cities) along the roads, one cell off the road every several cells.</summary>
    private void PlaceRoadsidePoles()
    {
        if (!RoadsideByTheater.TryGetValue(map.Theater, out var kinds) || roadCells.Count == 0)
            return;
        var alongX = kinds.AlongX.Where(Available).ToList();
        var alongY = kinds.AlongY.Where(Available).ToList();
        if (alongX.Count == 0 || alongY.Count == 0)
            return;

        var placed = new List<CellPos>();
        foreach (var r in roadCells.OrderBy(c => c.X + c.Y * 3))
        {
            if (placed.Any(p => p.DistanceTo(r) < 7) || rng.NextDouble() > 0.5)
                continue;
            // Which way the road runs here, from its neighbours.
            bool runsX = roadCells.Contains(r + new CellPos(1, 0)) || roadCells.Contains(r + new CellPos(-1, 0));
            bool runsY = roadCells.Contains(r + new CellPos(0, 1)) || roadCells.Contains(r + new CellPos(0, -1));
            if (runsX == runsY)
                continue;
            var side = runsX ? new CellPos(0, rng.Next(2) == 0 ? 2 : -2) : new CellPos(rng.Next(2) == 0 ? 2 : -2, 0);
            var at = r + side;
            if (!IsUsable(at) || !IsFlatGround(at) || roadCells.Contains(at) || zone[at.X, at.Y] is not (Zone.Free or Zone.Open) || overlay[at.X, at.Y] >= 0)
                continue;
            var list = runsX ? alongX : alongY;
            if (TryPlaceProp(list[rng.Next(list.Count)], at))
                placed.Add(at);
        }
    }

    // ---------------------------------------------------------------- clutter overlays

    /// <summary>Drums, pallets and crates in the lee of tech buildings and village houses.</summary>
    private void PlaceClutter()
    {
        var kinds = Clutter.Select(theater.OverlayIndex).Where(i => i >= 0).ToList();
        if (kinds.Count == 0)
            return;
        var hosts = sharedTech.Where(s => !walledOff.Contains(s)).Concat(villageHouses).Concat(structures.Where(s => IsOil(s.Type) && !walledOff.Contains(s))).ToList();
        foreach (var s in hosts)
        {
            if (rng.NextDouble() > 0.5)
                continue;
            int n = rng.Next(1, 4);
            foreach (var p in SpotsNear(s.Position, 3).Take(n))
            {
                // Copies for every image of the host, like the host itself.
                foreach (var q in sym.Orbit(p))
                {
                    if (!map.IsValid(q) || overlay[q.X, q.Y] >= 0 || zone[q.X, q.Y] is not (Zone.Free or Zone.Open) || roadCells.Contains(q))
                        continue;
                    overlay[q.X, q.Y] = (short)kinds[rng.Next(kinds.Count)];
                    overlayData[q.X, q.Y] = 0;
                    // Clutter blocks a cell: treated like a rock so a corridor is never closed by it.
                    zone[q.X, q.Y] = Zone.Rock;
                }
            }
        }
    }

    // ---------------------------------------------------------------- parked cars

    /// <summary>A civilian car or two parked by the villages, as neutral units.</summary>
    private void PlaceCars()
    {
        if (map.Theater == "LUNAR")
            return;
        var byVillage = new HashSet<CellPos>();
        for (int i = 0; i < villageHouses.Count; i += 2)
        {
            var house = villageHouses[i];
            if (rng.NextDouble() > 0.6)
                continue;
            var spot = SpotsNear(house.Position, 4).FirstOrDefault(p => roadCells.Any(r => r.DistanceTo(p) <= 1.5) && !byVillage.Contains(p));
            if (spot == default)
                spot = SpotsNear(house.Position, 3).FirstOrDefault();
            if (spot == default)
                continue;
            string type = CivilianCars[rng.Next(CivilianCars.Length)];
            int facing = rng.Next(0, 256);
            units.Add(new UnitObject { Type = type, Position = spot, Facing = facing });
            byVillage.Add(spot);
            zone[spot.X, spot.Y] = Zone.Open;
        }
    }

    // ---------------------------------------------------------------- rocks at the cliff feet

    /// <summary>Fallen rocks along the foot of the cliffs, sparse, off the routes.</summary>
    private void PlaceCliffRocks()
    {
        var rocks = RocksByTheater.TryGetValue(map.Theater, out var r)
            ? r.Select(theater.OverlayIndex).Where(i => i >= 0).ToArray()
            : Array.Empty<int>();
        if (rocks.Length == 0)
            return;
        foreach (var p in map.AllValidCells())
        {
            if (!IsUsable(p) || zone[p.X, p.Y] != Zone.Open || !IsFlatGround(p) || overlay[p.X, p.Y] >= 0 || OnRoute(p) || roadCells.Contains(p))
                continue;
            if (!CellPos.Neighbours8.Take(4).Any(o => map.IsValid(p + o) && zone[p.X + o.X, p.Y + o.Y] == Zone.Cliff))
                continue;
            if (rng.NextDouble() > 0.06)
                continue;
            overlay[p.X, p.Y] = (short)rocks[rng.Next(rocks.Length)];
            overlayData[p.X, p.Y] = 0;
            zone[p.X, p.Y] = Zone.Rock;
        }
    }
}
