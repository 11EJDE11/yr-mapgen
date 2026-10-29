using LightningMap;
using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Formats;

/// <summary>
/// Renders the preview embedded in [Preview]/[PreviewPack] (24-bit RGB rows), covering the visible
/// LocalSize area like the lobby .png. With the game's art available it is a LightningMap render;
/// without it, a radar-style picture built from the tiles' radar colours.
/// </summary>
public static class MapPreview
{
    private const int Ore = 0xE8C810;
    private const int Gem = 0xB040C8;
    private const int Wall = 0xA0A0A0;
    private const int Building = 0x383840;
    private const int Drill = 0x786428;
    private const int Tree = 0x1C3A18;
    private const int Rock = 0x707070;

    /// <summary>
    /// Four pixels per LocalSize column, twice what FinalAlert 2 writes, so the client's enlarged lobby
    /// preview stays sharp. The image is LocalWidth * p by about LocalHeight * p / 2 pixels; p shrinks so it
    /// stays within 75k pixels. Busy renders barely compress (up to ~4.3 bytes per pixel once LZO-packed
    /// and base64-encoded), so that keeps the preview under about 320 KB of the .map file.
    /// </summary>
    public static int RenderWidth(MapModel map)
    {
        double perColumn = Math.Min(4, Math.Sqrt(2 * 75_000.0 / (map.LocalWidth * map.LocalHeight)));
        return (int)Math.Round(map.LocalWidth * perColumn);
    }

    /// <summary>Renders the map file at <paramref name="mapPath"/> with LightningMap, without start markers.</summary>
    public static (int Width, int Height, byte[] Rgb) Render(string mapPath, RenderEngine renderer, int width)
    {
        var sink = new RgbSink();
        renderer.Render(mapPath, sink, new RenderOptions(Width: width, StartMarkers: false));
        return (sink.Width, sink.Height, sink.Rgb);
    }

    private sealed class RgbSink : IImageSink
    {
        private int row;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Rgb { get; private set; }

        public void Start(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = new byte[width * height * 3];
        }

        public void WriteRow(ReadOnlySpan<uint> pixels)
        {
            int o = row++ * Width * 3;
            foreach (uint argb in pixels)
            {
                Rgb[o++] = (byte)(argb >> 16);
                Rgb[o++] = (byte)(argb >> 8);
                Rgb[o++] = (byte)argb;
            }
        }

        public void Finish() { }
    }

    /// <summary>Radar-style preview at one pixel per half cell (2 * LocalWidth by LocalHeight).</summary>
    public static (int Width, int Height, byte[] Rgb) RenderRadar(MapModel map, TheaterData theater)
    {
        var canvas = new Canvas(map);

        // Back to front, as the game draws: a raised cell covers the lower cells behind it.
        var cells = map.AllValidCells()
            .Select(p => (p, c: map[p]))
            .OrderBy(t => t.p.X + t.p.Y).ThenBy(t => t.c.Level)
            .ToList();

        foreach (var (p, c) in cells)
        {
            var radar = theater.Tile(c.TileIndex)?.GetSubTile(c.SubTile)?.Radar;
            if (radar != null)
                canvas.PaintCell(p, c.Level, radar[0] << 16 | radar[1] << 8 | radar[2], radar[3] << 16 | radar[4] << 8 | radar[5]);
        }

        foreach (var (p, c) in cells)
        {
            if (c.Overlay < 0 || c.Overlay >= theater.OverlayTypes.Count)
                continue;
            var type = theater.OverlayTypes[c.Overlay];
            if (type.Tiberium)
                canvas.PaintCell(p, c.Level, type.Name.StartsWith("GEM", StringComparison.OrdinalIgnoreCase) ? Gem : Ore);
            else if (type.Wall)
                canvas.PaintCell(p, c.Level, Wall);
        }

        foreach (var t in map.TerrainObjects)
        {
            if (!map.IsValid(t.Position) || theater.TerrainType(t.Type)?.RadarInvisible == true)
                continue;
            if (theater.TerrainType(t.Type)?.SpawnsTiberium == true)
                canvas.PaintCell(t.Position, map[t.Position].Level, Drill);
            else
                canvas.TintCell(t.Position, map[t.Position].Level, t.Type.Contains("ROCK", StringComparison.OrdinalIgnoreCase) ? Rock : Tree, 0.6);
        }

        foreach (var s in map.Structures)
        {
            var (w, h) = theater.Building(s.Type)?.FoundationSize ?? (1, 1);
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                {
                    var p = new CellPos(s.Position.X + dx, s.Position.Y + dy);
                    if (map.IsValid(p))
                        canvas.PaintCell(p, map[p].Level, Building);
                }
        }

        return (canvas.Width, canvas.Height, canvas.ToRgb());
    }

