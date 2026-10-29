using System.Globalization;
using System.Text;
using MapGen.Model;

namespace MapGen.Formats;

/// <summary>Writes a <see cref="MapModel"/> as a YR multiplayer .map file.</summary>
public static class MapWriter
{
    // Same house list and order as the official multiplayer maps.
    private static readonly (string Name, string Color)[] Houses =
    {
        ("Americans", "Gold"), ("Alliance", "Gold"), ("French", "Gold"), ("Germans", "Gold"),
        ("British", "Gold"), ("Africans", "Gold"), ("Arabs", "Gold"), ("Confederation", "Gold"),
        ("Russians", "Gold"), ("YuriCountry", "Gold"), ("GDI", "Gold"), ("Nod", "Gold"),
        ("Neutral", "Grey"), ("Special", "Grey"),
    };

    public static void Write(MapModel map, string path)
    {
        File.WriteAllText(path, ToIni(map).Write(), Encoding.Latin1);
    }

    public static IniDocument ToIni(MapModel map)
    {
        var ini = new IniDocument();

        // The game and the client expect the preview sections first; a real preview can be injected later.
        ini.Set("Preview", "Size", "0,0,106,61");
        ini.Set("PreviewPack", "1", "yAsAIAXQ5PDQ5PDQ6JQATAEE6PDQ4PDI4JgBTAFEAkgAJyAATAG0AydEAEABpAJIA0wBVA");
        ini.Set("PreviewPack", "2", "BIACcgAEwBtAMnRABAAaQCSANMAVQASAAnIABMAbQDJ0QAQAGkAkgDTAFUAEgAJyAATAG0");

        WriteBasic(map, ini);
        ini.Set("Map", "Size", $"0,0,{map.Width},{map.Height}");
        ini.Set("Map", "Theater", map.Theater);
        ini.Set("Map", "LocalSize", $"{map.LocalX},{map.LocalY},{map.LocalWidth},{map.LocalHeight}");

        foreach (var (k, v) in map.Lighting)
            ini.Set("Lighting", k, v);

        WriteSpecialFlags(ini);
        WriteHouses(ini);
        WriteIsoMapPack(map, ini);
        WriteOverlays(map, ini);
        WriteWaypoints(map, ini);
        WriteStructures(map, ini);
        WriteTerrain(map, ini);
        WriteSmudges(map, ini);

        foreach (string empty in new[] { "Triggers", "Events", "Actions", "Tags", "TeamTypes", "ScriptTypes", "TaskForces", "AITriggerTypes" })
            ini.Section(empty);

        return ini;
    }

    private static void WriteBasic(MapModel map, IniDocument ini)
    {
        var b = ini.Section("Basic");
        b.Set("Name", map.Name);
        b.Set("Author", map.Author);
        b.Set("GameMode", map.GameMode);
        b.Set("MinPlayer", map.MinPlayers.ToString());
        b.Set("MaxPlayer", map.MaxPlayers.ToString());
        b.Set("EnforceMaxPlayers", "yes");
        b.Set("MultiplayerOnly", "1");
        b.Set("Official", "no");
        b.Set("NewINIFormat", "4");
        b.Set("CarryOverCap", "0");
        b.Set("EndOfGame", "no");
        b.Set("SkipScore", "no");
        b.Set("OneTimeOnly", "no");
        b.Set("SkipMapSelect", "no");
        b.Set("TruckCrate", "no");
        b.Set("TrainCrate", "no");
        b.Set("Percent", "0");
        b.Set("InitTime", "10000");
        b.Set("FreeRadar", "no");
        b.Set("TiberiumGrowthEnabled", "yes");
        b.Set("VeinGrowthEnabled", "no");
        b.Set("IceGrowthEnabled", "no");
        b.Set("TiberiumDeathToVisceroid", "no");
        b.Set("IgnoreGlobalAITriggers", "no");

        for (int i = 0; i < map.TeamStartMappings.Count; i++)
        {
            b.Set($"TeamStartMapping{i}", map.TeamStartMappings[i].Mapping);
            b.Set($"TeamStartMapping{i}Name", map.TeamStartMappings[i].Name);
        }

        foreach (var (k, v) in map.ExtraBasic)
            b.Set(k, v);
    }

