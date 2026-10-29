using MapGen.Model;

namespace MapGen.Generation;

internal sealed partial class GenerationRun
{
    private readonly List<string> warnings = new();

    private List<int> oreOverlays;
    private List<int> gemOverlays;

    // ---------------------------------------------------------------- resources

    private (int Main, int Second, int Gem, int Contested, int ContestedGem) FieldSizes()
    {
        // Cell counts per field. Medium is calibrated against popular maps (Heck Freezes Over, Hex Bay):
        // roughly 20k credits within 25 cells of travel and 40-50k within 40.
        var sizes = settings.Resources switch
        {
            ResourceLevel.Low => (36, 28, 16, 32, 18),
            ResourceLevel.High => (64, 52, 28, 58, 32),
            ResourceLevel.VeryHigh => (80, 66, 36, 72, 40),
            _ => (50, 40, 22, 46, 26),
        };

        double scale = IsBlitz ? 0.8 : 1.0;
        if (settings.Economy == EconomyMode.Mixed)
            scale *= 0.7;
        double ore = scale * Math.Clamp(settings.OreFieldSize, 0.25, 4);
        double gem = scale * Math.Clamp(settings.GemFieldSize, 0.25, 4);

        return ((int)(sizes.Item1 * ore), (int)(sizes.Item2 * ore), (int)(sizes.Item3 * gem),
            (int)(sizes.Item4 * ore), (int)(sizes.Item5 * gem));
    }

    private void PlaceResources()
    {
        // Same overlay sets as WAE's YR "Ore" and "Gems" collections.
        oreOverlays = Enumerable.Range(1, 12).Select(i => theater.OverlayIndex($"TIB{i:00}")).Where(i => i >= 0).ToList();
        gemOverlays = Enumerable.Range(1, 12).Select(i => theater.OverlayIndex($"GEM{i:00}")).Where(i => i >= 0).ToList();

        if (settings.Economy == EconomyMode.OilOnly)
            return;

        var sizes = FieldSizes();
        bool gemsPerPlayer = settings.Gems is GemPlacement.PerPlayer or GemPlacement.Both;
        bool gemsContested = settings.Gems is GemPlacement.Contested or GemPlacement.Both;

        for (int k = 0; k < canonicalStarts.Count; k++)
        {
            double mainMin = IsBlitz ? 8 : 10, mainMax = IsBlitz ? 10 : 13;
            if (!PlacePlayerField(k, gems: false, sizes.Main, mainMin, mainMax, awayBias: 0.6))
                throw new GenerationFailedException("Could not place a main ore field");

            if (!IsBlitz && settings.Economy == EconomyMode.Ore)
                if (!PlacePlayerField(k, gems: false, sizes.Second, 19, 26, awayBias: 0.0) && !IsIslandLayout)
                    warnings.Add("Could not place a secondary ore field");

            if (gemsPerPlayer)
                if (!PlacePlayerField(k, gems: true, sizes.Gem, IsBlitz ? 14 : 20, IsBlitz ? 19 : 28, awayBias: -0.2)
                    && !PlacePlayerField(k, gems: true, sizes.Gem, 12, 18, awayBias: 0.3) && !IsIslandLayout)
                    warnings.Add("Could not place a player gem field");
        }

        if (!IsBlitz || settings.Format != MatchFormat.Duel)
        {
            if (!PlaceSharedField(gems: false, sizes.Contested, centre: false) && !IsIslandLayout)
                warnings.Add("Could not place a contested ore field");
        }

        if (gemsContested)
        {
            if (!PlaceSharedField(gems: true, sizes.ContestedGem, centre: true) && !IsIslandLayout)
                warnings.Add("Could not place a centre gem field");
        }
        else if (!IsBlitz && ZoneAt(MapCentre()) == Zone.Free)
        {
            if (!PlaceSharedField(gems: false, sizes.Contested, centre: true) && !IsIslandLayout)
                warnings.Add("Could not place a centre ore field");
        }
    }

    /// <summary>
    /// How many symmetry transforms leave a canonical start in place. A start on a mirror axis
    /// (the middle player of a 3v3) receives a feature and its mirror image, so it gets half-size features.
    /// </summary>
    private int StabilizerSize(int k) => Enumerable.Range(0, sym.Order).Count(t => sym.Apply(t, canonicalStarts[k]) == canonicalStarts[k]);

