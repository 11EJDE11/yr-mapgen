// Dumps the per-theater data the generator needs from the game's MIX files:
// tile sets, every tile's subtile cells (height, terrain type, ramp, radar colour),
// [General] tile-set numbers, which terrain objects exist in the theater, and
// building/terrain foundations from art.
//
// Usage: TileDump <gameDir> <outDir>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CNCMaps.FileFormats;
using CNCMaps.FileFormats.VirtualFileSystem;
using CNCMaps.Shared;

static class Program
{
    static readonly (string name, string ini, string ext, string[] mixes)[] Theaters =
    {
        ("TEMPERATE", "temperatmd.ini", ".tem", new[] { "isotemp.mix", "isotemmd.mix", "temperat.mix", "tem.mix" }),
        ("SNOW", "snowmd.ini", ".sno", new[] { "isosnomd.mix", "snowmd.mix", "isosnow.mix", "snow.mix", "sno.mix" }),
        ("URBAN", "urbanmd.ini", ".urb", new[] { "isourbmd.mix", "isourb.mix", "urb.mix", "urban.mix" }),
        ("NEWURBAN", "urbannmd.ini", ".ubn", new[] { "isoubnmd.mix", "isoubn.mix", "ubn.mix", "urbann.mix" }),
        ("DESERT", "desertmd.ini", ".des", new[] { "isodesmd.mix", "desert.mix", "des.mix", "isodes.mix" }),
        ("LUNAR", "lunarmd.ini", ".lun", new[] { "isolunmd.mix", "isolun.mix", "lun.mix", "lunar.mix" }),
    };

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: TileDump <gameDir> <outDir>");
            return 1;
        }

        string gameDir = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        foreach (var th in Theaters)
        {
            using (var vfs = new VirtualFileSystem())
            {
                vfs.LoadMixes(gameDir, EngineType.YurisRevenge);
                foreach (string mix in th.mixes)
                    vfs.Add(mix);

                var rules = vfs.Open<IniFile>("rulesmd.ini");
                var art = vfs.Open<IniFile>("artmd.ini");
                var theaterIni = vfs.Open<IniFile>(th.ini);
                if (theaterIni == null)
                {
                    Console.Error.WriteLine($"{th.name}: {th.ini} not found, skipped");
                    continue;
                }

                var sb = new StringBuilder();
                var w = new JsonWriter(sb);
                w.BeginObject();
                w.Prop("theater", th.name);
                w.Prop("extension", th.ext);

                w.Key("general");
                w.BeginObject();
                foreach (var kv in theaterIni.GetSection("General").OrderedEntries)
                    w.Prop(kv.Key, (string)kv.Value);
                w.EndObject();

                DumpTiles(vfs, theaterIni, th.ext, th.name, w);
                DumpTerrainTypes(vfs, rules, art, th.ext, th.name, w);
                DumpOverlayTypes(rules, w);
                DumpFoundations(vfs, rules, art, th.ext, th.name, w);

                w.EndObject();
                string path = Path.Combine(outDir, th.name.ToLowerInvariant() + ".json");
                File.WriteAllText(path, sb.ToString());
                Console.WriteLine($"{th.name}: wrote {path}");
            }
        }

        return 0;
    }

    static void DumpTiles(VirtualFileSystem vfs, IniFile theaterIni, string ext, string theater, JsonWriter w)
    {
        w.Key("tileSets");
        w.BeginArray();
        int tileId = 0;
        var tileRecords = new List<Action>();
        for (int setIdx = 0; ; setIdx++)
        {
            var sect = theaterIni.GetSection("TileSet" + setIdx.ToString("0000"));
            if (sect == null)
                break;

            string fileName = sect.ReadString("FileName");
            string setName = sect.ReadString("SetName");
            int tilesInSet = sect.ReadInt("TilesInSet");

            w.BeginObject();
            w.Prop("index", setIdx);
            w.Prop("name", setName);
            w.Prop("fileName", fileName);
            w.Prop("tilesInSet", tilesInSet);
            w.Prop("startTile", tileId);
            w.Prop("morphable", sect.ReadBool("Morphable"));
            w.Prop("allowTiberium", sect.ReadBool("AllowTiberium"));
            w.Prop("allowToPlace", sect.ReadBool("AllowToPlace", true));
            w.Prop("marbleMadness", sect.ReadInt("MarbleMadness", -1));
            w.Prop("nonMarbleMadness", sect.ReadInt("NonMarbleMadness", -1));
            w.EndObject();

            for (int j = 1; j <= tilesInSet; j++, tileId++)
            {
                int id = tileId, set = setIdx, idx = j - 1;
                string baseName = fileName + j.ToString("00");
                byte[] data = ReadFile(vfs, baseName + ext);
                if (data == null && theater == "NEWURBAN")
                    data = ReadFile(vfs, baseName + ".tem");
                int variants = 0;
                if (data != null)
                {
                    variants = 1;
                    for (char r = 'a'; r <= 'z'; r++)
                    {
                        if (!vfs.FileExists(baseName + r + ext))
                            break;
                        variants++;
                    }
                }

                tileRecords.Add(() => WriteTile(w, id, set, idx, variants, data));
            }
        }
        w.EndArray();

        w.Key("tiles");
        w.BeginArray();
        foreach (var rec in tileRecords)
            rec();
        w.EndArray();
    }

    static byte[] ReadFile(VirtualFileSystem vfs, string name)
    {
        var f = vfs.Open(name);
        if (f == null)
            return null;
        f.Position = 0;
        return f.Read((int)f.Length);
    }

    static void WriteTile(JsonWriter w, int id, int set, int idx, int variants, byte[] data)
    {
        w.BeginObject();
        w.Prop("id", id);
        w.Prop("set", set);
        w.Prop("idx", idx);
        if (data == null)
        {
            w.Prop("missing", true);
            w.EndObject();
            return;
        }

        int width = BitConverter.ToInt32(data, 0);
        int height = BitConverter.ToInt32(data, 4);
        w.Prop("w", width);
        w.Prop("h", height);
        w.Prop("variants", variants);
        w.Key("cells");
        w.BeginArray();
        for (int i = 0; i < width * height; i++)
        {
            int off = BitConverter.ToInt32(data, 16 + i * 4);
            if (off == 0)
                continue;

            // TMP image header: X, Y, 3 offsets, extra rect (4 ints), flags, then height/terrain/ramp/radar bytes
            int px = BitConverter.ToInt32(data, off);
            int py = BitConverter.ToInt32(data, off + 4);
            int b = off + 40;
            w.BeginObject();
            w.Prop("i", i);
            w.Prop("x", i % width);
            w.Prop("y", i / width);
            w.Prop("px", px);
            w.Prop("py", py);
            w.Prop("z", data[b]);
            w.Prop("t", data[b + 1]);
            w.Prop("r", data[b + 2]);
            w.Prop("rgb", $"{data[b + 3]},{data[b + 4]},{data[b + 5]},{data[b + 6]},{data[b + 7]},{data[b + 8]}");
            w.EndObject();
        }
        w.EndArray();
        w.EndObject();
    }

    static void DumpTerrainTypes(VirtualFileSystem vfs, IniFile rules, IniFile art, string ext, string theater, JsonWriter w)
    {
        w.Key("terrainTypes");
        w.BeginArray();
        var list = rules.GetSection("TerrainTypes");
        int index = 0;
        foreach (var kv in list.OrderedEntries)
        {
            string name = ((string)kv.Value).Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            var rs = rules.GetSection(name);
            var artSect = art.GetSection(rs?.ReadString("Image", name) ?? name) ?? art.GetSection(name);
            string image = artSect?.ReadString("Image", null) ?? rs?.ReadString("Image", name) ?? name;

            // Theater-specific terrain art uses the theater extension; generic art is .shp.
            bool available = vfs.FileExists(image + ext) || (theater == "NEWURBAN" && vfs.FileExists(image + ".tem"));
            w.BeginObject();
            w.Prop("index", index++);
            w.Prop("name", name);
            w.Prop("available", available);
            w.Prop("isAnimated", rs?.ReadBool("IsAnimated") ?? false);
            w.Prop("isVeinhole", rs?.ReadBool("IsVeinhole") ?? false);
            w.Prop("spawnsTiberium", rs?.ReadBool("SpawnsTiberium") ?? false);
            w.Prop("immune", rs?.ReadBool("Immune") ?? false);
            w.Prop("radarInvisible", rs?.ReadBool("RadarInvisible") ?? false);
            w.Prop("foundation", artSect?.ReadString("Foundation", "1x1") ?? "1x1");
            w.EndObject();
        }
        w.EndArray();
    }

    static void DumpOverlayTypes(IniFile rules, JsonWriter w)
    {
        w.Key("overlayTypes");
        w.BeginArray();
        int index = 0;
        foreach (var kv in rules.GetSection("OverlayTypes").OrderedEntries)
        {
            string name = ((string)kv.Value).Trim();
            var rs = rules.GetSection(name);
            w.BeginObject();
            w.Prop("index", index++);
            w.Prop("name", name);
            w.Prop("tiberium", rs?.ReadBool("Tiberium") ?? false);
            w.Prop("wall", rs?.ReadBool("Wall") ?? false);
            w.Prop("land", rs?.ReadString("Land", "") ?? "");
            w.EndObject();
        }
        w.EndArray();
    }

    static readonly Dictionary<string, char> NewTheaterChar = new Dictionary<string, char>
    {
        ["TEMPERATE"] = 'T', ["SNOW"] = 'A', ["URBAN"] = 'U', ["NEWURBAN"] = 'N', ["DESERT"] = 'D', ["LUNAR"] = 'L',
    };

    /// <summary>
    /// Whether a building's art exists in the theater, following the game's naming: NewTheater=yes
    /// replaces the second letter with the theater letter (falling back to the generic 'G' name),
    /// Theater=yes uses the theater extension, otherwise .shp.
    /// </summary>
    static bool BuildingArtExists(VirtualFileSystem vfs, IniFile.IniSection artSect, string image, string ext, string theater)
    {
        if (artSect != null && artSect.ReadBool("NewTheater") && image.Length > 1)
        {
            var chars = image.ToCharArray();
            chars[1] = NewTheaterChar[theater];
            if (vfs.FileExists(new string(chars) + ".shp"))
                return true;
            chars[1] = 'G';
            return vfs.FileExists(new string(chars) + ".shp");
        }
        if (artSect != null && artSect.ReadBool("Theater"))
            return vfs.FileExists(image + ext);
        return vfs.FileExists(image + ".shp") || vfs.FileExists(image + ".vxl");
    }

    static void DumpFoundations(VirtualFileSystem vfs, IniFile rules, IniFile art, string ext, string theater, JsonWriter w)
    {
        w.Key("buildings");
        w.BeginArray();
        foreach (var kv in rules.GetSection("BuildingTypes").OrderedEntries)
        {
            string name = ((string)kv.Value).Trim();
            var rs = rules.GetSection(name);
            if (rs == null)
                continue;
            string image = rs.ReadString("Image", name);
            var artSect = art.GetSection(image) ?? art.GetSection(name);
            w.BeginObject();
            w.Prop("name", name);
            w.Prop("uiName", rs.ReadString("Name", ""));
            w.Prop("foundation", artSect?.ReadString("Foundation", "1x1") ?? "1x1");
            w.Prop("capturable", rs.ReadBool("Capturable"));
            w.Prop("canBeOccupied", rs.ReadBool("CanBeOccupied"));
            w.Prop("maxNumberOccupants", rs.ReadInt("MaxNumberOccupants"));
            w.Prop("techLevel", rs.ReadInt("TechLevel", -1));
            w.Prop("strength", rs.ReadInt("Strength", 0));
            w.Prop("available", BuildingArtExists(vfs, artSect, artSect?.ReadString("Image", image) ?? image, ext, theater));
            w.Prop("insignificant", rs.ReadBool("Insignificant"));
            w.Prop("wall", rs.ReadBool("Wall"));
            w.EndObject();
        }
        w.EndArray();
    }
}

