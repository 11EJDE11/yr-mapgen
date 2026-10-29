using MapGen.Analysis;
using MapGen.Model;
using MapGen.Terrain;
using MapGen.Theater;

namespace MapGen.Generation;

/// <summary>What a cell has been reserved for during generation.</summary>
internal enum Zone : byte
{
    Free,
    /// <summary>Construction yard and the space right around it; nothing may be placed here.</summary>
    BaseCore,
    /// <summary>The rest of a player's base; resources and tech may go here, obstacles may not.</summary>
    Base,
    Resource,
    Tech,
    Obstacle,
    /// <summary>Cliff tiles: impassable, never cleared or decorated.</summary>
    Cliff,
    /// <summary>Top of a closed cliff plateau: unreachable scenery, gets trees.</summary>
    CliffTop,
    /// <summary>Sandbags around tech: blocks movement, never cleared.</summary>
    Wall,
    /// <summary>Lake water and its shore tiles.</summary>
    Water,
    /// <summary>A rock overlay: blocks movement, cleared like trees if it blocks a corridor.</summary>
    Rock,
    /// <summary>Kept open for movement (carved corridors, space around features).</summary>
    Open,
}

internal sealed class PlayerSlot
{
    public int Waypoint;
    public CellPos Position;
    public int Team;
    /// <summary>Index into the canonical start list.</summary>
    public int Canonical;
    /// <summary>Symmetry transform mapping the canonical start to this player.</summary>
    public int Transform;
}

/// <summary>One generation attempt with a fixed seed.</summary>
internal sealed partial class GenerationRun
{
    private readonly TheaterData theater;
    private readonly GeneratorSettings settings;
    private readonly Random rng;
    private readonly int seed;

    private MapModel map;
    private Symmetry sym;

    // Symmetry of the terrain: the same as sym, or none at all (features stay symmetric either way).
    private Symmetry terrain;
    private Zone[,] zone;
    private bool[,] usable;

    // Planned overlay per cell (index, data); -1 = none.
    private short[,] overlay;
    private byte[,] overlayData;

    private readonly List<CellPos> canonicalStarts = new();
    private readonly List<PlayerSlot> players = new();
    private readonly List<StructureObject> structures = new();
    private readonly List<TerrainObject> terrainObjects = new();

    private readonly IReadOnlyList<ConnectedTileType> connectedTypes;

    public GenerationRun(TheaterData theater, IReadOnlyList<ConnectedTileType> connectedTypes, GeneratorSettings settings, int seed)
    {
        this.theater = theater;
        this.connectedTypes = connectedTypes;
        this.settings = settings;
        this.seed = seed;
        rng = new Random(seed);
    }

    private bool IsBlitz => settings.Style == MapStyle.Blitz;

    /// <param name="cancel">Checked between steps; cancelled once another attempt has won.</param>
    public GenerationResult Run(CancellationToken cancel = default)
    {
        // MAPGEN_PROFILE=1 prints how long each step takes.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Step(string name, Action a)
        {
            cancel.ThrowIfCancellationRequested();
            var t0 = sw.Elapsed.TotalMilliseconds;
            a();
            if (Environment.GetEnvironmentVariable("MAPGEN_PROFILE") == "1")
                Console.Error.WriteLine($"PROFILE {name,-18} {sw.Elapsed.TotalMilliseconds - t0,8:0} ms");
        }
        Step("CreateMap", CreateMap);
        Step("PlaceStarts", PlaceStarts);
        Step("ReserveBases", ReserveBases);
        Step("PlanTerrain", PlanTerrain);
        Step("SharedTech", () => PlaceTech(shared: true));
        Step("Resources", PlaceResources);
        Step("PlayerTech", () => PlaceTech(shared: false));
        Step("TechWalls", PlaceTechWalls);
        Step("Garrisons", PlaceGarrisons);
        Step("Trails", PlaceTrails);
        Step("Obstacles", PlaceObstacles);
        Step("Scatter", PlaceScatter);
        Step("Connectivity", EnsureConnectivity);
        Step("Trees", MaterializeTrees);
        Step("Realize", Realize);

        var teams = players.GroupBy(p => p.Team).OrderBy(g => g.Key).Select(g => g.Select(p => p.Waypoint).ToArray()).ToList();
        // Starts meant to be equal: images of one canonical start.
        var equivalent = players.GroupBy(p => p.Canonical).Select(g => g.Select(p => p.Waypoint).ToArray()).ToList();
        FairnessReport report = null;
        Step("Analyze", () => report = FairnessAnalyzer.Analyze(map, theater, teams, equivalent, expectGroundRoutes: !IsIslandLayout));
        report.Problems.InsertRange(0, warnings.Distinct().Select(w => "generator: " + w));

        return new GenerationResult
        {
            Map = map,
            Report = report,
            Settings = settings,
            Seed = seed,
            Teams = teams,
            EquivalentStarts = equivalent,
        };
    }