    private static void WriteSpecialFlags(IniDocument ini)
    {
        var s = ini.Section("SpecialFlags");
        s.Set("TiberiumGrows", "yes");
        s.Set("TiberiumSpreads", "yes");
        s.Set("TiberiumExplosive", "no");
        s.Set("DestroyableBridges", "yes");
        s.Set("MCVDeploy", "no");
        s.Set("InitialVeteran", "no");
        s.Set("FixedAlliance", "no");
        s.Set("HarvesterImmune", "no");
        s.Set("FogOfWar", "no");
        s.Set("Inert", "no");
        s.Set("IonStorms", "no");
        s.Set("Meteorites", "no");
        s.Set("Visceroids", "no");
    }

    private static void WriteHouses(IniDocument ini)
    {
        var list = ini.Section("Houses");
        for (int i = 0; i < Houses.Length; i++)
            list.Set(i.ToString(), Houses[i].Name);

        foreach (var (name, color) in Houses)
        {
            var s = ini.Section(name);
            s.Set("IQ", "0");
            s.Set("Color", color);
            s.Set("Allies", name);
            s.Set("Country", name);
            s.Set("Credits", "0");
            s.Set("NodeCount", "0");
            s.Set("TechLevel", name is "Neutral" or "Special" ? "1" : "0");
            s.Set("PercentBuilt", "0");
        }
    }

    private static void WriteIsoMapPack(MapModel map, IniDocument ini)
    {
        // Every valid cell is written (including clear level 0) so the map does not depend on the
        // game's defaults; ordering by X, level, tile keeps the LZO blocks small.
        var cells = map.AllValidCells()
            .Select(p => (p, c: map[p]))
            .OrderBy(t => t.p.X).ThenBy(t => t.c.Level).ThenBy(t => t.c.TileIndex)
            .ToList();

        var buffer = new byte[cells.Count * 11 + 4];
        int o = 0;
        foreach (var (p, c) in cells)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buffer, o, 2), (short)p.X);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, o + 2, 2), (short)p.Y);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, o + 4, 4), c.TileIndex);
            buffer[o + 8] = c.SubTile;
            buffer[o + 9] = c.Level;
            buffer[o + 10] = 0;
            o += 11;
        }

        foreach (var (k, v) in PackEncoding.ToBase64Lines(PackEncoding.EncodeLzo(buffer)))
            ini.Set("IsoMapPack5", k, v);
    }

    private static void WriteOverlays(MapModel map, IniDocument ini)
    {
        var overlay = new byte[512 * 512];
        var data = new byte[512 * 512];
        Array.Fill(overlay, (byte)0xFF);

        foreach (var p in map.AllValidCells())
        {
            var c = map[p];
            if (c.Overlay < 0)
                continue;
            int idx = p.Y * 512 + p.X;
            overlay[idx] = (byte)c.Overlay;
            data[idx] = c.OverlayData;
        }

        foreach (var (k, v) in PackEncoding.ToBase64Lines(PackEncoding.EncodeFormat80(overlay)))
            ini.Set("OverlayPack", k, v);
        foreach (var (k, v) in PackEncoding.ToBase64Lines(PackEncoding.EncodeFormat80(data)))
            ini.Set("OverlayDataPack", k, v);
    }

    private static void WriteWaypoints(MapModel map, IniDocument ini)
    {
        var s = ini.Section("Waypoints");
        foreach (var (index, p) in map.Waypoints.OrderBy(w => w.Key))
            s.Set(index.ToString(), (p.Y * 1000 + p.X).ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteStructures(MapModel map, IniDocument ini)
    {
        var s = ini.Section("Structures");
        for (int i = 0; i < map.Structures.Count; i++)
        {
            var b = map.Structures[i];
            // owner,type,health,x,y,facing,tag,AISellable,AIRebuildable,Powered,Upgrades,SpotLight,
            // Upgrade1-3,AIRepairable,Nominal
            s.Set(i.ToString(), $"{b.Owner},{b.Type},{b.Health},{b.Position.X},{b.Position.Y},{b.Facing},None,1,0,1,0,0,None,None,None,0,0");
        }
    }

    private static void WriteTerrain(MapModel map, IniDocument ini)
    {
        var s = ini.Section("Terrain");
        foreach (var t in map.TerrainObjects.OrderBy(t => t.Position.Y).ThenBy(t => t.Position.X))
            s.Set((t.Position.Y * 1000 + t.Position.X).ToString(), t.Type);
    }

    private static void WriteSmudges(MapModel map, IniDocument ini)
    {
        if (map.Smudges.Count == 0)
            return;
        var s = ini.Section("Smudge");
        for (int i = 0; i < map.Smudges.Count; i++)
            s.Set(i.ToString(), $"{map.Smudges[i].Type},{map.Smudges[i].Position.X},{map.Smudges[i].Position.Y},0");
    }
}
