using MapGen.Model;

namespace MapGen.Generation;

/// <summary>
/// Asymmetry: the map keeps its symmetry group for deciding what every player gets, but each copy of
/// a feature is pushed off its mirror position by a smooth drift field, and its shape is grown
/// afresh. Fairness then comes from measuring the result rather than from exact copies.
/// </summary>
internal sealed partial class GenerationRun
{
    /// <summary>0 = exact copies (the classic generator); 1 = copies drift up to ~9 cells and nothing in the scenery is mirrored.</summary>
    private double Asym => Math.Clamp(settings.Asymmetry, 0, 1);

    private bool Asymmetric => Asym > 0;

    private int driftSeedX, driftSeedY, turnSeed;

    /// <summary>How far a player's own features turn about their start at full asymmetry.</summary>
    private const double MaxTurn = 50 * Math.PI / 180;

    /// <summary>How much nearer or further a player's own features may sit at full asymmetry (the fairness check catches the rest).</summary>
    private const double MaxStretch = 0.15;

    /// <summary>Shared features (contested fields, team and contested tech) drift this much less: they must stay about equidistant.</summary>
    private const double SharedDriftScale = 0.5;

    /// <summary>Largest drift of a feature copy, in cells, at full asymmetry.</summary>
    private const double MaxDrift = 9;

    /// <summary>Wavelength of the drift field: features closer than this move together, further apart they diverge.</summary>
    private const double DriftScale = 24;

    /// <summary>
    /// How far the copy of a feature at <paramref name="q"/> is pushed. One smooth field over the whole
    /// map, so coincident copies (a feature on a mirror axis) stay coincident and neighbours move alike.
    /// </summary>
    private (int X, int Y) Drift(CellPos q, double scale = 1)
    {
        if (!Asymmetric)
            return (0, 0);
        double a = Asym * MaxDrift * scale;
        double dx = (ValueNoise(q.X / DriftScale, q.Y / DriftScale, driftSeedX) * 2 - 1) * a;
        double dy = (ValueNoise(q.X / DriftScale, q.Y / DriftScale, driftSeedY) * 2 - 1) * a;
        return ((int)Math.Round(dx), (int)Math.Round(dy));
    }

    private CellPos Warp(CellPos q)
    {
        var (dx, dy) = Drift(q);
        return new CellPos(q.X + dx, q.Y + dy);
    }

    /// <summary>Where copy <paramref name="t"/> of the canonical point <paramref name="p"/> lands (starts, plateaus).</summary>
    private CellPos Image(int t, CellPos p) => Warp(sym.Apply(t, p));

    /// <summary>Where copy <paramref name="t"/> of a shared feature at <paramref name="p"/> lands.</summary>
    private CellPos SharedImage(int t, CellPos p)
    {
        var q = sym.Apply(t, p);
        var (dx, dy) = Drift(q, SharedDriftScale);
        return new CellPos(q.X + dx, q.Y + dy);
    }

    /// <summary>
    /// Where copy <paramref name="t"/> of a point belonging to player <paramref name="k"/> lands: it
    /// moves with that player's drifted start, turned about it by an angle that depends on where the
    /// start is, plus a touch of jitter. Distances to the owner's start are kept, so every player's
    /// own economy stays equal while the bases are laid out differently.
    /// </summary>
    private CellPos OwnedImage(int k, int t, CellPos p)
    {
        if (!Asymmetric)
            return sym.Apply(t, p);
        var s = sym.Apply(t, canonicalStarts[k]);
        var pos = Warp(s);
        var q = sym.Apply(t, p);
        double theta = (ValueNoise(s.X / DriftScale, s.Y / DriftScale, turnSeed) * 2 - 1) * MaxTurn * Asym;
        double stretch = 1 + (ValueNoise(s.X / DriftScale, s.Y / DriftScale, turnSeed + 1) * 2 - 1) * MaxStretch * Asym;
        double ox = (q.X - s.X) * stretch, oy = (q.Y - s.Y) * stretch;
        double rx = ox * Math.Cos(theta) - oy * Math.Sin(theta);
        double ry = ox * Math.Sin(theta) + oy * Math.Cos(theta);
        var (jx, jy) = Drift(q, 0.25);
        return new CellPos(pos.X + (int)Math.Round(rx) + jx, pos.Y + (int)Math.Round(ry) + jy);
    }

    /// <summary>The actual start position of copy <paramref name="t"/> of canonical player <paramref name="k"/>.</summary>
    private CellPos PlayerPosition(int k, int t) => Image(t, canonicalStarts[k]);

    /// <summary>The player slot nearest to a point among the copies of canonical player <paramref name="k"/>.</summary>
    private PlayerSlot OwnerSlot(int k, CellPos near)
        => players.Where(p => p.Canonical == k).OrderBy(p => p.Position.DistanceTo(near)).First();
}
