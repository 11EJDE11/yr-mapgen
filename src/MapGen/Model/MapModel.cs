namespace MapGen.Model;

public readonly record struct CellPos(int X, int Y)
{
    public static CellPos operator +(CellPos a, CellPos b) => new(a.X + b.X, a.Y + b.Y);
    public static CellPos operator -(CellPos a, CellPos b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(CellPos o) => Math.Sqrt((X - o.X) * (double)(X - o.X) + (Y - o.Y) * (double)(Y - o.Y));
    public override string ToString() => $"{X},{Y}";

    /// <summary>The eight neighbours, orthogonal first.</summary>
    public static readonly CellPos[] Neighbours8 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(-1, -1), new(1, -1), new(-1, 1),
    };
}

public struct MapCell
{
    public int TileIndex;
    public byte SubTile;
    public byte Level;
    public short Overlay;     // -1 = none
    public byte OverlayData;
}

public sealed class StructureObject
{
    public string Owner = "Neutral";
    public string Type;
    public CellPos Position;   // origin (top) cell of the foundation
    public int Health = 256;
    public int Facing = 64;
}

public sealed class TerrainObject
{
    public string Type;
    public CellPos Position;
}

public sealed class SmudgeObject
{
    public string Type;
    public CellPos Position;
}

/// <summary>
/// Cell-space model of an RA2/YR map. Cell (x, y) is valid when it lies inside the diamond
/// x + y > W, |x - y| < W, x + y <= W + 2H for Size=0,0,W,H.
/// </summary>
public sealed class MapModel
{
    public MapModel(string theater, int width, int height)
    {
        Theater = theater;
        Width = width;
        Height = height;
        Span = width + height + 2;
        Cells = new MapCell[Span, Span];
        for (int x = 0; x < Span; x++)
            for (int y = 0; y < Span; y++)
                Cells[x, y].Overlay = -1;

        // Margins match what official maps commonly use.
        LocalX = 2;
        LocalY = 4;
        LocalWidth = width - 4;
        LocalHeight = height - 10;
    }

    public string Theater { get; }
    public int Width { get; }
    public int Height { get; }
    public int Span { get; }
    public MapCell[,] Cells { get; }

    public int LocalX { get; set; }
    public int LocalY { get; set; }
    public int LocalWidth { get; set; }
    public int LocalHeight { get; set; }

    public string Name { get; set; } = "Generated Map";
    public string Author { get; set; } = "yr-mapgen";
    public string GameMode { get; set; } = "Standard";
    public int MinPlayers { get; set; } = 2;
    public int MaxPlayers { get; set; } = 2;
    public List<(string Name, string Mapping)> TeamStartMappings { get; } = new();
    public Dictionary<string, string> Lighting { get; } = new();
    public Dictionary<string, string> ExtraBasic { get; } = new();

    public Dictionary<int, CellPos> Waypoints { get; } = new();
    public List<StructureObject> Structures { get; } = new();
    public List<TerrainObject> TerrainObjects { get; } = new();
    public List<SmudgeObject> Smudges { get; } = new();

    public bool IsValid(int x, int y)
    {
        if (x <= 0 || y <= 0 || x >= Span || y >= Span)
            return false;
        if (x + y < Width + 1) return false;
        if (x - y > Width - 1) return false;
        if (y - x > Width - 1) return false;
        if (x + y > Height * 2 + Width) return false;
        return true;
    }

    public bool IsValid(CellPos p) => IsValid(p.X, p.Y);

    public ref MapCell this[CellPos p] => ref Cells[p.X, p.Y];

    /// <summary>Screen position of the cell's top corner, in pixels, with heights ignored.</summary>
    public (int X, int Y) ToScreen(CellPos p) => ((p.X - p.Y + Width - 1) * 30, (p.X + p.Y - Width - 1) * 15);

    /// <summary>Whether a cell (at height 0) is inside the playable LocalSize rectangle.</summary>
    public bool IsInLocal(CellPos p, int marginCells = 0)
    {
        if (!IsValid(p))
            return false;
        var (sx, sy) = ToScreen(p);
        int left = (LocalX + marginCells) * 60;
        int right = (LocalX + LocalWidth - marginCells) * 60;
        int top = (LocalY + marginCells) * 30;
        int bottom = (LocalY + LocalHeight - marginCells) * 30;
        return sx >= left && sx + 60 <= right && sy >= top && sy + 30 <= bottom;
    }

    /// <summary>Converts a normalised position inside the LocalSize rectangle to the nearest cell.</summary>
    public CellPos FromNormalisedLocal(double nx, double ny)
    {
        double sx = (LocalX + nx * LocalWidth) * 60.0 - 30;
        double sy = (LocalY + ny * LocalHeight) * 30.0 - 15;
        double u = sx / 30.0 - (Width - 1);   // x - y
        double v = sy / 15.0 + Width + 1;     // x + y
        int x = (int)Math.Round((u + v) / 2.0);
        int y = (int)Math.Round((v - u) / 2.0);
        return new CellPos(x, y);
    }

    public IEnumerable<CellPos> AllValidCells()
    {
        for (int x = 1; x < Span; x++)
            for (int y = 1; y < Span; y++)
                if (IsValid(x, y))
                    yield return new CellPos(x, y);
    }
}
