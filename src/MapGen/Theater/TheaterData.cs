using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapGen.Theater;

public sealed class TileCellInfo
{
    [JsonPropertyName("i")] public int Index { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("z")] public int Height { get; set; }
    [JsonPropertyName("t")] public int TerrainType { get; set; }
    [JsonPropertyName("r")] public int RampType { get; set; }
    [JsonPropertyName("rgb")] public string Rgb { get; set; }

    private byte[] radar;

    /// <summary>Left and right radar colours: r,g,b,r,g,b.</summary>
    public byte[] Radar => radar ??= Rgb.Split(',').Select(byte.Parse).ToArray();
}

public sealed class TileInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("set")] public int Set { get; set; }
    [JsonPropertyName("idx")] public int IndexInSet { get; set; }
    [JsonPropertyName("missing")] public bool Missing { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("variants")] public int Variants { get; set; }
    [JsonPropertyName("cells")] public List<TileCellInfo> Cells { get; set; } = new();

    public TileCellInfo GetSubTile(int subTile) => Cells.Find(c => c.Index == subTile);
}

public sealed class TileSetInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; }
    [JsonPropertyName("tilesInSet")] public int TilesInSet { get; set; }
    [JsonPropertyName("startTile")] public int StartTile { get; set; }
    [JsonPropertyName("morphable")] public bool Morphable { get; set; }
    [JsonPropertyName("allowTiberium")] public bool AllowTiberium { get; set; }
    [JsonPropertyName("allowToPlace")] public bool AllowToPlace { get; set; }
    [JsonPropertyName("marbleMadness")] public int MarbleMadness { get; set; }
    [JsonPropertyName("nonMarbleMadness")] public int NonMarbleMadness { get; set; }
}

public sealed class TerrainTypeInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("isAnimated")] public bool IsAnimated { get; set; }
    [JsonPropertyName("isVeinhole")] public bool IsVeinhole { get; set; }
    [JsonPropertyName("spawnsTiberium")] public bool SpawnsTiberium { get; set; }
    [JsonPropertyName("immune")] public bool Immune { get; set; }
    [JsonPropertyName("radarInvisible")] public bool RadarInvisible { get; set; }
    [JsonPropertyName("foundation")] public string Foundation { get; set; }
}

public sealed class OverlayTypeInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("tiberium")] public bool Tiberium { get; set; }
    [JsonPropertyName("wall")] public bool Wall { get; set; }
    [JsonPropertyName("land")] public string Land { get; set; }
}

public sealed class BuildingInfo
{
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("uiName")] public string UIName { get; set; }
    [JsonPropertyName("foundation")] public string Foundation { get; set; }
    [JsonPropertyName("capturable")] public bool Capturable { get; set; }
    [JsonPropertyName("canBeOccupied")] public bool CanBeOccupied { get; set; }
    [JsonPropertyName("maxNumberOccupants")] public int MaxNumberOccupants { get; set; }
    [JsonPropertyName("techLevel")] public int TechLevel { get; set; }
    [JsonPropertyName("strength")] public int Strength { get; set; }
    [JsonPropertyName("available")] public bool Available { get; set; } = true;

    public (int W, int H) FoundationSize => ParseFoundation(Foundation);

    public static (int W, int H) ParseFoundation(string foundation)
    {
        if (string.IsNullOrWhiteSpace(foundation))
            return (1, 1);

        var parts = foundation.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            return (w, h);

        // Custom foundations are not used by anything the generator places.
        return (1, 1);
    }
}

/// <summary>
/// Per-theater game data dumped from the MIX files by tools/TileDump.
/// </summary>
public sealed class TheaterData
{
    [JsonPropertyName("theater")] public string Name { get; set; }
    [JsonPropertyName("extension")] public string Extension { get; set; }
    [JsonPropertyName("general")] public Dictionary<string, string> General { get; set; }
    [JsonPropertyName("tileSets")] public List<TileSetInfo> TileSets { get; set; }
    [JsonPropertyName("tiles")] public List<TileInfo> Tiles { get; set; }
    [JsonPropertyName("terrainTypes")] public List<TerrainTypeInfo> TerrainTypes { get; set; }
    [JsonPropertyName("overlayTypes")] public List<OverlayTypeInfo> OverlayTypes { get; set; }
    [JsonPropertyName("buildings")] public List<BuildingInfo> Buildings { get; set; }

    /// <summary>Garrisonable civilian buildings the shipped maps use in this theater (data/garrisons.json).</summary>
    [JsonIgnore]
    public List<string> Garrisons { get; private set; } = new();

    private Dictionary<string, BuildingInfo> buildingsByName;
    private Dictionary<string, TerrainTypeInfo> terrainByName;

    public static TheaterData Load(string dataDirectory, string theater)
    {
        string path = Path.Combine(dataDirectory, theater.ToLowerInvariant() + ".json");
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<TheaterData>(stream);
        data.buildingsByName = data.Buildings.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        data.terrainByName = data.TerrainTypes.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string garrisons = Path.Combine(dataDirectory, "garrisons.json");
        if (File.Exists(garrisons) && !theater.Equals("LUNAR", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(garrisons));
            if (doc.RootElement.TryGetProperty(data.Name.ToUpperInvariant(), out var list))
                data.Garrisons = list.EnumerateArray().Select(e => e.GetString()).Where(n => data.Building(n)?.Available == true).ToList();
        }
        return data;
    }

    public int GeneralInt(string key, int defaultValue = -1)
        => General != null && General.TryGetValue(key, out var v) && int.TryParse(v, out int i) ? i : defaultValue;

    public TileSetInfo TileSet(int index) => index >= 0 && index < TileSets.Count ? TileSets[index] : null;

    public TileSetInfo TileSetByName(string name)
        => TileSets.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public TileInfo Tile(int id) => id >= 0 && id < Tiles.Count ? Tiles[id] : null;

    /// <summary>Tile ID of the n-th tile of a tile set, or -1.</summary>
    public int TileId(int setIndex, int indexInSet)
    {
        var set = TileSet(setIndex);
        if (set == null || indexInSet < 0 || indexInSet >= set.TilesInSet)
            return -1;
        return set.StartTile + indexInSet;
    }

    public BuildingInfo Building(string name) => buildingsByName.TryGetValue(name, out var b) ? b : null;

    public TerrainTypeInfo TerrainType(string name) => terrainByName.TryGetValue(name, out var t) ? t : null;

    public int OverlayIndex(string name)
        => OverlayTypes.FindIndex(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
}