    // ---------------------------------------------------------------- map & symmetry

    /// <summary>Teams (or the two duel players) face each other top vs bottom instead of left vs right.</summary>
    private bool splitTopBottom;

    private void CreateMap()
    {
        // Playable area per format (Size units: W columns of 60 px, H rows of 30 px; W = H is square in cell space).
        double area = settings.Format switch
        {
            MatchFormat.Duel => 76 * 76,
            MatchFormat.TwoVsTwo => 110 * 86,
            MatchFormat.ThreeVsThree => 124 * 100,
            MatchFormat.FourVsFour => 140 * 112,
            MatchFormat.FourTeamsOfTwo => 120 * 116,
            _ => 90 * 90,
        };

        double scale = settings.Size switch { MapSize.Small => 0.85, MapSize.Large => 1.2, _ => 1.0 };
        // Island layouts lose space to the sea.
        if (settings.Layout is MapLayout.TeamIslands or MapLayout.PlayerIslands or MapLayout.Archipelago or MapLayout.Continent)
            scale *= 1.15;
        if (IsBlitz)
            scale *= 0.7;
        area *= scale * scale * (0.92 + rng.NextDouble() * 0.16);

        var shape = settings.Shape == MapShape.Random ? (MapShape)rng.Next(1, 4) : settings.Shape;
        var kind = settings.Symmetry ?? DefaultSymmetry(shape);
        if (!GeneratorSettings.AllowedSymmetries(settings.Format).Contains(kind))
            throw new GenerationFailedException($"{kind} symmetry does not work for {settings.FormatLabel}");
        // Without symmetry only the terrain is free: bases, resources and tech still need one to be fair.
        bool freeTerrain = kind == SymmetryKind.None;
        if (freeTerrain)
            kind = FeatureSymmetry(shape);
        if (kind == SymmetryKind.Diamond)
            shape = MapShape.Square;

        // Wide maps put the sides left and right, tall maps top and bottom; mirrors force their own axis.
        splitTopBottom = kind switch
        {
            SymmetryKind.MirrorLeftRight => false,
            SymmetryKind.MirrorTopBottom => true,
            _ => shape == MapShape.Tall || shape == MapShape.Square && rng.Next(2) == 0,
        };

        // Team formats are elongated along the axis between the sides so teammates are closer to
        // each other than to the enemy.
        bool teams = settings.Format is not (MatchFormat.Duel or MatchFormat.FourTeamsOfTwo);
        double aspect = shape switch
        {
            MapShape.Wide => (teams ? 1.25 : 1.15) + rng.NextDouble() * 0.2,
            MapShape.Tall => 1 / ((teams ? 1.25 : 1.15) + rng.NextDouble() * 0.2),
            _ => 0.95 + rng.NextDouble() * 0.1,
        };
        if (kind == SymmetryKind.Diamond)
            aspect = 1;

        int w = (int)Math.Round(Math.Sqrt(area * aspect));
        int h = (int)Math.Round(Math.Sqrt(area / aspect));

        // Keep W + H even so the 90° rotation centre and the mirror axes fall on cells.
        if ((w + h) % 2 != 0)
            h++;

        map = new MapModel(settings.Theater.ToUpperInvariant(), w, h);
        sym = new Symmetry(kind, map);
        terrain = freeTerrain ? new Symmetry(SymmetryKind.None, map) : sym;

        int n = map.Span;
        zone = new Zone[n, n];
        usable = new bool[n, n];
        overlay = new short[n, n];
        overlayData = new byte[n, n];
        tilePlaced = new bool[n, n];
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                overlay[x, y] = -1;

        foreach (var p in map.AllValidCells())
            usable[p.X, p.Y] = sym.Orbit(p).All(q => map.IsInLocal(q, 1));
    }

    private SymmetryKind DefaultSymmetry(MapShape shape)
    {
        double r = rng.NextDouble();
        // Now and then a map with free (unmirrored) terrain, for variety.
        return r < 0.12 ? SymmetryKind.None : FeatureSymmetry(shape);
    }

    private SymmetryKind FeatureSymmetry(MapShape shape)
    {
        var mirror = shape == MapShape.Tall ? SymmetryKind.MirrorTopBottom : SymmetryKind.MirrorLeftRight;
        double r = rng.NextDouble();
        return settings.Format switch
        {
            MatchFormat.Duel => r < 0.5 ? SymmetryKind.Rotate180 : mirror,
            MatchFormat.FourTeamsOfTwo => r < 0.75 ? SymmetryKind.Quad : SymmetryKind.Diamond,
            _ => r < 0.6 ? SymmetryKind.Quad : r < 0.85 ? SymmetryKind.Rotate180 : mirror,
        };
    }

