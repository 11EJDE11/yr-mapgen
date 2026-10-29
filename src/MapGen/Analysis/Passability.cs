using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Analysis;

public enum LandType : byte
{
    Clear, Road, Water, Rock, Wall, Tiberium, Beach, Rough, Ice, Railroad, Tunnel, Weeds
}

/// <summary>
/// Ground-vehicle passability and speed per cell, following CellClass::Recalc_Attributes /
/// Check_Passability (gamemd 0x47D2B0 / 0x483C80) closely enough for fairness comparisons.
/// </summary>
public sealed class Passability
{
    // IsometricTileTypeClass::Land_Type table at 0x8288E4: TMP terrain type -> LandType.
    private static readonly LandType[] TmpToLand =
    {
        LandType.Clear, LandType.Ice, LandType.Ice, LandType.Ice, LandType.Ice, LandType.Tunnel,
        LandType.Railroad, LandType.Rock, LandType.Rock, LandType.Water, LandType.Beach,
        LandType.Road, LandType.Road, LandType.Clear, LandType.Rough, LandType.Rock,
    };

    // Wheel speed multiplier per land type from rulesmd ([Clear] Wheel=100% etc.); harvesters are wheeled.
    private static readonly double[] WheelSpeed = { 1.0, 1.0, 0, 0, 0, 0.5, 0, 1.0, 0.5, 0.5, 1.0, 0.5 };

    private readonly MapModel map;

    public Passability(MapModel map, TheaterData theater)
    {
        this.map = map;
        int n = map.Span;
        Land = new LandType[n, n];
        Speed = new double[n, n];
        Blocked = new bool[n, n];
        Buildable = new bool[n, n];
        Compute(theater);
    }

    public LandType[,] Land { get; }

    /// <summary>Relative wheel speed (0 = impassable) for a ground vehicle.</summary>
    public double[,] Speed { get; }

    /// <summary>Cells blocked by an object (building, terrain object).</summary>
    public bool[,] Blocked { get; }

    /// <summary>Flat land that a building could be placed on.</summary>
    public bool[,] Buildable { get; }

    public bool IsPassable(CellPos p) => map.IsValid(p) && map.IsInLocal(p) && Speed[p.X, p.Y] > 0;

    private void Compute(TheaterData theater)
    {
        var tiberiumOverlays = new HashSet<int>(theater.OverlayTypes.Where(o => o.Tiberium).Select(o => o.Index));

        foreach (var p in map.AllValidCells())
        {
            var cell = map[p];
            var tile = theater.Tile(cell.TileIndex) ?? theater.Tile(0);
            var sub = tile?.GetSubTile(cell.SubTile);
            LandType land = sub != null && sub.TerrainType < TmpToLand.Length ? TmpToLand[sub.TerrainType] : LandType.Clear;
            bool sloped = sub != null && sub.RampType != 0;

            if (cell.Overlay >= 0)
            {
                if (tiberiumOverlays.Contains(cell.Overlay))
                {
                    if (land == LandType.Clear || land == LandType.Rough || land == LandType.Road)
                        land = LandType.Tiberium;
                }
                else
                {
                    var ovl = theater.OverlayTypes[cell.Overlay];
                    if (ovl.Wall)
                        land = LandType.Wall;
                    else if (!string.IsNullOrEmpty(ovl.Land) && Enum.TryParse(ovl.Land, true, out LandType ol))
                        land = ol;
                }
            }

            // CliffBackImpassability=2: clear-ish cells right behind a cliff 4+ levels higher are rock.
            if (land is LandType.Clear or LandType.Water or LandType.Beach or LandType.Ice && IsBehindCliff(p, cell.Level))
                land = LandType.Rock;

            Land[p.X, p.Y] = land;
            Speed[p.X, p.Y] = WheelSpeed[(int)land];
            Buildable[p.X, p.Y] = !sloped && land is LandType.Clear or LandType.Rough or LandType.Road;
        }

        foreach (var s in map.Structures)
        {
            var b = theater.Building(s.Type);
            var (w, h) = b != null ? b.FoundationSize : (1, 1);
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    Block(new CellPos(s.Position.X + dx, s.Position.Y + dy));
        }

        foreach (var t in map.TerrainObjects)
        {
            var info = theater.TerrainType(t.Type);
            var (w, h) = BuildingInfo.ParseFoundation(info?.Foundation);
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    Block(new CellPos(t.Position.X + dx, t.Position.Y + dy));
        }
    }

    private void Block(CellPos p)
    {
        if (!map.IsValid(p))
            return;
        Blocked[p.X, p.Y] = true;
        Speed[p.X, p.Y] = 0;
        Buildable[p.X, p.Y] = false;
    }

    private static readonly CellPos[] CliffBackOffsets =
    {
        new(0, -1), new(-1, 0), new(2, 2), new(1, 1), new(-1, 1), new(1, -1),
    };

    private bool IsBehindCliff(CellPos p, int level)
    {
        foreach (var o in CliffBackOffsets)
        {
            var q = p + o;
            if (map.IsValid(q) && map[q].Level >= level + 4)
                return true;
        }
        return false;
    }
}
