using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Terrain;

/// <summary>
/// Ground height as a field of cell-corner heights, after World-Altering Editor's CornerHeightField:
/// corners are relaxed so that neighbouring corners differ by at most one level, and each cell's
/// level and ramp tile follow from its four corners. Cells covered by fixed tiles (cliffs, water,
/// shores) are rigid: their corners are taken from the tile and never move.
/// </summary>
public sealed class HeightField
{
    // Corner offsets from a cell: NW, NE, SE, SW (cell (x, y) owns corner point (x, y)).
    private static readonly CellPos[] CornerOffsets = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };

    // Corner heights per RampType (TS++ TIBSUN_DEFINES), in CornerOffsets order.
    private static readonly int[][] RampCornerHeights =
    {
        new[] { 0, 0, 0, 0 }, // None
        new[] { 0, 1, 1, 0 }, // West
        new[] { 0, 0, 1, 1 }, // North
        new[] { 1, 0, 0, 1 }, // East
        new[] { 1, 1, 0, 0 }, // South
        new[] { 0, 0, 1, 0 }, // CornerNW
        new[] { 0, 0, 0, 1 }, // CornerNE
        new[] { 1, 0, 0, 0 }, // CornerSE
        new[] { 0, 1, 0, 0 }, // CornerSW
        new[] { 0, 1, 1, 1 }, // MidNW
        new[] { 1, 0, 1, 1 }, // MidNE
        new[] { 1, 1, 0, 1 }, // MidSE
        new[] { 1, 1, 1, 0 }, // MidSW
        new[] { 0, 1, 2, 1 }, // SteepSE
        new[] { 1, 0, 1, 2 }, // SteepSW
        new[] { 2, 1, 0, 1 }, // SteepNW
        new[] { 1, 2, 1, 0 }, // SteepNE
        new[] { 0, 1, 0, 1 }, // DoubleUpSWNE
        new[] { 1, 0, 1, 0 }, // DoubleDownSWNE
    };

    private static readonly Dictionary<int, int> RampByPattern = BuildLookup();

    private readonly MapModel map;
    private readonly TheaterData theater;
    private readonly int n;
    private readonly int[,] height;
    private readonly bool[,] rigid;

    public HeightField(MapModel map, TheaterData theater)
    {
        this.map = map;
        this.theater = theater;
        n = map.Span + 1;
        height = new int[n, n];
        rigid = new bool[n, n];
    }

    /// <summary>The ramp tile for a ramp type is RampBase's first tile + type - 1.</summary>
    private int RampTile(int rampType)
    {
        int set = theater.GeneralInt("RampBase", -1);
        return theater.TileId(set, rampType - 1);
    }

    /// <summary>
    /// Sets the corner field from desired per-cell levels: a corner takes the lowest level of the
    /// cells around it, so raised areas shrink towards their inside rather than spilling out.
    /// Corners of cells marked fixed (already tiled with cliffs, shores...) are taken from the map.
    /// </summary>
    public void Build(int[,] targetLevel, bool[,] fixedCells)
    {
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                height[x, y] = int.MaxValue;

        foreach (var p in map.AllValidCells())
        {
            for (int k = 0; k < 4; k++)
            {
                var c = p + CornerOffsets[k];
                if (fixedCells[p.X, p.Y])
                {
                    int h = map[p].Level + CornerRamp(p)[k];
                    if (!rigid[c.X, c.Y])
                    {
                        rigid[c.X, c.Y] = true;
                        height[c.X, c.Y] = h;
                    }
                    else
                    {
                        height[c.X, c.Y] = Math.Max(height[c.X, c.Y], h);
                    }
                }
                else if (!rigid[c.X, c.Y])
                {
                    height[c.X, c.Y] = Math.Min(height[c.X, c.Y], targetLevel[p.X, p.Y]);
                }
            }
        }

        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                if (height[x, y] == int.MaxValue)
                    height[x, y] = 0;
    }

    private int[] CornerRamp(CellPos p)
    {
        var sub = theater.Tile(map[p].TileIndex)?.GetSubTile(map[p].SubTile);
        int r = sub?.RampType ?? 0;
        return r >= 0 && r < RampCornerHeights.Length ? RampCornerHeights[r] : RampCornerHeights[0];
    }

    /// <summary>
    /// Lowers corners until no corner is more than one level above any of its eight neighbours.
    /// This is a distance transform (h = min over q of h0(q) + chebyshev(p, q)), so the result does
    /// not depend on processing order and a symmetric input gives a symmetric output.
    /// Rigid corners act as sources but never move.
    /// </summary>
    public void Relax()
    {
        var queue = new PriorityQueue<(int X, int Y), int>();
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                queue.Enqueue((x, y), height[x, y]);

        while (queue.TryDequeue(out var p, out int h))
        {
            // Rigid corners (cliff tiles) keep their height but do not pull ground down: the low
            // corners at a cliff's foot must not drag the plateau top behind it into a slope.
            if (h != height[p.X, p.Y] || rigid[p.X, p.Y])
                continue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = p.X + dx, ny = p.Y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= n || ny >= n || rigid[nx, ny])
                        continue;
                    if (height[nx, ny] > h + 1)
                    {
                        height[nx, ny] = h + 1;
                        queue.Enqueue((nx, ny), h + 1);
                    }
                }
        }
    }

    public int CornerHeight(CellPos corner) => height[corner.X, corner.Y];

    /// <summary>Whether all four corners of a cell are at the same height.</summary>
    public bool IsFlat(CellPos p)
    {
        int h = height[p.X, p.Y];
        return height[p.X + 1, p.Y] == h && height[p.X + 1, p.Y + 1] == h && height[p.X, p.Y + 1] == h;
    }

    public int LevelOf(CellPos p)
        => Math.Min(Math.Min(height[p.X, p.Y], height[p.X + 1, p.Y]), Math.Min(height[p.X + 1, p.Y + 1], height[p.X, p.Y + 1]));

    /// <summary>
    /// Writes levels and ramp tiles for every non-fixed cell. Flat cells keep their tile (it may be a
    /// ground patch) unless they were ramps; sloped cells get the matching ramp tile.
    /// </summary>
    public void WriteBack(bool[,] fixedCells, int clearTile)
    {
        foreach (var p in map.AllValidCells())
        {
            if (fixedCells[p.X, p.Y])
                continue;

            int c0 = height[p.X, p.Y], c1 = height[p.X + 1, p.Y], c2 = height[p.X + 1, p.Y + 1], c3 = height[p.X, p.Y + 1];
            int min = Math.Min(Math.Min(c0, c1), Math.Min(c2, c3));
            int max = Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
            ref var cell = ref map[p];
            cell.Level = (byte)Math.Clamp(min, 0, 14);

            int ramp = max - min <= 2 && RampByPattern.TryGetValue(Key(c0 - min, c1 - min, c2 - min, c3 - min), out int r) ? r : 0;
            if (ramp == 0)
            {
                var sub = theater.Tile(cell.TileIndex)?.GetSubTile(cell.SubTile);
                if (sub == null || sub.RampType != 0)
                {
                    cell.TileIndex = clearTile;
                    cell.SubTile = 0;
                }
            }
            else
            {
                int tile = RampTile(ramp);
                if (tile >= 0)
                {
                    cell.TileIndex = tile;
                    cell.SubTile = 0;
                }
            }
        }
    }

    private static int Key(int c0, int c1, int c2, int c3) => c0 + c1 * 3 + c2 * 9 + c3 * 27;

    private static Dictionary<int, int> BuildLookup()
    {
        var d = new Dictionary<int, int>();
        for (int r = 0; r < RampCornerHeights.Length; r++)
        {
            var h = RampCornerHeights[r];
            d.TryAdd(Key(h[0], h[1], h[2], h[3]), r);
        }
        return d;
    }
}
