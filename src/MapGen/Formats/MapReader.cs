using MapGen.Model;

namespace MapGen.Formats;

/// <summary>Reads the parts of a .map file the fairness analyser needs.</summary>
public static class MapReader
{
    public static MapModel Read(string path) => Read(IniDocument.Load(path));

    public static MapModel Read(IniDocument ini)
    {
        int[] size = ParseInts(ini.Get("Map", "Size", "0,0,0,0"));
        int[] local = ParseInts(ini.Get("Map", "LocalSize", "0,0,0,0"));
        string theater = ini.Get("Map", "Theater", "TEMPERATE").ToUpperInvariant();

        var map = new MapModel(theater, size[2], size[3])
        {
            LocalX = local[0],
            LocalY = local[1],
            LocalWidth = local[2],
            LocalHeight = local[3],
            Name = ini.Get("Basic", "Name", "")
        };

        var iso = ini.TryGetSection("IsoMapPack5");
        if (iso != null)
        {
            byte[] raw = PackEncoding.DecodeLzo(PackEncoding.FromBase64Section(iso.Values));
            for (int o = 0; o + 11 <= raw.Length; o += 11)
            {
                int x = BitConverter.ToInt16(raw, o);
                int y = BitConverter.ToInt16(raw, o + 2);
                if (!map.IsValid(x, y))
                    continue;
                int tile = BitConverter.ToInt32(raw, o + 4);
                ref var c = ref map.Cells[x, y];
                c.TileIndex = tile is < 0 or 0xFFFF ? 0 : tile;
                c.SubTile = raw[o + 8];
                c.Level = raw[o + 9];
            }
        }

        var ovl = ini.TryGetSection("OverlayPack");
        var ovlData = ini.TryGetSection("OverlayDataPack");
        if (ovl != null)
        {
            byte[] o = PackEncoding.DecodeFormat80(PackEncoding.FromBase64Section(ovl.Values));
            byte[] d = ovlData != null ? PackEncoding.DecodeFormat80(PackEncoding.FromBase64Section(ovlData.Values)) : null;
            for (int i = 0; i < o.Length && i < 512 * 512; i++)
            {
                if (o[i] == 0xFF)
                    continue;
                int x = i % 512, y = i / 512;
                if (!map.IsValid(x, y))
                    continue;
                map.Cells[x, y].Overlay = o[i];
                map.Cells[x, y].OverlayData = d != null && i < d.Length ? d[i] : (byte)0;
            }
        }

        foreach (var (k, v) in ini.TryGetSection("Waypoints")?.Entries ?? Enumerable.Empty<(string, string)>())
        {
            if (int.TryParse(k, out int idx) && int.TryParse(v, out int packed))
                map.Waypoints[idx] = new CellPos(packed % 1000, packed / 1000);
        }

        foreach (var (_, v) in ini.TryGetSection("Structures")?.Entries ?? Enumerable.Empty<(string, string)>())
        {
            var p = v.Split(',');
            if (p.Length < 5 || !int.TryParse(p[3], out int x) || !int.TryParse(p[4], out int y))
                continue;
            map.Structures.Add(new StructureObject { Owner = p[0], Type = p[1], Position = new CellPos(x, y) });
        }

        foreach (var (k, v) in ini.TryGetSection("Terrain")?.Entries ?? Enumerable.Empty<(string, string)>())
        {
            if (int.TryParse(k, out int packed))
                map.TerrainObjects.Add(new TerrainObject { Type = v, Position = new CellPos(packed % 1000, packed / 1000) });
        }

        return map;
    }

    private static int[] ParseInts(string s)
    {
        var parts = s.Split(',').Select(p => int.TryParse(p.Trim(), out int i) ? i : 0).ToList();
        while (parts.Count < 4)
            parts.Add(0);
        return parts.ToArray();
    }
}