    private bool PlacePlayerField(int k, bool gems, int size, double minDist, double maxDist, double awayBias)
    {
        var start = canonicalStarts[k];
        size = Math.Max(6, size / StabilizerSize(k));
        var away = AwayFromEnemies(k);
        double awayAngle = Math.Atan2(away.Y, away.X);

        for (int attempt = 0; attempt < 80; attempt++)
        {
            // awayBias > 0 prefers the back of the base, < 0 the front, 0 anywhere.
            double spread = Math.PI * (1.0 - Math.Abs(awayBias) * 0.6);
            double angle = (awayBias >= 0 ? awayAngle : awayAngle + Math.PI) + (rng.NextDouble() * 2 - 1) * spread;
            double d = minDist + rng.NextDouble() * (maxDist - minDist);
            var centre = new CellPos(start.X + (int)Math.Round(Math.Cos(angle) * d), start.Y + (int)Math.Round(Math.Sin(angle) * d));

            if (!IsUsable(centre) || !IsFlatGround(centre))
                continue;
            int level = GroundLevel(centre);
            var blob = GrowBlob(centre, size, p => ZoneAt(p) is Zone.Free or Zone.Base && start.DistanceTo(p) >= BaseCoreRadius + 1
                                                   && IsFlatGround(p) && GroundLevel(p) == level);
            if (blob.Count < size * 0.85)
                continue;
            if (!TryStamp(blob, k, z => z is Zone.Free or Zone.Base, Zone.Resource, out _))
                continue;

            FillField(blob, gems);
            return true;
        }
        return false;
    }

    private bool PlaceSharedField(bool gems, int size, bool centre)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            CellPos at = centre ? MapCentre() : PickContestedPoint(minFromStarts: BaseRadius + 7);
            if (centre && attempt > 0)
                at = new CellPos(at.X + rng.Next(-attempt / 6, attempt / 6 + 1), at.Y + rng.Next(-attempt / 6, attempt / 6 + 1));

            // Shared fields merge with their own images, so grow a smaller blob.
            int target = centre ? Math.Max(8, size / Math.Max(1, sym.Order / 2)) : size;
            if (!IsUsable(at) || !IsFlatGround(at))
                continue;
            int level = GroundLevel(at);
            var blob = GrowBlob(at, target, p => ZoneAt(p) == Zone.Free && players.All(pl => pl.Position.DistanceTo(p) > BaseRadius + 3)
                                                 && IsFlatGround(p) && GroundLevel(p) == level);
            if (blob.Count < target * 0.8)
                continue;
            if (!TryStamp(blob, -1, z => z == Zone.Free, Zone.Resource, out _))
                continue;