    /// <summary>
    /// The LocalSize area in units of half a cell each way (30x15 screen pixels). A cell's diamond is
    /// centred on a unit corner and fills one triangle in each of the four units round that corner, so
    /// every unit holds an upper and a lower triangle, each from a different cell.
    /// </summary>
    private sealed class Canvas
    {
        private readonly MapModel map;
        private readonly int columns, rows, originColumn, originRow;
        private readonly int[] upper, lower;

        public Canvas(MapModel map)
        {
            this.map = map;
            columns = map.LocalWidth * 2;
            rows = map.LocalHeight * 2;
            originColumn = map.LocalX * 2;
            originRow = map.LocalY * 2;
            upper = new int[columns * rows];
            lower = new int[columns * rows];
        }

        public int Width => columns;
        public int Height => rows / 2;

        // Same placement as the game (and LightningMap): the diamond's centre is at
        // ((x - y + W) * 30, (x + y - W - level) * 15) screen pixels.
        private (int Column, int Row) Centre(CellPos p, int level)
            => (p.X - p.Y + map.Width - originColumn, p.X + p.Y - map.Width - level - originRow);

        public void PaintCell(CellPos p, int level, int colour) => PaintCell(p, level, colour, colour);

        public void PaintCell(CellPos p, int level, int left, int right)
        {
            var (cx, cy) = Centre(p, level);
            Set(lower, cx - 1, cy - 1, left);
            Set(lower, cx, cy - 1, right);
            Set(upper, cx - 1, cy, left);
            Set(upper, cx, cy, right);
        }

        /// <summary>Blends <paramref name="colour"/> over what the cell already shows.</summary>
        public void TintCell(CellPos p, int level, int colour, double amount)
        {
            var (cx, cy) = Centre(p, level);
            Blend(lower, cx - 1, cy - 1, colour, amount);
            Blend(lower, cx, cy - 1, colour, amount);
            Blend(upper, cx - 1, cy, colour, amount);
            Blend(upper, cx, cy, colour, amount);
        }

        private int Index(int column, int row)
            => column < 0 || row < 0 || column >= columns || row >= rows ? -1 : row * columns + column;

        private void Set(int[] triangles, int column, int row, int colour)
        {
            int i = Index(column, row);
            if (i >= 0)
                triangles[i] = colour;
        }

        private void Blend(int[] triangles, int column, int row, int colour, double amount)
        {
            int i = Index(column, row);
            if (i < 0)
                return;
            int old = triangles[i];
            int Mix(int shift) => (int)Math.Round(((old >> shift) & 0xFF) * (1 - amount) + ((colour >> shift) & 0xFF) * amount);
            triangles[i] = Mix(16) << 16 | Mix(8) << 8 | Mix(0);
        }

        /// <summary>Each output pixel is one unit wide and two tall: the average of its four triangles.</summary>
        public byte[] ToRgb()
        {
            var rgb = new byte[Width * Height * 3];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int a = (2 * y) * columns + x, b = a + columns, o = (y * Width + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int shift = 16 - 8 * c;
                        int sum = ((upper[a] >> shift) & 0xFF) + ((lower[a] >> shift) & 0xFF)
                                + ((upper[b] >> shift) & 0xFF) + ((lower[b] >> shift) & 0xFF);
                        rgb[o + c] = (byte)((sum + 2) / 4);
                    }
                }
            return rgb;
        }
    }
}
