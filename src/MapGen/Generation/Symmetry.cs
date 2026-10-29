using MapGen.Model;

namespace MapGen.Generation;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SymmetryKind
{
    /// <summary>180° rotation about the map centre.</summary>
    Rotate180,

    /// <summary>Left-right mirror (screen space).</summary>
    MirrorLeftRight,

    /// <summary>Top-bottom mirror (screen space).</summary>
    MirrorTopBottom,

    /// <summary>Both mirrors: four quadrants, each a mirror image of its neighbours.</summary>
    Quad,

    /// <summary>90° rotation in cell space: a diamond-shaped layout with four rotated quarters.</summary>
    Diamond,

    /// <summary>
    /// No symmetry in the scenery (lakes, closed cliffs, trees, decoration). Bases, resources, tech,
    /// hills and ramped plateaus still follow one of the other symmetries so the map stays fair.
    /// </summary>
    None,
}

/// <summary>
/// A symmetry group acting on cell coordinates. All transforms are isometries of the cell grid,
/// so movement and distances are preserved exactly.
///
/// Screen coordinates are u = x - y (horizontal) and v = x + y (vertical). With c = W + H:
///   left-right mirror  (u, v) -> (-u, v)       : (x, y) -> (y, x)
///   top-bottom mirror  (u, v) -> (u, 2c - v)   : (x, y) -> (c - y, c - x)
///   180° rotation                               : (x, y) -> (c - x, c - y)
///   90° cell rotation about (c/2, c/2)          : (x, y) -> (c - y, x)
/// The top-bottom mirror maps the valid map diamond onto itself shifted by one half-row, which only
/// matters at the very edge of the map; generation stays inside the usable area.
/// </summary>
public sealed class Symmetry
{
    private readonly int c;
    private readonly Func<CellPos, CellPos>[] transforms;

    public Symmetry(SymmetryKind kind, MapModel map)
    {
        Kind = kind;
        c = map.Width + map.Height;

        CellPos Identity(CellPos p) => p;
        CellPos LR(CellPos p) => new(p.Y, p.X);
        CellPos TB(CellPos p) => new(c - p.Y, c - p.X);
        CellPos R180(CellPos p) => new(c - p.X, c - p.Y);
        CellPos R90(CellPos p) => new(c - p.Y, p.X);

        transforms = kind switch
        {
            SymmetryKind.Rotate180 => new Func<CellPos, CellPos>[] { Identity, R180 },
            SymmetryKind.MirrorLeftRight => new Func<CellPos, CellPos>[] { Identity, LR },
            SymmetryKind.MirrorTopBottom => new Func<CellPos, CellPos>[] { Identity, TB },
            SymmetryKind.Quad => new Func<CellPos, CellPos>[] { Identity, LR, TB, R180 },
            SymmetryKind.Diamond => new Func<CellPos, CellPos>[] { Identity, R90, p => R90(R90(p)), p => R90(R90(R90(p))) },
            SymmetryKind.None => new Func<CellPos, CellPos>[] { Identity },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Each transform's position in the list is also used as the "image index" for objects:
        // image i of a canonical object is produced by transforms[i].
    }

    public SymmetryKind Kind { get; }

    public int Order => transforms.Length;

    public CellPos Apply(int transformIndex, CellPos p) => transforms[transformIndex](p);

    public IEnumerable<CellPos> Orbit(CellPos p) => transforms.Select(t => t(p)).Distinct();

    /// <summary>
    /// Whether p lies in the fundamental domain: exactly one cell of each orbit is canonical,
    /// and every random decision is made on canonical cells only.
    /// </summary>
    public bool IsCanonical(CellPos p) => Canonical(p) == p;

    public CellPos Canonical(CellPos p)
    {
        var best = p;
        long bestKey = Key(p);
        for (int i = 1; i < transforms.Length; i++)
        {
            var q = transforms[i](p);
            long k = Key(q);
            if (k < bestKey)
            {
                best = q;
                bestKey = k;
            }
        }
        return best;
    }

    // Chooses a geometrically compact fundamental domain: the left half / top-left quadrant / one
    // wedge, rather than an arbitrary lexicographic one.
    private long Key(CellPos p)
    {
        long u = p.X - p.Y;        // screen horizontal
        long v = p.X + p.Y;        // screen vertical
        return Kind switch
        {
            SymmetryKind.Rotate180 => u * 4096 + v,
            SymmetryKind.MirrorLeftRight => u * 4096 + v,
            SymmetryKind.MirrorTopBottom => v * 4096 + u,
            SymmetryKind.Quad => (u > 0 ? 1L << 40 : 0) + (v > c ? 1L << 39 : 0) + u * 4096 + v,
            SymmetryKind.Diamond => DiamondKey(p),
            _ => 0,
        };
    }

    private long DiamondKey(CellPos p)
    {
        // Quadrant around the centre in cell space; prefer px > 0, py >= 0.
        double cx = c / 2.0;
        double px = p.X - cx, py = p.Y - cx;
        int quadrant = px > 0 && py >= 0 ? 0 : px <= 0 && py > 0 ? 1 : px < 0 && py <= 0 ? 2 : 3;
        return ((long)quadrant << 40) + p.X * 4096L + p.Y;
    }
}