            FillField(blob, gems);
            return true;
        }
        return false;
    }

    private CellPos MapCentre()
    {
        int c = map.Width + map.Height;
        return new CellPos(c / 2, c / 2);
    }

    /// <summary>
    /// A point roughly equidistant from the nearest player of team 0 and the nearest opposing player,
    /// i.e. ground both sides are expected to fight over.
    /// </summary>
    private CellPos PickContestedPoint(double minFromStarts)
    {
        int teamA = players[0].Team;
        var team0 = players.Where(p => p.Team == teamA).Select(p => p.Position).ToList();
        var others = players.Where(p => p.Team != teamA).Select(p => p.Position).ToList();

        var candidates = new List<(CellPos p, double score)>();
        for (int i = 0; i < 600; i++)
        {
            var p = new CellPos(rng.Next(1, map.Span), rng.Next(1, map.Span));
            if (!IsUsable(p) || zone[p.X, p.Y] != Zone.Free)
                continue;
            double da = team0.Min(s => s.DistanceTo(p));
            double db = others.Min(s => s.DistanceTo(p));
            if (Math.Min(da, db) < minFromStarts)
                continue;
            // Equidistant, and not absurdly far from both.
            candidates.Add((p, Math.Abs(da - db) + 0.15 * Math.Min(da, db)));
        }

        if (candidates.Count == 0)
            return MapCentre();

        var top = candidates.OrderBy(c => c.score).Take(8).ToList();
        return top[rng.Next(top.Count)].p;
    }

    /// <summary>Grows a compact random blob of up to <paramref name="size"/> cells from <paramref name="centre"/>.</summary>
    private List<CellPos> GrowBlob(CellPos centre, int size, Func<CellPos, bool> allowed)
    {
        var result = new List<CellPos>();
        if (!IsUsable(centre) || !allowed(centre))
            return result;

        var inBlob = new HashSet<CellPos> { centre };
        var frontier = new PriorityQueue<CellPos, double>();
        result.Add(centre);
        void Push(CellPos from)
        {
            foreach (var o in CellPos.Neighbours8.Take(4))
            {
                var q = from + o;
                if (inBlob.Contains(q) || !IsUsable(q) || !allowed(q))
                    continue;
                inBlob.Add(q);
                // Prefer cells close to the centre, with noise for an organic outline.
                frontier.Enqueue(q, q.DistanceTo(centre) + rng.NextDouble() * 2.2);
            }
        }

        Push(centre);
        while (result.Count < size && frontier.TryDequeue(out var next, out _))
        {
            result.Add(next);
            Push(next);
        }
        return result;
    }

    /// <summary>Assigns ore/gem overlays to a canonical blob and copies them to all images.</summary>
    private void FillField(List<CellPos> blob, bool gems)
    {
        double cx = blob.Average(p => p.X), cy = blob.Average(p => p.Y);
        double r = blob.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))) + 0.75;
        var variants = gems ? gemOverlays : oreOverlays;

        foreach (var p in blob)
        {
            double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) / r;
            // Official maps keep most ore at stages 7-11 with a thinner rim.
            double stage = 11.5 - 7 * d * d + (rng.NextDouble() * 2 - 1);
            byte data = (byte)Math.Clamp((int)Math.Round(stage), 3, 11);
            short ovl = (short)variants[rng.Next(variants.Count)];

            for (int t = 0; t < sym.Order; t++)
            {
                var q = sym.Apply(t, p);
                overlay[q.X, q.Y] = ovl;
                overlayData[q.X, q.Y] = data;
            }
        }

        if (!gems)
            PlaceDrills(blob, cx, cy);

        // Keep the edges of fields free of obstacles so harvesters can reach every cell.
        foreach (var p in blob)
            foreach (var q in Disk(p, 1.5))
                for (int t = 0; t < sym.Order; t++)
                {
                    var img = sym.Apply(t, q);
                    if (map.IsValid(img) && zone[img.X, img.Y] == Zone.Free)
                        zone[img.X, img.Y] = Zone.Open;
                }
    }

    /// <summary>
    /// Ore drills (TIBTRE) make a field regrow. Most popular maps give 80-90% of ore fields one; the
    /// choice is made per canonical field, so every player's matching field gets the same.
    /// </summary>
    private void PlaceDrills(List<CellPos> blob, double cx, double cy)
    {
        var drillTypes = theater.TerrainTypes.Where(t => t.Available && t.SpawnsTiberium).Select(t => t.Name).ToList();
        if (drillTypes.Count == 0 || rng.NextDouble() >= settings.Drills)
            return;

        int count = blob.Count >= 70 ? 2 : 1;
        var byCentre = blob.OrderBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)).ToList();
        var chosen = new List<CellPos> { byCentre[0] };
        if (count == 2)
        {
            // Second drill roughly halfway between the centre and the far edge.
            var far = byCentre[^1];
            var mid = new CellPos((byCentre[0].X + far.X) / 2, (byCentre[0].Y + far.Y) / 2);
            if (blob.Contains(mid) && mid.DistanceTo(byCentre[0]) >= 3)
                chosen.Add(mid);
        }

        foreach (var p in chosen)
        {
            string type = drillTypes[rng.Next(drillTypes.Count)];
            var placed = new HashSet<CellPos>();
            for (int t = 0; t < sym.Order; t++)
            {
                var q = sym.Apply(t, p);
                if (!placed.Add(q))
                    continue;
                overlay[q.X, q.Y] = -1;
                overlayData[q.X, q.Y] = 0;
                terrainObjects.Add(new TerrainObject { Type = type, Position = q });
            }
        }
    }

    // ---------------------------------------------------------------- tech

    /// <summary>
    /// Shared tech (centre, contested) is placed before resources so it gets the spots it needs;
    /// player and team tech afterwards, fitted around the player's fields.
    /// </summary>
    private void PlaceTech(bool shared)
    {
        // Centre buildings first: the centre is also the most even contested spot, so a contested
        // building placed earlier would take it.
        foreach (var rule in settings.EffectiveTech().OrderBy(r => r.Placement == TechPlacement.Centre ? 0 : 1))
        {
            bool isShared = rule.Placement is TechPlacement.Centre or TechPlacement.Contested;
            if (isShared != shared)
                continue;

            string type = rule.BuildingId;
            var info = theater.Building(type);
            if (info == null)
            {
                warnings.Add($"Unknown tech building '{rule.Type}'");
                continue;
            }

            for (int n = 0; n < Math.Max(1, rule.Count); n++)
            {
                bool placed = rule.Placement switch
                {
                    TechPlacement.PerPlayer => Enumerable.Range(0, canonicalStarts.Count)
                        .Select(k => PlacePlayerTech(type, k, rule.MinDistance, rule.MaxDistance)).All(ok => ok),
                    TechPlacement.PerTeam => PlaceTeamTech(type),
                    TechPlacement.Contested => PlaceSharedTech(type, centre: false),
                    TechPlacement.Centre => PlaceSharedTech(type, centre: true),
                    _ => false,
                };
                // On island maps the centre or the contested ground may well be open water.
                bool optional = IsIslandLayout && rule.Placement is TechPlacement.Centre or TechPlacement.Contested or TechPlacement.PerTeam;
                if (!placed && !optional)
                    warnings.Add($"Could not place {rule.Placement} {info.UIName}");
            }
        }
    }

    private bool PlacePlayerTech(string type, int k, int minDist, int maxDist)
    {
        var (w, h) = theater.Building(type).FoundationSize;
        var start = canonicalStarts[k];
        var away = AwayFromEnemies(k);

        bool onAxis = StabilizerSize(k) > 1;
        double awayAngle = Math.Atan2(away.Y, away.X);

        for (int attempt = 0; attempt < 120; attempt++)
        {
            // A start on a mirror axis gets its building on the axis too (straight behind it), so the
            // building's mirror image coincides with it instead of giving that player two.
            double angle = onAxis ? awayAngle : rng.NextDouble() * Math.PI * 2;
            // Avoid putting a player's own tech straight towards the enemy.
            if (Math.Cos(angle) * away.X + Math.Sin(angle) * away.Y < -0.5)
                continue;
            double d = minDist + rng.NextDouble() * Math.Max(0, maxDist - minDist);
            var origin = new CellPos(start.X + (int)Math.Round(Math.Cos(angle) * d - w / 2.0),
                start.Y + (int)Math.Round(Math.Sin(angle) * d - h / 2.0));

            if (TryPlaceStructure(type, origin, k, z => z is Zone.Free or Zone.Base or Zone.Open, margin: 1))
                return true;
        }
        return false;
    }

    private bool PlaceTeamTech(string type)
    {
        if (settings.Format == MatchFormat.Duel)
            return Enumerable.Range(0, canonicalStarts.Count).Select(k => PlacePlayerTech(type, k, 12, 20)).All(ok => ok);

        var (w, h) = theater.Building(type).FoundationSize;
        int team = players.First(p => p.Canonical == 0 && p.Transform == 0).Team;
        var members = players.Where(p => p.Team == team).Select(p => p.Position).ToList();
        var enemies = players.Where(p => p.Team != team).Select(p => p.Position).ToList();
        double mx = members.Average(p => p.X), my = members.Average(p => p.Y);

        // Somewhere between the teammates, outside every base, as evenly reachable by all of them as
        // possible, and not closer to the enemy than to the team.
        var candidates = new List<(CellPos origin, double score)>();
        for (int i = 0; i < 1500; i++)
        {
            var centre = new CellPos((int)Math.Round(mx) + rng.Next(-30, 31), (int)Math.Round(my) + rng.Next(-30, 31));
            if (!IsUsable(centre) || players.Any(p => p.Position.DistanceTo(centre) < BaseRadius + 1))
                continue;
            double worstAlly = members.Max(m => m.DistanceTo(centre));
            double nearestEnemy = enemies.Min(e => e.DistanceTo(centre));
            if (nearestEnemy < worstAlly + 8)
                continue;
            double spread = members.Max(m => m.DistanceTo(centre)) - members.Min(m => m.DistanceTo(centre));
            candidates.Add((new CellPos(centre.X - w / 2, centre.Y - h / 2), worstAlly + spread * 0.5 + rng.NextDouble() * 3));
        }

        foreach (var (origin, _) in candidates.OrderBy(c => c.score).Take(250))
            if (TryPlaceStructure(type, origin, -1, z => z is Zone.Free or Zone.Base or Zone.Open, margin: 1))
                return true;
        return false;
    }

    private bool PlaceSharedTech(string type, bool centre)
    {
        var (w, h) = theater.Building(type).FoundationSize;
        for (int attempt = 0; attempt < 80; attempt++)
        {
            var at = centre ? MapCentre() : PickContestedPoint(BaseRadius + 6);
            int jitter = centre ? attempt / 8 : 0;
            var origin = new CellPos(at.X - w / 2 + rng.Next(-jitter, jitter + 1), at.Y - h / 2 + rng.Next(-jitter, jitter + 1));
            // A centre building must map onto itself under every symmetry: exactly one copy.
            int before = structures.Count;
            if (TryPlaceStructure(type, origin, -1, z => z is Zone.Free or Zone.Open, margin: 2, maxCopies: centre ? 1 : int.MaxValue))
            {
                sharedTech.AddRange(structures.Skip(before));
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- obstacles

    private void PlaceObstacles()
    {
        if (settings.Trees <= 0)
            return;

        int usableCount = map.AllValidCells().Count(IsUsable);
        // Clusters per canonical area: each is copied to every image, so density is the same for any symmetry.
        int clusters = (int)Math.Round(settings.Trees * usableCount / terrain.Order / 65.0);

        for (int i = 0; i < clusters; i++)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var p = new CellPos(rng.Next(1, map.Span), rng.Next(1, map.Span));
                if (!IsUsable(p) || !terrain.IsCanonical(p) || zone[p.X, p.Y] != Zone.Free || OnLane(p))
                    continue;
                if (players.Any(pl => pl.Position.DistanceTo(p) < BaseRadius + 3))
                    continue;

                double r = 1.5 + rng.NextDouble() * 3.0;
                int size = (int)(Math.PI * r * r * (0.6 + rng.NextDouble() * 0.4));
                var blob = GrowBlob(p, Math.Max(2, size), q => ZoneAt(q) == Zone.Free && !OnLane(q)
                    && players.All(pl => pl.Position.DistanceTo(q) >= BaseRadius + 2));
                if (blob.Count < 2)
                    continue;
                if (TryStamp(blob, -1, z => z == Zone.Free, Zone.Obstacle, out _, terrain))
                    break;
            }
        }
    }

    /// <summary>
    /// Every start must reach every other start through corridors at least three cells wide, and
    /// every resource field and tech building must be reachable. Obstacles in the way are cleared
    /// (symmetrically).
    /// </summary>
    private void EnsureConnectivity()
    {
        if (IsIslandLayout)
        {
            EnsureIslandConnectivity();
            return;
        }

        for (int iteration = 0; iteration < 40; iteration++)
        {
            var reached = WideReach(players[0].Position);
            var target = players.Select(p => p.Position).FirstOrDefault(p => !reached[p.X, p.Y]);

            if (target == default)
                target = FindUnreachableFeature(Reach(players[0].Position));
            if (target == default)
                return;

            CarvePath(players[0].Position, target);
        }

        var finalReach = WideReach(players[0].Position);
        var stuck = players.Where(p => !finalReach[p.Position.X, p.Position.Y]).Select(p => p.Position.ToString());
        var stuckFeature = FindUnreachableFeature(Reach(players[0].Position));
        warnings.Add($"Connectivity could not be fully ensured (starts: {string.Join(" ", stuck)}; feature: {stuckFeature})");
    }

    /// <summary>
    /// A resource cell with no reachable neighbour, or a cell next to a building none of whose
    /// surrounding cells can be reached. Returns default when everything is reachable.
    /// </summary>
    private CellPos FindUnreachableFeature(bool[,] reach, Func<CellPos, bool> within = null)
    {
        bool Reached(CellPos p) => map.IsValid(p) && reach[p.X, p.Y];
        within ??= _ => true;

        foreach (var p in map.AllValidCells())
            if (zone[p.X, p.Y] == Zone.Resource && within(p) && !Reached(p) && !CellPos.Neighbours8.Any(o => Reached(p + o)))
                return p;

        foreach (var s in structures)
        {
            if (!within(s.Position))
                continue;
            var (w, h) = theater.Building(s.Type).FoundationSize;
            var ring = new List<CellPos>();
            for (int dx = -1; dx <= w; dx++)
                for (int dy = -1; dy <= h; dy++)
                    if (dx < 0 || dy < 0 || dx == w || dy == h)
                        ring.Add(new CellPos(s.Position.X + dx, s.Position.Y + dy));
            if (!ring.Any(Reached))
                return ring.FirstOrDefault(IsUsable);
        }

        return default;
    }

    /// <summary>
    /// Islands: players on the same landmass must reach each other, and everything on a player's
    /// landmass must be reachable from it. Islets nobody starts on are for transports and air.
    /// </summary>
    private void EnsureIslandConnectivity()
    {
        bool Land(CellPos p) => IsUsable(p) && zone[p.X, p.Y] is not (Zone.Water or Zone.Cliff or Zone.CliffTop or Zone.Wall);
        for (int iteration = 0; iteration < 40; iteration++)
        {
            CellPos from = default, target = default;
            foreach (var pl in players)
            {
                var landmass = Flood(pl.Position, Land);
                var wide = WideReach(pl.Position);
                var mate = players.Select(o => o.Position).FirstOrDefault(o => landmass[o.X, o.Y] && !wide[o.X, o.Y]);
                if (mate != default)
                {
                    (from, target) = (pl.Position, mate);
                    break;
                }

                var narrow = Reach(pl.Position);
                var lost = FindUnreachableFeature(narrow, p => landmass[p.X, p.Y]);
                if (lost != default)
                {
                    (from, target) = (pl.Position, lost);
                    break;
                }
            }

            if (target == default)
                return;
            CarvePath(from, target);
        }
        warnings.Add("Connectivity could not be fully ensured on the islands");
    }

    private bool IsWalkable(CellPos p) => IsUsable(p) && zone[p.X, p.Y] is not (Zone.Obstacle or Zone.Tech or Zone.Cliff or Zone.CliffTop or Zone.Wall or Zone.Rock or Zone.Water);

    private bool[,] Reach(CellPos from) => Flood(from, IsWalkable);

    private bool[,] WideReach(CellPos from)
        => Flood(from, p => IsWalkable(p) && CellPos.Neighbours8.Take(4).All(o => IsWalkable(p + o)));

    private bool[,] Flood(CellPos from, Func<CellPos, bool> ok)
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
                if (!map.IsValid(q) || seen[q.X, q.Y] || !ok(q))
                    continue;
                seen[q.X, q.Y] = true;
                queue.Enqueue(q);
            }
        }
        return seen;
    }

    private void CarvePath(CellPos from, CellPos to)
    {
        var dist = new Dictionary<CellPos, double> { [from] = 0 };
        var prev = new Dictionary<CellPos, CellPos>();
        var queue = new PriorityQueue<CellPos, double>();
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out var p, out double d))
        {
            if (p == to)
                break;
            if (d > dist[p])
                continue;
            foreach (var o in CellPos.Neighbours8.Take(4))
            {
                var q = p + o;
                if (!IsUsable(q) || zone[q.X, q.Y] is Zone.Cliff or Zone.CliffTop or Zone.Wall or Zone.Water || zone[q.X, q.Y] == Zone.Tech && q != to)
                    continue;
                double cost = zone[q.X, q.Y] is Zone.Obstacle or Zone.Rock ? 12 : 1;
                double nd = d + cost;
                if (!dist.TryGetValue(q, out double old) || nd < old)
                {
                    dist[q] = nd;
                    prev[q] = p;
                    queue.Enqueue(q, nd);
                }
            }
        }

        if (!prev.ContainsKey(to))
            return;

        for (var p = to; p != from; p = prev[p])
            foreach (var q in Disk(p, 2.2))
                for (int t = 0; t < terrain.Order; t++)
                {
                    var img = terrain.Apply(t, q);
                    if (map.IsValid(img) && zone[img.X, img.Y] is Zone.Obstacle or Zone.Rock)
                    {
                        if (zone[img.X, img.Y] == Zone.Rock)
                            overlay[img.X, img.Y] = -1;
                        zone[img.X, img.Y] = Zone.Open;
                    }
                }
    }

    private void MaterializeTrees()
    {
        var trees = Trees.Where(n => (theater.TerrainType(n)?.Foundation ?? "1x1").Equals("1x1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (trees.Count == 0)
            return;

        foreach (var p in map.AllValidCells())
        {
            // Closed plateau tops are unreachable, so their trees are pure scenery (like Hidden Valley's ridges).
            bool tree = zone[p.X, p.Y] == Zone.Obstacle
                        || zone[p.X, p.Y] == Zone.CliffTop && heights.IsFlat(p) && rng.NextDouble() < 0.45;
            if (tree)
                terrainObjects.Add(new TerrainObject { Type = trees[rng.Next(trees.Count)], Position = p });
        }
    }
}
