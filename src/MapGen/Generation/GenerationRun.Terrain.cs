using MapGen.Model;
using MapGen.Terrain;

namespace MapGen.Generation;

internal sealed partial class GenerationRun
{
    // Cells whose tile was set by terrain generation (cliffs); everything else is ground shaped by the height field.
    private bool[,] tilePlaced;

    // Desired ground level per cell before slopes are worked out.
    private int[,] targetLevel;

    private HeightField heights;

    /// <summary>Reachable raised ground (ramped plateau tops, terraces with a way up): where a reward is worth holding.</summary>
    private bool[,] highGround;

    private bool OnHighGround(CellPos p) => highGround != null && map.IsValid(p) && highGround[p.X, p.Y];

    private const int PlateauHeight = 4;

    private static readonly Dictionary<string, string[]> CliffTypesByTheater = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEMPERATE"] = new[] { "CliffSet" },
        ["SNOW"] = new[] { "CliffSet", "CliffIceSet" },
        ["DESERT"] = new[] { "CliffSet" },
        ["URBAN"] = new[] { "CliffStoneSet", "CliffConcreteSet" },
        ["NEWURBAN"] = new[] { "CliffNaturalSet", "CliffStoneSet" },
    };

    private int CanonicalUsableCells => map.AllValidCells().Count(IsUsable) / terrain.Order;

    /// <summary>
    /// Shapes the ground: cliff plateaus (some with a ramp up, some closed off), raised highlands with
    /// sloped edges, and rolling hills. Bases stay flat at ground level. Afterwards the height field
    /// decides every cell's level and slope, so later steps can ask whether ground is flat.
    /// </summary>
    private void PlanTerrain()
    {
        targetLevel = new int[map.Span, map.Span];
        highGround = new bool[map.Span, map.Span];

        bool mountainous = settings.Layout == MapLayout.Mountainous;
        double cliffs = mountainous ? Math.Max(settings.Cliffs, 0.9) : settings.Cliffs;
        double hills = mountainous ? Math.Max(settings.Hills, 0.85) : settings.Hills;

        // Terraces come first: the level map decides where lakes may go and where cliffs run.
        var cliffType = connectedTypes?.FirstOrDefault(t =>
            CliffTypesByTheater.TryGetValue(map.Theater, out var names) && names.Contains(t.IniName));
        if (cliffStyle == CliffStyle.Terraced && cliffs > 0 && cliffType != null)
            PlanTerraces(cliffs);
        if (!mountainous)
            PlanWater();
        if (raised != null)
            BuildTerraceCliffs(cliffType);
        if (cliffs > 0)
            PlaceCliffPlateaus(cliffs);
        if (hills > 0)
            PlaceRollingTerrain(hills);

        heights = new HeightField(map, theater);
        heights.Build(targetLevel, tilePlaced);
        heights.Relax();
    }

    /// <summary>Flat, non-cliff ground (needed for ore, buildings and bases).</summary>
    private bool IsFlatGround(CellPos p) => map.IsValid(p) && !tilePlaced[p.X, p.Y] && heights.IsFlat(p);

    private int GroundLevel(CellPos p) => heights.LevelOf(p);

    // ---------------------------------------------------------------- cliff plateaus

    private void PlaceCliffPlateaus(double amount)
    {
        var cliffType = connectedTypes?.FirstOrDefault(t =>
            CliffTypesByTheater.TryGetValue(map.Theater, out var names) && names.Contains(t.IniName));
        // Lunar has no cliff set in WAE's YR configuration; that theater simply gets no cliffs.
        if (cliffType == null)
            return;

        // Tactical cliffs first, so they get the spots that matter; scenery then fills in round them
        // at a lower rate.
        double scenic = amount;
        if (cliffStyle == CliffStyle.Tactical)
        {
            PlaceTacticalCliffs(cliffType, amount);
            scenic *= 0.25;
        }
        // On terraced maps the cliffs are the borders; a few plateaus on the lowland is plenty.
        if (cliffStyle == CliffStyle.Terraced)
            scenic *= 0.15;

        int target = (int)Math.Round(scenic * CanonicalUsableCells / 520.0);
        target = Math.Max(1, target);
        int placed = 0;

        for (int attempt = 0; attempt < target * 80 + 200 && placed < target; attempt++)
        {
            // A mix of plateaus with a ramp up (usable high ground), closed plateaus and long ridges
            // (scenery that shapes the paths between them). Ramps use the "Slope Set Pieces", which
            // only fit the regular cliff set.
            // Ramped plateaus are high ground to fight over, so they follow the feature symmetry even when
            // the rest of the terrain is free (and count once per copy).
            double roll = rng.NextDouble();
            if (roll < 0.45 && cliffType.IniName == "CliffSet")
            {
                if (TryPlaceRampedPlateau(cliffType))
                    placed += sym.Order / terrain.Order;
            }
            else if (TryPlaceCliffPlateau(cliffType, ridge: roll > 0.7))
            {
                placed++;
            }
        }

        // Too little room for cliffs (small islands, crowded maps) is not a fairness problem.
        if (Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1")
            Console.Error.WriteLine($"CLIFFS scenic placed {placed} of {target} ({cliffType.IniName})");
    }

    private bool TryPlaceCliffPlateau(ConnectedTileType cliffType, bool ridge)
    {
        var centre = new CellPos(rng.Next(1, map.Span), rng.Next(1, map.Span));
        if (!IsUsable(centre) || !terrain.IsCanonical(centre) || zone[centre.X, centre.Y] != Zone.Free)
            return false;

        // Half extents in screen-space "right" (1,-1) and "down" (1,1) steps, plus a rotation so
        // ridges can run diagonally.
        double scale = 1.0;
        double rx, ry;
        if (ridge)
        {
            rx = (7 + rng.NextDouble() * 7) * scale;
            ry = 2.5 + rng.NextDouble() * 1.5;
        }
        else
        {
            rx = (3.5 + rng.NextDouble() * 6) * scale;
            ry = (3 + rng.NextDouble() * 4.5) * scale;
        }
        double rotation = (rng.NextDouble() - 0.5) * (ridge ? Math.PI : 0.8);
        // One screen step is sqrt(2) cells; the outline is scaled by up to 1.15.
        double extent = Math.Max(rx, ry) * 1.15 * 1.414 + 2;

        // The planner keeps 3 cells from protected zones, so leave room for that past the base edge.
        if (players.Any(p => p.Position.DistanceTo(centre) < BaseRadius + extent + 4))
            return false;
        if (MapCentre().DistanceTo(centre) < extent + 4)
            return false;
        // A closed plateau must not swallow part of a lane either.
        if (lanes != null && Disk(centre, extent).Any(OnLane))
            return false;
        var images = Enumerable.Range(0, terrain.Order).Select(t => terrain.Apply(t, centre)).ToList();
        for (int i = 0; i < images.Count; i++)
            for (int j = i + 1; j < images.Count; j++)
                if (images[i].DistanceTo(images[j]) < extent * 2 + 3)
                    return false;

        var outline = PlateauOutline(rx, ry, rotation);
        return TryPlaceClosedCliff(cliffType, centre, outline, (int)(Math.PI * rx * ry * 4) + 80, terrain, t => terrain.Apply(t, centre), keepRoutes: false);
    }

    /// <summary>
    /// A convex outline in screen-space steps (r right, d down), counter-clockwise on screen starting
    /// at the bottom: with Front-side cliffs this raises the inside (verified by rendering every orientation).
    /// </summary>
    private List<(double r, double d)> PlateauOutline(double rx, double ry, double rotation)
    {
        int vertexCount = rng.Next(5, 9);
        double phase = rng.NextDouble() * 2 * Math.PI / vertexCount;
        var outline = new List<(double r, double d)>();
        for (int k = 0; k < vertexCount; k++)
        {
            double a = phase + k * 2 * Math.PI / vertexCount;
            double s = 0.8 + rng.NextDouble() * 0.35;
            double r = rx * Math.Cos(a) * s, d = ry * Math.Sin(a) * s;
            outline.Add((r * Math.Cos(rotation) - d * Math.Sin(rotation), r * Math.Sin(rotation) + d * Math.Cos(rotation)));
        }
        return outline;
    }

    private static CellPos ToCell(CellPos c, (double r, double d) v)
        => new(c.X + (int)Math.Round(v.r + v.d), c.Y + (int)Math.Round(-v.r + v.d));

    /// <summary>
    /// A closed cliff loop round <paramref name="outline"/> and every copy of it. Copy t is the
    /// canonical outline mapped through <paramref name="images"/> and moved so its centre lands on
    /// <paramref name="imageCentre"/>(t). The inside becomes an unreachable plateau top. All copies
    /// or none.
    /// </summary>
    private bool TryPlaceClosedCliff(ConnectedTileType cliffType, CellPos centre, List<(double r, double d)> outline, int floodLimit,
        Symmetry images, Func<int, CellPos> imageCentre, bool keepRoutes)
    {
        var canonicalVertices = outline.Select(v => ToCell(centre, v)).ToList();

        int planSeed = rng.Next();
        var results = new List<(ConnectedTilePlan plan, HashSet<CellPos> cliffCells, HashSet<CellPos> top)>();
        var claimed = new HashSet<CellPos>();

        for (int t = 0; t < images.Order; t++)
        {
            var at = imageCentre(t);
            var mapped = images.Apply(t, centre);
            var vertices = OrderCounterClockwise(canonicalVertices.Select(v =>
            {
                var q = images.Apply(t, v);
                return new CellPos(q.X + at.X - mapped.X, q.Y + at.Y - mapped.Y);
            }).ToList());

            // Scenery stays off every lane (free terrain); tactical cliffs only keep the routes to the
            // centre and to teammates open, since cutting the direct line to the enemy is their purpose.
            bool Valid(CellPos p) => IsUsable(p) && map.IsInLocal(p, 2) && zone[p.X, p.Y] == Zone.Free && !claimed.Contains(p)
                                     && (keepRoutes ? !OnRoute(p) : !OnLane(p)) && !NearProtected(p, 3) && !OnTerrace(p);

            if (!vertices.All(Valid))
            {
                var bad = vertices.First(v => !Valid(v));
                lastCliffFailure = "vertex " + (!IsUsable(bad) ? "unusable" : !map.IsInLocal(bad, 2) ? "edge" : zone[bad.X, bad.Y] != Zone.Free ? zone[bad.X, bad.Y].ToString()
                    : claimed.Contains(bad) ? "claimed" : keepRoutes && OnRoute(bad) ? "route" : OnLane(bad) ? "lane" : "protected");
                return false;
            }

            // The planner occasionally closes a loop the wrong way round (cliffs facing inwards);
            // retry from another starting vertex before giving up on this plateau.
            ConnectedTilePlan plan = null;
            HashSet<CellPos> cliffCells = null, top = null;
            string why = "";
            // Which way round a loop raises its inside depends on the cliff set (the urban stone set is
            // the mirror of the regular one). Each set is tried the way that worked last time first.
            bool reversed = cliffLoopReversed.GetValueOrDefault(cliffType.IniName);
            for (int retry = 0; retry < Math.Min(4, vertices.Count) * 2 && plan == null; retry++)
            {
                bool flip = retry >= Math.Min(4, vertices.Count);
                var order = reversed != flip ? Enumerable.Reverse(vertices).ToList() : vertices;
                int shift = retry % Math.Min(4, vertices.Count);
                var rotated = order.Skip(shift).Concat(order.Take(shift)).ToList();
                var result = ConnectedTilePlanner.Plan(cliffType, rotated, ConnectedTileSide.Front, planSeed + retry,
                    useEndPieces: false, closed: true, Valid);
                if (!result.Success)
                {
                    why = "planner " + result.Status;
                    continue;
                }

                var cells = FoundationCells(result.Plan.Placements);
                var inside = FloodInside(at, cells, floodLimit);
                if (inside == null || inside.Count < 6)
                {
                    why = "inside";
                    continue;
                }
                if (CountWrongWayCliffCells(result.Plan, inside, cells) > 0)
                {
                    why = "wrong way";
                    continue;
                }
                if (keepRoutes && inside.Any(OnRoute))
                {
                    why = "inside on route";
                    continue;
                }

                (plan, cliffCells, top) = (result.Plan, cells, inside);
                cliffLoopReversed[cliffType.IniName] = reversed != flip;
            }

            if (plan == null)
            {
                lastCliffFailure = "no plan: " + why;
                return false;
            }

            claimed.UnionWith(cliffCells);
            claimed.UnionWith(top);
            results.Add((plan, cliffCells, top));
        }

        foreach (var (plan, cliffCells, top) in results)
        {
            var touched = CliffBuilder.Apply(map, theater, plan, 0, new Random(planSeed));
            foreach (var p in touched)
            {
                tilePlaced[p.X, p.Y] = true;
                zone[p.X, p.Y] = Zone.Cliff;
            }

            // Plateau tops are unreachable scenery (trees on top, like Hidden Valley's ridges).
            foreach (var p in top)
            {
                if (tilePlaced[p.X, p.Y])
                    continue;
                targetLevel[p.X, p.Y] = PlateauHeight;
                zone[p.X, p.Y] = Zone.CliffTop;
            }

            // Keep a strip at the foot of the cliffs clear so they read as cliffs, not tree walls.
            foreach (var p in cliffCells)
                foreach (var q in Disk(p, 1.5))
                    if (map.IsValid(q) && zone[q.X, q.Y] == Zone.Free && !top.Contains(q))
                        zone[q.X, q.Y] = Zone.Open;
        }

        return true;
    }

    // ---------------------------------------------------------------- tactical cliffs

    /// <summary>
    /// Cliffs laid out along each player's route to the centre: a gateway (ridges either side of the
    /// route) and an overlook (a ramped plateau beside the route, ramp towards the owner). Each copy
    /// follows its own player's (possibly drifted) start, so it stays in front of that base.
    /// </summary>
    private string lastCliffFailure;

    /// <summary>Per cliff set: whether closed loops must run clockwise on screen to raise their inside.</summary>
    private readonly Dictionary<string, bool> cliffLoopReversed = new();

    private void PlaceTacticalCliffs(ConnectedTileType cliffType, double amount)
    {
        bool debug = Environment.GetEnvironmentVariable("MAPGEN_DEBUG") == "1";
        // Ramps use the "Slope Set Pieces", which only fit the regular cliff set.
        bool ramps = cliffType.IniName == "CliffSet";
        int perPlayer = (int)Math.Round(Math.Clamp(amount, 0, 1) * 2);

        for (int k = 0; k < canonicalStarts.Count; k++)
        {
            // The overlook goes first: it sits nearer the base, and the gateway then finds room beyond it.
            var kinds = new List<bool>(); // true = gateway, false = overlook
            if (perPlayer >= 2)
            {
                if (ramps)
                    kinds.Add(false);
                kinds.Add(true);
            }
            else if (perPlayer == 1)
                kinds.Add(!ramps || rng.NextDouble() < 0.5);

            foreach (bool gateway in kinds)
            {
                var reasons = new Dictionary<string, int>();
                bool ok = false;
                for (int attempt = 0; attempt < 40 && !ok; attempt++)
                {
                    lastCliffFailure = "precheck";
                    ok = gateway ? TryPlaceGateway(cliffType, k) : TryPlaceOverlook(cliffType, k);
                    if (!ok)
                        reasons[lastCliffFailure] = reasons.GetValueOrDefault(lastCliffFailure) + 1;
                }
                if (debug)
                    Console.Error.WriteLine($"TACTICAL player {k} {(gateway ? "gateway" : "overlook")}: {(ok ? "ok" : "FAILED")} {string.Join(", ", reasons.Select(r => $"{r.Key} x{r.Value}"))}");
            }
        }
    }

    /// <summary>Unit direction and length of canonical player <paramref name="k"/>'s route to the centre.</summary>
    private (CellPos Start, double Ux, double Uy, double Length) Route(int k)
    {
        var start = canonicalStarts[k];
        var c = MapCentre();
        double dx = c.X - start.X, dy = c.Y - start.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        return (start, dx / len, dy / len, len);
    }

    /// <summary>Copy t of a point laid out for player k: the mirrored point, moved with that copy's start.</summary>
    private Func<int, CellPos> WithStart(int k, CellPos canonical) => t =>
    {
        var m = sym.Apply(t, canonical);
        var (dx, dy) = Drift(sym.Apply(t, canonicalStarts[k]));
        return new CellPos(m.X + dx, m.Y + dy);
    };

    /// <summary>
    /// Two ridges across the route to the centre with the route itself open between them. One side
    /// alone is kept too (a flank wall) if the other will not fit.
    /// </summary>
    private bool TryPlaceGateway(ConnectedTileType cliffType, int k)
    {
        var (start, ux, uy, len) = Route(k);
        double scale = 1.0;
        // Shorter than scenic ridges: the pair plus the gap has to fit between a base and its teammate.
        // Thick enough for the chunkier cliff sets (urban stone) to close a loop.
        double rx = (4 + rng.NextDouble() * 3.5) * scale, ry = 3.5 + rng.NextDouble() * 1.0;
        // Far enough out that the ridges clear the base and its 3-cell margin, and short of the centre
        // by the ridge thickness plus room for the centre feature. Short routes get no gateway.
        double nearest = BaseRadius + 9, furthest = len - ry * 1.414 * 1.15 - 8;
        if (furthest < nearest)
            return false;
        // Nearer the base more often than not: a wall in front of the base reads as its gateway, a
        // wall at the centre as a fortress.
        double bias = rng.NextDouble();
        double along = nearest + bias * bias * (furthest - nearest);
        // The route in screen steps (r right, d down) is ((ux - uy) / 2, (ux + uy) / 2); the ridge's long
        // axis runs a quarter turn from it, straight across the route.
        double rotation = Math.Atan2(ux + uy, ux - uy) + Math.PI / 2;
        double halfGap = 5 + rng.NextDouble() * 2.5;
        // Ridge half-length in cells (a screen step is sqrt(2) cells, outlines scale up to 1.15).
        double offset = halfGap + rx * 1.414 * 1.1;
        var outline = PlateauOutline(rx, ry, rotation);
        int floodLimit = (int)(Math.PI * rx * ry * 4) + 80;

        bool any = false;
        foreach (int side in new[] { -1, 1 })
        {
            var centre = new CellPos(
                (int)Math.Round(start.X + ux * along - uy * side * offset),
                (int)Math.Round(start.Y + uy * along + ux * side * offset));
            if (!IsUsable(centre) || zone[centre.X, centre.Y] != Zone.Free)
                continue;
            if (TryPlaceClosedCliff(cliffType, centre, outline, floodLimit, sym, WithStart(k, centre), keepRoutes: true))
                any = true;
        }
        return any;
    }

    /// <summary>A ramped plateau beside the route to the centre, its ramp facing the owner's base.</summary>
    private bool TryPlaceOverlook(ConnectedTileType cliffType, int k)
    {
        var (start, ux, uy, len) = Route(k);
        double scale = 1.0;
        // At least 7: the 8-cell ramp gap has to fit in an edge with a cliff piece either side.
        int hx = (int)Math.Round((7 + rng.Next(0, 2)) * scale), hy = (int)Math.Round((7 + rng.Next(0, 2)) * scale);
        int h = Math.Max(hx, hy);
        // Clear of the route by the box half extent plus the ramp and its foot, which face the route:
        // units come up from the corridor, and the top looks down on it.
        double offset = h + 7;
        // The nearest corner of the box must clear the base and its 3-cell margin; the far side must
        // stay clear of the centre. Short routes get no overlook.
        double nearest = h + Math.Sqrt(Math.Max(0, (BaseRadius + 4) * (BaseRadius + 4) - (offset - h) * (offset - h)));
        double furthest = len - h - 6;
        if (furthest < nearest)
        {
            lastCliffFailure = "short route";
            return false;
        }
        double along = nearest + rng.NextDouble() * Math.Min(10, furthest - nearest);
        var onRoute = new CellPos((int)Math.Round(start.X + ux * along), (int)Math.Round(start.Y + uy * along));

        // Either side of the route, in random order: near the map edge only one of them has room.
        int first = rng.Next(2) == 0 ? -1 : 1;
        foreach (int side in new[] { first, -first })
        {
            var centre = new CellPos(
                (int)Math.Round(start.X + ux * along - uy * side * offset),
                (int)Math.Round(start.Y + uy * along + ux * side * offset));
            if (!IsUsable(centre) || zone[centre.X, centre.Y] != Zone.Free)
            {
                lastCliffFailure = !IsUsable(centre) ? "centre unusable" : "centre " + zone[centre.X, centre.Y];
                continue;
            }
            if (TryPlaceRampedBox(cliffType, centre, hx, hy, towards: onRoute, WithStart(k, centre), keepRoutes: true))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- plateaus with a ramp

    /// <summary>
    /// A cliff plateau you can drive onto: a cell-aligned box (a diamond on screen) whose cliff is an
    /// open path that starts and ends in "Slope Set Pieces" either side of a ramp gap; the height field
    /// fills the gap with slope tiles. The top is usable ground (ore, tech and bases can go there).
    /// </summary>
    private bool TryPlaceRampedPlateau(ConnectedTileType cliffType)
    {
        var centre = new CellPos(rng.Next(1, map.Span), rng.Next(1, map.Span));
        if (!IsUsable(centre) || !sym.IsCanonical(centre) || zone[centre.X, centre.Y] != Zone.Free)
            return false;

        double scale = 1.0;
        int hx = (int)Math.Round((6 + rng.Next(0, 4)) * scale), hy = (int)Math.Round((6 + rng.Next(0, 4)) * scale);
        double extent = Math.Sqrt(hx * hx + hy * hy) + 6;

        if (players.Any(p => p.Position.DistanceTo(centre) < BaseRadius + extent + 3))
            return false;
        if (MapCentre().DistanceTo(centre) < extent + 2)
            return false;
        var images = Enumerable.Range(0, sym.Order).Select(t => Image(t, centre)).ToList();
        for (int i = 0; i < images.Count; i++)
            for (int j = i + 1; j < images.Count; j++)
                if (images[i].DistanceTo(images[j]) < extent * 2 + 3)
                    return false;

        // The ramp faces the nearest base or the map centre.
        var towards = rng.NextDouble() < 0.6 ? players.OrderBy(p => p.Position.DistanceTo(centre)).First().Position : MapCentre();
        return TryPlaceRampedBox(cliffType, centre, hx, hy, towards, t => Image(t, centre), keepRoutes: false);
    }

    /// <summary>
    /// The ramped box itself and every copy of it: copy t is the box mapped through the feature
    /// symmetry and moved so its centre lands on <paramref name="imageCentre"/>(t). All copies or none.
    /// </summary>
    private bool TryPlaceRampedBox(ConnectedTileType cliffType, CellPos centre, int hx, int hy, CellPos towards,
        Func<int, CellPos> imageCentre, bool keepRoutes)
    {
        int dx = towards.X - centre.X, dy = towards.Y - centre.Y;
        var edge = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? RampEdge.SouthEast : RampEdge.NorthWest)
                                               : (dy > 0 ? RampEdge.SouthWest : RampEdge.NorthEast);

        // Ramp gap: two connection-point cells 8 apart along the chosen edge, roughly central.
        const int gapLength = 8;
        int along = edge is RampEdge.SouthWest or RampEdge.NorthEast ? hx : hy;
        if (along * 2 < gapLength + 6)
            return false;
        int offset = rng.Next(-(along - gapLength / 2 - 3), along - gapLength / 2 - 3 + 1);
        CellPos gapA, gapB;
        switch (edge)
        {
            case RampEdge.SouthWest: gapA = new(centre.X + offset - gapLength / 2, centre.Y + hy); gapB = new(gapA.X + gapLength, gapA.Y); break;
            case RampEdge.NorthEast: gapA = new(centre.X + offset - gapLength / 2, centre.Y - hy); gapB = new(gapA.X + gapLength, gapA.Y); break;
            case RampEdge.SouthEast: gapA = new(centre.X + hx, centre.Y + offset - gapLength / 2); gapB = new(gapA.X, gapA.Y + gapLength); break;
            default: gapA = new(centre.X - hx, centre.Y + offset - gapLength / 2); gapB = new(gapA.X, gapA.Y + gapLength); break;
        }
        var boxA = new CellPos(centre.X - hx, centre.Y - hy);
        var boxB = new CellPos(centre.X + hx, centre.Y + hy);

        int planSeed = rng.Next();
        var results = new List<(ConnectedTilePlan Plan, HashSet<CellPos> Cells, HashSet<CellPos> Top, HashSet<CellPos> Ramp, HashSet<CellPos> Foot)>();
        var claimed = new HashSet<CellPos>();

        for (int t = 0; t < sym.Order; t++)
        {
            // Mirrors and rotations map a box to a box and an edge to an edge, so the ramp is
            // re-planned on whichever edge it lands on in this copy. The whole copy then shifts so its
            // centre lands where the caller wants it (drift, or in front of a drifted base).
            var mapped = sym.Apply(t, centre);
            var at = imageCentre(t);
            CellPos Shifted(CellPos p) => new(p.X + at.X - mapped.X, p.Y + at.Y - mapped.Y);
            var a = Shifted(sym.Apply(t, boxA));
            var b = Shifted(sym.Apply(t, boxB));
            int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            var g1 = Shifted(sym.Apply(t, gapA));
            var g2 = Shifted(sym.Apply(t, gapB));
            var imageCentreCell = at;

            RampEdge e;
            if (g1.Y == y1 && g2.Y == y1) e = RampEdge.SouthWest;
            else if (g1.X == x1 && g2.X == x1) e = RampEdge.SouthEast;
            else if (g1.Y == y0 && g2.Y == y0) e = RampEdge.NorthEast;
            else if (g1.X == x0 && g2.X == x0) e = RampEdge.NorthWest;
            else return false;

            var type = CliffRamps.WithRampEnds(cliffType, theater, e);
            if (type == null)
                return false;

            // Counter-clockwise corners on screen: left, bottom, right, top. Edge k joins corner k and k+1.
            var corners = new[] { new CellPos(x0, y1), new CellPos(x1, y1), new CellPos(x1, y0), new CellPos(x0, y0) };
            int kEdge = e switch { RampEdge.SouthWest => 0, RampEdge.SouthEast => 1, RampEdge.NorthEast => 2, _ => 3 };
            // The path starts at the gap end nearer the edge's second corner and ends nearer its first.
            var (start, end) = g1.DistanceTo(corners[(kEdge + 1) % 4]) < g2.DistanceTo(corners[(kEdge + 1) % 4]) ? (g1, g2) : (g2, g1);
            var path = new List<CellPos> { start, corners[(kEdge + 1) % 4], corners[(kEdge + 2) % 4], corners[(kEdge + 3) % 4], corners[kEdge], end };

            var (ramp, foot) = RampCells(e, start, end, x0, x1, y0, y1);

            bool Valid(CellPos p) => IsUsable(p) && map.IsInLocal(p, 2) && zone[p.X, p.Y] == Zone.Free && !claimed.Contains(p)
                                     && !(keepRoutes && OnRoute(p)) && !NearProtected(p, 3) && !OnTerrace(p);
            if (!path.All(Valid) || !ramp.All(Valid) || !foot.All(Valid))
            {
                lastCliffFailure = "box invalid";
                return false;
            }

            var result = ConnectedTilePlanner.Plan(type, path, CliffRamps.SideOf(e), planSeed, useEndPieces: true, closed: false, Valid);
            if (!result.Success)
            {
                lastCliffFailure = "no ramp plan";
                return false;
            }
            var plan = result.Plan;
            if (!CliffRamps.IsRampPiece(plan.Placements[0].Tile) || !CliffRamps.IsRampPiece(plan.Placements[^1].Tile))
                return false;

            var cells = FoundationCells(plan.Placements);
            if (cells.Overlaps(ramp))
                return false;
            var top = FloodInside(imageCentreCell, cells.Concat(ramp).ToHashSet(), (2 * hx + 3) * (2 * hy + 3));
            if (top == null || top.Count < 20)
                return false;
            if (keepRoutes && top.Any(OnRoute))
            {
                lastCliffFailure = "top on route";
                return false;
            }

            var cliffOnly = new ConnectedTilePlan(plan.Type, plan.Origin,
                plan.Placements.Where(pl => !CliffRamps.IsRampPiece(pl.Tile)).ToList(), isClosed: false, usesEndPieces: false);
            if (CountWrongWayCliffCells(cliffOnly, top, cells) > 0)
                return false;

            claimed.UnionWith(cells);
            claimed.UnionWith(top);
            claimed.UnionWith(ramp);
            results.Add((plan, cells, top, ramp, foot));
        }

        foreach (var (plan, cells, top, ramp, foot) in results)
        {
            foreach (var p in CliffBuilder.Apply(map, theater, plan, 0, new Random(planSeed)))
            {
                tilePlaced[p.X, p.Y] = true;
                var sub = theater.Tile(map[p].TileIndex)?.GetSubTile(map[p].SubTile);
                // Slope pieces mix rock (the cliff end) with walkable ramp cells.
                zone[p.X, p.Y] = sub != null && sub.TerrainType is 7 or 8 or 15 ? Zone.Cliff : Zone.Open;
            }

            foreach (var p in top)
            {
                if (tilePlaced[p.X, p.Y])
                    continue;
                targetLevel[p.X, p.Y] = PlateauHeight;
                zone[p.X, p.Y] = Zone.Free;
                highGround[p.X, p.Y] = true;
            }

            // The gap: raised to the plateau level and left for the height field to slope down.
            foreach (var p in ramp)
            {
                targetLevel[p.X, p.Y] = PlateauHeight;
                zone[p.X, p.Y] = Zone.Open;
            }
            foreach (var p in foot)
                zone[p.X, p.Y] = Zone.Open;

            foreach (var p in cells)
                foreach (var q in Disk(p, 1.5))
                    if (map.IsValid(q) && zone[q.X, q.Y] == Zone.Free && !top.Contains(q))
                        zone[q.X, q.Y] = Zone.Open;
        }

        return true;
    }

    private static (HashSet<CellPos> Ramp, HashSet<CellPos> Foot) RampCells(RampEdge e, CellPos start, CellPos end, int x0, int x1, int y0, int y1)
    {
        var ramp = new HashSet<CellPos>();
        var foot = new HashSet<CellPos>();
        switch (e)
        {
            case RampEdge.SouthWest: // ramp strips at end.X + 2 and start.X - 2, rows y1-1..y1+2, descending to +y
                for (int x = end.X + 2; x <= start.X - 2; x++)
                {
                    for (int y = y1 - 1; y <= y1 + 2; y++)
                        if (x > end.X + 2 && x < start.X - 2) ramp.Add(new CellPos(x, y));
                    for (int y = y1 + 3; y <= y1 + 5; y++) foot.Add(new CellPos(x, y));
                }
                break;
            case RampEdge.SouthEast: // strips at start.Y + 2 and end.Y - 2, columns x1-1..x1+2, descending to +x
                for (int y = start.Y + 2; y <= end.Y - 2; y++)
                {
                    for (int x = x1 - 1; x <= x1 + 2; x++)
                        if (y > start.Y + 2 && y < end.Y - 2) ramp.Add(new CellPos(x, y));
                    for (int x = x1 + 3; x <= x1 + 5; x++) foot.Add(new CellPos(x, y));
                }
                break;
            case RampEdge.NorthEast: // strips at start.X + 2 and end.X - 2, rows y0-2..y0+1, descending to -y
                for (int x = start.X + 2; x <= end.X - 2; x++)
                {
                    for (int y = y0 - 2; y <= y0 + 1; y++)
                        if (x > start.X + 2 && x < end.X - 2) ramp.Add(new CellPos(x, y));
                    for (int y = y0 - 5; y <= y0 - 3; y++) foot.Add(new CellPos(x, y));
                }
                break;
            default: // NorthWest: strips at end.Y + 2 and start.Y - 2, columns x0-2..x0+1, descending to -x
                for (int y = end.Y + 2; y <= start.Y - 2; y++)
                {
                    for (int x = x0 - 2; x <= x0 + 1; x++)
                        if (y > end.Y + 2 && y < start.Y - 2) ramp.Add(new CellPos(x, y));
                    for (int x = x0 - 5; x <= x0 - 3; x++) foot.Add(new CellPos(x, y));
                }
                break;
        }
        return (ramp, foot);
    }

    private static HashSet<CellPos> FoundationCells(IEnumerable<ConnectedTilePlacement> placements)
    {
        var cells = new HashSet<CellPos>();
        foreach (var placement in placements)
            foreach (var f in placement.Tile.Foundation)
                cells.Add(placement.Location + f);
        return cells;
    }

    // ---------------------------------------------------------------- rolling terrain

    /// <summary>
    /// Hills and raised highlands from smooth value noise, sampled at each cell's canonical position
    /// so the terrain is exactly symmetric. Levels are quantised, which leaves flat tops and flat
    /// lowland for bases, ore and buildings; the height field turns the steps into slopes.
    /// Bases and cliff feet stay at ground level.
    /// </summary>
    private void PlaceRollingTerrain(double hills)
    {
        double amount = Math.Clamp(hills, 0, 1);
        // A few broad rises read better than many thin contour steps.
        int maxLevel = 1 + (int)Math.Round(amount * 2);
        double threshold = 0.6 - 0.2 * amount;
        int seed = rng.Next();
        double large = 30, small = 14;

        foreach (var p in map.AllValidCells())
        {
            if (!IsUsable(p) || zone[p.X, p.Y] is not (Zone.Free or Zone.Open or Zone.Base))
                continue;

            // Hills decide where buildings fit and who has the high ground, so they follow the feature
            // symmetry even when the rest of the terrain is free.
            // With asymmetry the noise is sampled off the canonical cell by twice the local drift, so
            // each copy of a rise sits and curves a little differently.
            var c = sym.Canonical(p);
            var (dx, dy) = Drift(p);
            double sx = c.X + 2 * dx, sy = c.Y + 2 * dy;
            double n = 0.75 * ValueNoise(sx / large, sy / large, seed) + 0.25 * ValueNoise(sx / small, sy / small, seed + 1);
            double t = Math.Clamp((n - threshold) / (1 - threshold), 0, 1);
            int level = (int)Math.Round(t * maxLevel);
            if (level == 0)
                continue;

            // Slopes run one level per cell. Hills may run through the base (buildings go up on them),
            // but the core round the start stays flat for the construction yard, and so do cliff feet
            // and the centre for the centre building.
            if (NearZone(p, level + 2, Zone.BaseCore) || NearZone(p, level + 1, Zone.Cliff, Zone.CliffTop, Zone.Water)
                || p.DistanceTo(MapCentre()) < level + 6)
                continue;

            targetLevel[p.X, p.Y] = Math.Max(targetLevel[p.X, p.Y], level);
        }
    }

    /// <summary>Smoothly interpolated lattice noise in [0, 1].</summary>
    private static double ValueNoise(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Lattice(x0, y0, seed), b = Lattice(x0 + 1, y0, seed);
        double c = Lattice(x0, y0 + 1, seed), d = Lattice(x0 + 1, y0 + 1, seed);
        return (a + (b - a) * sx) * (1 - sy) + (c + (d - c) * sx) * sy;
    }

    private static double Lattice(int x, int y, int seed)
    {
        uint h = (uint)seed;
        h ^= (uint)x * 0x27d4eb2d;
        h = (h ^ (h >> 15)) * 0x85ebca6b;
        h ^= (uint)y * 0x165667b1;
        h = (h ^ (h >> 13)) * 0xc2b2ae35;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (double)0xFFFFFF;
    }

    private bool NearZone(CellPos p, double radius, params Zone[] zones)
    {
        foreach (var q in Disk(p, radius))
            if (map.IsValid(q) && Array.IndexOf(zones, zone[q.X, q.Y]) >= 0)
                return true;
        return false;
    }

    // ---------------------------------------------------------------- helpers

    private int CountWrongWayCliffCells(ConnectedTilePlan plan, HashSet<CellPos> top, HashSet<CellPos> cliffCells)
    {
        var height = new Dictionary<CellPos, int>();
        foreach (var placement in plan.Placements)
        {
            var set = theater.TileSet(placement.Tile.TileSetIndex);
            var tile = theater.Tile(set.StartTile + placement.Tile.IndicesInTileSet[0]);
            if (tile == null)
                continue;
            foreach (var cell in tile.Cells)
                height[new CellPos(placement.Location.X + cell.X, placement.Location.Y + cell.Y)] = cell.Height;
        }

        int wrong = 0;
        foreach (var (p, z) in height)
        {
            foreach (var o in CellPos.Neighbours8.Take(4))
            {
                var q = p + o;
                if (cliffCells.Contains(q))
                    continue;
                // High cells may border the outside (back cliffs drop away to the north), but a low cell
                // next to the plateau top means the cliff faces inwards.
                if (z == 0 && top.Contains(q))
                {
                    wrong++;
                    break;
                }
            }
        }
        return wrong;
    }

    private int ClearTileId()
    {
        int id = theater.TileId(Math.Max(0, theater.GeneralInt("ClearTile", 0)), 0);
        return id < 0 ? 0 : id;
    }

    /// <summary>Whether a cell is on a raised terrace (or within a cell of its border), where no other cliff may go.</summary>
    private bool OnTerrace(CellPos p)
    {
        if (raised == null)
            return false;
        foreach (var o in CellPos.Neighbours8)
            if (map.IsValid(p + o) && IsRaisedCell(p + o))
                return true;
        return IsRaisedCell(p);
    }

    private bool NearProtected(CellPos p, double radius)
        => NearZone(p, radius, Zone.Base, Zone.BaseCore, Zone.Resource, Zone.Tech, Zone.Cliff, Zone.CliffTop, Zone.Water);

    /// <summary>Orders a convex outline counter-clockwise on screen, starting with the lowest-left vertex.</summary>
    private static List<CellPos> OrderCounterClockwise(List<CellPos> vertices)
    {
        if (vertices.Count == 0)
            return vertices;
        // Screen coordinates: r = (x - y) / 2 to the right, d = (x + y) / 2 downwards.
        double cr = vertices.Average(v => (v.X - v.Y) / 2.0), cd = vertices.Average(v => (v.X + v.Y) / 2.0);
        double Angle(CellPos v) => Math.Atan2((v.X + v.Y) / 2.0 - cd, (v.X - v.Y) / 2.0 - cr);

        // Decreasing atan2(down, right) runs bottom-left -> bottom-right -> top-right -> top-left.
        var ordered = vertices.Distinct().OrderByDescending(Angle).ToList();
        int start = 0;
        double best = double.MinValue;
        for (int i = 0; i < ordered.Count; i++)
        {
            double score = (ordered[i].X + ordered[i].Y) * 1000.0 - (ordered[i].X - ordered[i].Y);
            if (score > best)
            {
                best = score;
                start = i;
            }
        }

        return ordered.Skip(start).Concat(ordered.Take(start)).ToList();
    }

    /// <summary>Flood fill from <paramref name="from"/> bounded by the cliff cells; null if it leaks.</summary>
    private HashSet<CellPos> FloodInside(CellPos from, HashSet<CellPos> boundary, int limit)
    {
        if (boundary.Contains(from))
            return null;
        var seen = new HashSet<CellPos> { from };
        var queue = new Queue<CellPos>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var o in CellPos.Neighbours8.Take(4))
            {
                var q = p + o;
                if (boundary.Contains(q) || seen.Contains(q))
                    continue;
                if (!map.IsValid(q) || seen.Count > limit)
                    return null;
                seen.Add(q);
                queue.Enqueue(q);
            }
        }
        return seen;
    }
}
