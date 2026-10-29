// Connected tile (cliff/shore/road) definitions, ported from World-Altering Editor
// (https://github.com/CnCNet/WorldAlteringEditor, GPL-3.0) for headless use.
// Data comes from WAE's YR ConnectedTileDrawer.ini (CnCNet YR client package).

using System.Globalization;
using MapGen.Formats;
using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Terrain;

public enum ConnectedTileSide
{
    Front,
    Back
}

/// <summary>Visual directions, clockwise from north-east (screen up-right).</summary>
public enum Direction
{
    NE = 0, E = 1, SE = 2, S = 3, SW = 4, W = 5, NW = 6, N = 7, Count = 8
}

public static class Directions
{
    private static readonly CellPos[] VisualDirectionToPointTable =
    {
        new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1)
    };

    public static CellPos ToPoint(Direction direction) => VisualDirectionToPointTable[(int)direction];

    public static List<Direction> InMask(byte mask)
    {
        var list = new List<Direction>();
        for (int d = 0; d < (int)Direction.Count; d++)
            if ((mask & (byte)(0b10000000 >> d)) != 0)
                list.Add((Direction)d);
        return list;
    }

    public static bool IsInStraightLine(CellPos a, CellPos b)
        => a.X == b.X || a.Y == b.Y || Math.Abs(b.X - a.X) == Math.Abs(b.Y - a.Y);
}

public readonly struct TileConnectionPoint
{
    public int Index { get; init; }
    public CellPos CoordinateOffset { get; init; }
    public byte ConnectionMask { get; init; }
    public byte ReversedConnectionMask => (byte)((ConnectionMask >> 4) + (0b11110000 & (ConnectionMask << 4)));
    public int[] RequiredTiles { get; init; }
    public int[] ForbiddenTiles { get; init; }
    public ConnectedTileSide Side { get; init; }
}

public sealed class ConnectedTile
{
    public int Index { get; set; }
    public string TileSetName { get; set; }
    public List<string> PossibleTileSetNames { get; set; }
    public List<int> IndicesInTileSet { get; set; }
    public TileConnectionPoint[] ConnectionPoints { get; set; }
    public HashSet<CellPos> Foundation { get; set; }
    public int ExtraPriority { get; set; }
    public int DistanceModifier { get; set; }

    /// <summary>Resolved tile set index in the current theater, or -1.</summary>
    public int TileSetIndex { get; set; } = -1;

    public bool IsEndingPiece => ConnectionPoints.Length == 1;