    private bool IsUsable(CellPos p) => map.IsValid(p) && usable[p.X, p.Y];

    private Zone ZoneAt(CellPos p) => map.IsValid(p) ? zone[p.X, p.Y] : Zone.Obstacle;

    private static IEnumerable<CellPos> Disk(CellPos centre, double radius)
    {
        int r = (int)Math.Ceiling(radius);
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
                if (dx * dx + dy * dy <= radius * radius)
                    yield return new CellPos(centre.X + dx, centre.Y + dy);
    }

    // ---------------------------------------------------------------- stamping

    /// <summary>
    /// Reserves a feature and all of its symmetric images. <paramref name="owner"/> is the canonical
    /// start the feature belongs to (images of one player's feature must not land on another player's),
    /// or -1 for shared features whose images may merge. Terrain (tree clusters) passes its own
    /// symmetry in <paramref name="images"/>.
    /// </summary>
    private bool TryStamp(IReadOnlyCollection<CellPos> cells, int owner, Func<Zone, bool> allowed, Zone newZone,
        out List<CellPos> allCells, Symmetry images = null)
    {
        images ??= sym;
        allCells = null;
        var claimed = new Dictionary<CellPos, CellPos>(); // cell -> start that owns this image

        for (int t = 0; t < images.Order; t++)
        {
            CellPos ownerImage = owner >= 0 ? images.Apply(t, canonicalStarts[owner]) : new CellPos(-1, -1);
            foreach (var c in cells)
            {
                var q = images.Apply(t, c);
                if (!IsUsable(q) || !allowed(ZoneAt(q)))
                    return false;
                if (claimed.TryGetValue(q, out var existingOwner) && existingOwner != ownerImage)
                    return false;
                claimed[q] = ownerImage;
            }
        }

        foreach (var q in claimed.Keys)
            zone[q.X, q.Y] = newZone;
        allCells = claimed.Keys.ToList();
        return true;
    }

    /// <summary>Places a building and its images. Returns false if any image footprint is blocked.</summary>
    private bool TryPlaceStructure(string type, CellPos origin, int owner, Func<Zone, bool> allowed, int margin,
        int maxCopies = int.MaxValue)
    {
        var info = theater.Building(type);
        if (info == null)
            throw new GenerationFailedException($"Unknown building {type}");
        var (w, h) = info.FoundationSize;

        var footprint = new List<CellPos>();
        for (int dx = 0; dx < w; dx++)
            for (int dy = 0; dy < h; dy++)
                footprint.Add(new CellPos(origin.X + dx, origin.Y + dy));

        var withMargin = footprint.SelectMany(c => Disk(c, margin)).Distinct().ToList();
        if (withMargin.Any(c => !IsUsable(c)))
            return false;

        // The footprint itself must be free; the margin only needs to avoid hard features.
        var images = new List<CellPos>();
        var newStructures = new List<StructureObject>();
        for (int t = 0; t < sym.Order; t++)
        {
            var imageCells = footprint.Select(c => sym.Apply(t, c)).ToList();
            int minX = imageCells.Min(c => c.X), minY = imageCells.Min(c => c.Y);
            int maxX = imageCells.Max(c => c.X), maxY = imageCells.Max(c => c.Y);
            // Buildings cannot rotate; centre the real foundation on the image's bounding box.
            int ox = (minX + maxX + 1 - w) / 2;
            int oy = (minY + maxY + 1 - h) / 2;
            var imageOrigin = new CellPos(ox, oy);

            // Shared buildings (centre, on an axis) map onto themselves: keep one.
            if (newStructures.Any(s => Math.Abs(s.Position.X - ox) < w && Math.Abs(s.Position.Y - oy) < h))
                continue;

            newStructures.Add(new StructureObject { Type = type, Position = imageOrigin });
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    images.Add(new CellPos(ox + dx, oy + dy));
        }

        if (newStructures.Count > maxCopies)
            return false;

        foreach (var c in images)
            if (!IsUsable(c) || !allowed(ZoneAt(c)) || !IsFlatGround(c))
                return false;
        foreach (var s in newStructures)
        {
            int level = GroundLevel(s.Position);
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    if (GroundLevel(new CellPos(s.Position.X + dx, s.Position.Y + dy)) != level)
                        return false;
        }
        // Every copy on the same ground: with free terrain one could otherwise sit on a plateau or hill
        // and its image at the foot.
        if (newStructures.Select(s => GroundLevel(s.Position)).Distinct().Count() > 1)
            return false;

        var marginCells = images.SelectMany(c => Disk(c, margin)).Distinct().ToList();
        foreach (var c in marginCells)
            if (map.IsValid(c) && zone[c.X, c.Y] is Zone.Resource or Zone.Tech or Zone.BaseCore)
                return false;

        foreach (var c in marginCells)
            if (map.IsValid(c) && zone[c.X, c.Y] is Zone.Free or Zone.Base)
                zone[c.X, c.Y] = Zone.Open;
        foreach (var c in images)
            zone[c.X, c.Y] = Zone.Tech;

        structures.AddRange(newStructures);
        return true;
    }