/// <summary>
/// Minimal streaming JSON writer; System.Text.Json is not available on net48 without packages.
/// </summary>
sealed class JsonWriter
{
    private readonly StringBuilder sb;
    private readonly Stack<bool> first = new Stack<bool>();
    private bool afterKey;

    public JsonWriter(StringBuilder sb) { this.sb = sb; }

    private void Sep()
    {
        if (afterKey) { afterKey = false; return; }
        if (first.Count == 0) return;
        if (first.Peek()) { first.Pop(); first.Push(false); }
        else sb.Append(',');
    }

    public void BeginObject() { Sep(); sb.Append('{'); first.Push(true); }
    public void EndObject() { first.Pop(); sb.Append('}'); }
    public void BeginArray() { Sep(); sb.Append('['); first.Push(true); }
    public void EndArray() { first.Pop(); sb.Append("]\n"); }
    public void Key(string k) { Sep(); Str(k); sb.Append(':'); afterKey = true; }
    public void Prop(string k, string v) { Key(k); Sep(); Str(v); }
    public void Prop(string k, int v) { Key(k); Sep(); sb.Append(v); }
    public void Prop(string k, bool v) { Key(k); Sep(); sb.Append(v ? "true" : "false"); }

    private void Str(string s)
    {
        sb.Append('"');
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