    internal static ConnectedTile Parse(IniSectionData section, int index)
    {
        var tile = new ConnectedTile { Index = index };

        string tileSet = section.Get("TileSet");
        if (string.IsNullOrWhiteSpace(tileSet))
            tile.PossibleTileSetNames = (section.Get("TileSets") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        else
            tile.TileSetName = tileSet.Trim();

        tile.IndicesInTileSet = (section.Get("TileIndices") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();

        var points = new List<TileConnectionPoint>();
        for (int i = 0; ; i++)
        {
            string coords = section.Get($"ConnectionPoint{i}");
            if (coords == null)
                break;

            var xy = coords.Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            string dirs = section.Get($"ConnectionPoint{i}.Directions") ?? "";
            byte mask = 0;
            foreach (string part in dirs.Split(','))
            {
                byte d = DirectionFromString(part);
                if (d != byte.MaxValue)
                    mask |= d;
            }
            if (mask == 0 && dirs.Length == 8 && dirs.All(c => c is '0' or '1'))
                mask = Convert.ToByte(dirs, 2);

            var side = (section.Get($"ConnectionPoint{i}.Side") ?? "").Trim().ToLowerInvariant() == "back"
                ? ConnectedTileSide.Back : ConnectedTileSide.Front;

            int[] ParseList(string key) => (section.Get(key) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();

            int[] required = ParseList($"ConnectionPoint{i}.RequiredTiles");
            int[] forbidden = required.Length > 0 ? Array.Empty<int>() : ParseList($"ConnectionPoint{i}.ForbiddenTiles");

            points.Add(new TileConnectionPoint
            {
                Index = i,
                CoordinateOffset = new CellPos(xy[0], xy[1]),
                ConnectionMask = mask,
                Side = side,
                RequiredTiles = required,
                ForbiddenTiles = forbidden,
            });
        }
        tile.ConnectionPoints = points.ToArray();

        string foundation = section.Get("Foundation");
        if (!string.IsNullOrWhiteSpace(foundation))
        {
            tile.Foundation = foundation.Split('|').Select(p =>
            {
                var xy = p.Split(',');
                return new CellPos(int.Parse(xy[0]), int.Parse(xy[1]));
            }).ToHashSet();
        }

        bool straight = tile.ConnectionPoints.Length == 2 &&
                        (tile.ConnectionPoints[0].ConnectionMask & tile.ConnectionPoints[1].ReversedConnectionMask) > 0;
        bool diagonal = tile.ConnectionPoints.Length == 2 && Directions
            .InMask((byte)(tile.ConnectionPoints[0].ConnectionMask & tile.ConnectionPoints[1].ReversedConnectionMask))
            .Any(d => d is Direction.E or Direction.S or Direction.W or Direction.N);

        // Negated because sorting is ascending; larger INI values mean more important.
        tile.ExtraPriority = -section.GetInt("ExtraPriority", straight ? -1 : 0);
        tile.DistanceModifier = section.GetInt("DistanceModifier", diagonal ? -3 : 0);
        return tile;
    }

    private static byte DirectionFromString(string str) => str.Trim().ToUpperInvariant() switch
    {
        "NORTH" or "TOPRIGHT" => 1 << 7,
        "NORTHEAST" or "RIGHT" => 1 << 6,
        "EAST" or "BOTTOMRIGHT" => 1 << 5,
        "SOUTHEAST" or "BOTTOM" => 1 << 4,
        "SOUTH" or "BOTTOMLEFT" => 1 << 3,
        "SOUTHWEST" or "LEFT" => 1 << 2,
        "WEST" or "TOPLEFT" => 1 << 1,
        "NORTHWEST" or "TOP" => 1 << 0,
        _ => byte.MaxValue,
    };
}

public sealed class ConnectedTileType
{
    public string IniName { get; init; }
    public string Name { get; init; }
    public bool FrontOnly { get; init; }
    public List<string> AllowedTheaters { get; init; }
    public List<ConnectedTile> Tiles { get; } = new();

    /// <summary>
    /// Loads every connected tile type usable in <paramref name="theater"/>, resolving tile sets and
    /// filling missing foundations from the tiles' actual cells (as WAE does).
    /// </summary>
    public static List<ConnectedTileType> Load(string iniPath, TheaterData theater)
    {
        var ini = IniDocument.Load(iniPath);
        var result = new List<ConnectedTileType>();

        foreach (var (_, typeName) in ini.TryGetSection("ConnectedTiles")?.Entries ?? Enumerable.Empty<(string, string)>())
        {
            var section = ini.TryGetSection(typeName);
            if (section == null)
                continue;

            var type = new ConnectedTileType
            {
                IniName = typeName,
                Name = section.Get("Name") ?? typeName,
                FrontOnly = (section.Get("FrontOnly") ?? "").Trim().ToLowerInvariant() is "yes" or "true" or "1",
                AllowedTheaters = (section.Get("AllowedTheaters") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            };

            if (!type.AllowedTheaters.Any(t => string.Equals(t, theater.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var s in ini.Sections)
            {
                var parts = s.Name.Split('.');
                if (parts.Length != 2 || parts[0] != typeName || !int.TryParse(parts[1], out int index))
                    continue;
                type.Tiles.Add(ConnectedTile.Parse(s, index));
            }

            if (type.Resolve(theater))
                result.Add(type);
        }

        return result;
    }

    private bool Resolve(TheaterData theater)
    {
        foreach (var tile in Tiles)
        {
            var names = tile.PossibleTileSetNames?.Count > 0 ? tile.PossibleTileSetNames : new List<string> { tile.TileSetName };
            TileSetInfo set = null;
            foreach (string name in names)
            {
                set = theater.TileSets.Find(ts => ts.Name == name && ts.AllowToPlace && ts.TilesInSet > 0);
                if (set != null)
                {
                    tile.TileSetName = name;
                    break;
                }
            }

            if (set == null)
                return false;
            tile.TileSetIndex = set.Index;

            if (tile.Foundation != null || tile.IndicesInTileSet.Count == 0)
                continue;

            var tileInfo = theater.Tile(set.StartTile + tile.IndicesInTileSet[0]);
            if (tileInfo == null || tileInfo.Missing)
                return false;
            tile.Foundation = tileInfo.Cells.Select(c => new CellPos(c.X, c.Y)).ToHashSet();
        }
        return true;
    }
}
