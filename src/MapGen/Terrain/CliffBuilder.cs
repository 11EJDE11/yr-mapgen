using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Terrain;

/// <summary>Places connected-tile plans (cliff lines) onto a map.</summary>
public static class CliffBuilder
{
    /// <summary>
    /// Writes a plan's tiles into the map. Each subtile's level is <paramref name="baseLevel"/> plus the
    /// subtile's own height, as in WAE's DrawConnectedTilesMutation.
    /// </summary>
    public static List<CellPos> Apply(MapModel map, TheaterData theater, ConnectedTilePlan plan, int baseLevel, Random rng)
    {
        var touched = new List<CellPos>();
        ConnectedTile last = null;
        int lastIndex = -1;

        // Same order as WAE: last placement first, so overlapping subtiles resolve identically.
        for (int i = plan.Placements.Count - 1; i >= 0; i--)
        {
            var placement = plan.Placements[i];
            var ct = placement.Tile;
            var set = theater.TileSet(ct.TileSetIndex);

            int pick;
            if (ct.IndicesInTileSet.Count > 1 && last == ct)
            {
                // Avoid repeating the same variant twice in a row.
                pick = rng.Next(ct.IndicesInTileSet.Count - 1);
                if (pick >= lastIndex)
                    pick++;
            }
            else
            {
                pick = rng.Next(ct.IndicesInTileSet.Count);
            }

            var tile = theater.Tile(set.StartTile + ct.IndicesInTileSet[pick]);
            last = ct;
            lastIndex = pick;
            if (tile == null || tile.Missing)
                continue;

            foreach (var cell in tile.Cells)
            {
                var p = new CellPos(placement.Location.X + cell.X, placement.Location.Y + cell.Y);
                if (!map.IsValid(p))
                    continue;
                ref var c = ref map[p];
                c.TileIndex = tile.Id;
                c.SubTile = (byte)cell.Index;
                c.Level = (byte)Math.Min(baseLevel + cell.Height, 14);
                touched.Add(p);
            }
        }

        return touched;
    }
}
