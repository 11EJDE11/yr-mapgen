using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Terrain;

/// <summary>Which edge of a cell-aligned plateau box a ramp is cut into (named by the direction it faces).</summary>
public enum RampEdge
{
    /// <summary>Bottom-left edge (y = max), front cliff, descends towards +y.</summary>
    SouthWest,
    /// <summary>Bottom-right edge (x = max), front cliff, descends towards +x.</summary>
    SouthEast,
    /// <summary>Top-right edge (y = min), back cliff, descends towards -y.</summary>
    NorthEast,
    /// <summary>Top-left edge (x = min), back cliff, descends towards -x.</summary>
    NorthWest,
}

/// <summary>
/// The "Slope Set Pieces" tiles (theater [General] CliffRamps) as connected-tile end pieces, so the
/// cliff planner can run an open cliff path that begins and ends in a ramp transition.
///
/// The connection points were derived from how the shipped maps join cliff tiles to these pieces
/// (hundreds of placements each). Travelling counter-clockwise on screen around a plateau, each edge
/// has a piece that starts the cliff after the ramp gap and one that ends it before the gap:
///   SW edge (travel SE): end = piece 0, CP (0,1) facing NW; start = piece 1, CP (2,1) facing SE
///   SE edge (travel NE): end = piece 2, CP (1,2) facing SW; start = piece 3, CP (1,0) facing NE
///   NE edge (travel NW): end = piece 5, CP (2,2) facing SE; start = piece 4, CP (0,2) facing NW
///   NW edge (travel SW): end = piece 7, CP (2,0) facing NE; start = piece 8, CP (2,2) facing SW
/// </summary>
public static class CliffRamps
{
    private const byte NE = 0b10000000, SE = 0b00100000, SW = 0b00001000, NW = 0b00000010;

    private static readonly Dictionary<RampEdge, (int End, CellPos EndCp, byte EndDir, int Start, CellPos StartCp, byte StartDir, ConnectedTileSide Side)> Pieces = new()
    {
        [RampEdge.SouthWest] = (0, new(0, 1), NW, 1, new(2, 1), SE, ConnectedTileSide.Front),
        [RampEdge.SouthEast] = (2, new(1, 2), SW, 3, new(1, 0), NE, ConnectedTileSide.Front),
        [RampEdge.NorthEast] = (5, new(2, 2), SE, 4, new(0, 2), NW, ConnectedTileSide.Back),
        [RampEdge.NorthWest] = (7, new(2, 0), NE, 8, new(2, 2), SW, ConnectedTileSide.Back),
    };

    public static ConnectedTileSide SideOf(RampEdge edge) => Pieces[edge].Side;

    /// <summary>The cliff type plus this edge's two slope pieces as ending pieces; null if the theater lacks them.</summary>
    public static ConnectedTileType WithRampEnds(ConnectedTileType cliffType, TheaterData theater, RampEdge edge)
    {
        int setIndex = theater.GeneralInt("CliffRamps", -1);
        var set = theater.TileSet(setIndex);
        if (set == null || set.TilesInSet < 9)
            return null;

        var p = Pieces[edge];
        var end = MakePiece(theater, set, p.End, p.EndCp, p.EndDir, p.Side, 1000);
        var start = MakePiece(theater, set, p.Start, p.StartCp, p.StartDir, p.Side, 1001);
        if (end == null || start == null)
            return null;

        var type = new ConnectedTileType
        {
            IniName = cliffType.IniName + "+Ramps",
            Name = cliffType.Name,
            FrontOnly = cliffType.FrontOnly,
            AllowedTheaters = cliffType.AllowedTheaters,
        };
        type.Tiles.AddRange(cliffType.Tiles);
        type.Tiles.Add(end);
        type.Tiles.Add(start);
        return type;
    }

    public static bool IsRampPiece(ConnectedTile tile) => tile.Index >= 1000;

    private static ConnectedTile MakePiece(TheaterData theater, TileSetInfo set, int indexInSet, CellPos cp, byte direction, ConnectedTileSide side, int id)
    {
        var tile = theater.Tile(set.StartTile + indexInSet);
        if (tile == null || tile.Missing)
            return null;

        return new ConnectedTile
        {
            Index = id,
            TileSetName = set.Name,
            TileSetIndex = set.Index,
            IndicesInTileSet = new List<int> { indexInSet },
            ConnectionPoints = new[]
            {
                new TileConnectionPoint
                {
                    Index = 0,
                    CoordinateOffset = cp,
                    ConnectionMask = direction,
                    Side = side,
                    RequiredTiles = Array.Empty<int>(),
                    ForbiddenTiles = Array.Empty<int>(),
                },
            },
            Foundation = tile.Cells.Select(c => new CellPos(c.X, c.Y)).ToHashSet(),
        };
    }
}