    // ---------------------------------------------------------------- realisation

    private void Realize()
    {
        int clearTile = Math.Max(0, theater.GeneralInt("ClearTile", 0));
        int clearId = theater.TileId(clearTile, 0);
        if (clearId < 0)
            clearId = 0;

        foreach (var p in map.AllValidCells())
        {
            if (!tilePlaced[p.X, p.Y])
                map[p].TileIndex = clearId;
        }

        PaintGround();

        // Levels and slope tiles for all ground that is not a cliff or trail.
        heights.WriteBack(tilePlaced, clearId);

        WriteWalls();
        foreach (var p in map.AllValidCells())
        {
            ref var c = ref map[p];
            c.Overlay = overlay[p.X, p.Y];
            c.OverlayData = overlayData[p.X, p.Y];
        }

        PlaceSmudges();

        foreach (var pl in players)
            map.Waypoints[pl.Waypoint] = pl.Position;

        map.Structures.AddRange(structures);
        map.TerrainObjects.AddRange(terrainObjects);

        map.MinPlayers = 2;
        map.MaxPlayers = players.Count;
        map.GameMode = IsBlitz ? "Blitz" : "Standard";
        map.Name = settings.Name ?? $"[{players.Count}] Generated {settings.FormatLabel}{(IsBlitz ? " Blitz" : "")} #{seed}";
        map.ExtraBasic["GeneratorSeed"] = seed.ToString();
        // Invariant so the same seed writes the same file on every machine (no "0,5" on German Windows).
        map.ExtraBasic["GeneratorSettings"] = FormattableString.Invariant($"{settings.Format};{settings.Style};{settings.Size};{settings.Resources};{settings.Gems};{settings.Economy};hills {settings.Hills:0.##};cliffs {settings.Cliffs:0.##};water {settings.Water:0.##};{sym.Kind}{(terrain.Kind == SymmetryKind.None ? " (terrain free)" : "")}");

        AddTeamMappings();
        ApplyLighting();
    }

    private void AddTeamMappings()
    {
        var byWaypoint = players.OrderBy(p => p.Waypoint).ToList();
        string Letters(Func<PlayerSlot, int> team) => string.Join(",", byWaypoint.Select(p => ((char)('A' + team(p))).ToString()));

        switch (settings.Format)
        {
            case MatchFormat.Duel:
                break;
            case MatchFormat.FourTeamsOfTwo:
                map.TeamStartMappings.Add(("2v2v2v2", Letters(p => p.Team)));
                break;
            default:
                map.TeamStartMappings.Add(($"{settings.FormatLabel} (intended)", Letters(p => p.Team)));
                break;
        }
    }

    private void ApplyLighting()
    {
        var (ambient, red, green, blue, level) = settings.Time switch
        {
            TimeOfDay.Dusk => (0.80, 1.10, 0.92, 0.80, 0.045),
            TimeOfDay.Night => (0.55, 0.75, 0.80, 1.05, 0.065),
            _ => (1.00, 1.00, 1.00, 1.00, 0.032),
        };

        string F(double d) => d.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        var l = map.Lighting;
        l["Ambient"] = F(ambient);
        l["Red"] = F(red);
        l["Green"] = F(green);
        l["Blue"] = F(blue);
        l["Ground"] = F(0);
        l["Level"] = F(level);
        l["IonAmbient"] = F(0.75);
        l["IonRed"] = F(0.85);
        l["IonGreen"] = F(0.75);
        l["IonBlue"] = F(1.25);
        l["IonGround"] = F(0);
        l["IonLevel"] = F(level);
        l["DominatorAmbient"] = F(1.5);
        l["DominatorRed"] = F(0.85);
        l["DominatorGreen"] = F(0.2);
        l["DominatorBlue"] = F(0.3);
        l["DominatorGround"] = F(0);
        l["DominatorLevel"] = F(0);
        l["DominatorAmbientChangeRate"] = F(0.001);
    }
}
