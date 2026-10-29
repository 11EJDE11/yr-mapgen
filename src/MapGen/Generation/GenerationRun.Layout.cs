using MapGen.Model;

namespace MapGen.Generation;

internal sealed partial class GenerationRun
{
    private double BaseCoreRadius => IsBlitz ? 6 : 7;
    private double BaseRadius => IsBlitz ? 10 : 13;
    private double MinStartSeparation => IsBlitz ? 17 : 22;

    /// <summary>
    /// A start template: a range in normalised LocalSize coordinates for the side on the left
    /// (transposed when the sides are top and bottom), optionally pinned to the mirror axis between
    /// that side's two halves.
    /// </summary>
    private readonly record struct StartTemplate(double MinX, double MaxX, double MinY, double MaxY, bool OnAxis = false);

    private void PlaceStarts()
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            canonicalStarts.Clear();
            bool ok = sym.Kind switch
            {
                SymmetryKind.Diamond => PickDiamondStarts(),
                _ => PickTemplateStarts(),
            };
            if (ok && BuildPlayers())
                return;
        }
        throw new GenerationFailedException("Could not place start locations");
    }

    private IEnumerable<StartTemplate> Templates()
    {
        if (sym.Kind == SymmetryKind.Quad)
        {
            // One quarter of the map; the other three are mirror images.
            return settings.Format switch
            {
                MatchFormat.TwoVsTwo => new[] { new StartTemplate(0.08, 0.24, 0.16, 0.36) },
                MatchFormat.ThreeVsThree => new[]
                {
                    new StartTemplate(0.09, 0.20, 0.09, 0.20),
                    new StartTemplate(0.10, 0.24, 0.5, 0.5, OnAxis: true),
                },
                MatchFormat.FourVsFour => new[]
                {
                    new StartTemplate(0.07, 0.15, 0.09, 0.19),
                    new StartTemplate(0.22, 0.33, 0.30, 0.41),
                },
                // Teammates close together in one corner of the map.
                MatchFormat.FourTeamsOfTwo => new[]
                {
                    new StartTemplate(0.09, 0.17, 0.24, 0.36),
                    new StartTemplate(0.20, 0.30, 0.09, 0.17),
                },
                _ => throw new GenerationFailedException("Unsupported format for quad symmetry"),
            };
        }

        // Two-fold symmetries: the whole of one side; the other side is its rotated/mirrored image.
        return settings.Format switch
        {
            MatchFormat.Duel => new[] { new StartTemplate(0.10, 0.36, 0.15, 0.85) },
            MatchFormat.TwoVsTwo => new[]
            {
                new StartTemplate(0.08, 0.20, 0.15, 0.32),
                new StartTemplate(0.08, 0.20, 0.68, 0.85),
            },
            MatchFormat.ThreeVsThree => new[]
            {
                new StartTemplate(0.08, 0.18, 0.08, 0.20),
                new StartTemplate(0.12, 0.26, 0.42, 0.58),
                new StartTemplate(0.08, 0.18, 0.80, 0.92),
            },
            MatchFormat.FourVsFour => new[]
            {
                new StartTemplate(0.06, 0.14, 0.08, 0.18),
                new StartTemplate(0.06, 0.14, 0.82, 0.92),
                new StartTemplate(0.22, 0.32, 0.27, 0.40),
                new StartTemplate(0.22, 0.32, 0.60, 0.73),
            },
            _ => throw new GenerationFailedException("Unsupported format for two-fold symmetry"),
        };
    }

    private bool PickTemplateStarts()
    {
        foreach (var t in Templates())
        {
            double a = t.MinX + rng.NextDouble() * (t.MaxX - t.MinX);
            double b = t.MinY + rng.NextDouble() * (t.MaxY - t.MinY);
            // Islands need sea all round them, so pull the starts away from the map edge.
            if (IsIslandLayout)
            {
                a = 0.5 + (a - 0.5) * 0.72;
                b = t.OnAxis ? b : 0.5 + (b - 0.5) * 0.72;
            }
            var p = splitTopBottom ? map.FromNormalisedLocal(b, a) : map.FromNormalisedLocal(a, b);
            if (t.OnAxis)
                p = splitTopBottom ? SnapToVerticalAxis(p) : SnapToHorizontalAxis(p);
            if (!IsGoodStart(p))
                return false;
            canonicalStarts.Add(p);
        }
        return true;
    }

    /// <summary>Cells with x = y are fixed by the left-right mirror.</summary>
    private static CellPos SnapToVerticalAxis(CellPos p)
    {
        int v = p.X + p.Y;
        return new CellPos(v / 2, v - v / 2 - (v % 2));
    }

    /// <summary>Cells with x + y = W + H are fixed by the top-bottom mirror.</summary>
    private CellPos SnapToHorizontalAxis(CellPos p)
    {
        int c = map.Width + map.Height;
        int u = p.X - p.Y;
        if ((u + c) % 2 != 0)
            u++;
        return new CellPos((c + u) / 2, (c - u) / 2);
    }

    private bool PickDiamondStarts()
    {
        // Two teammates inside one 90° wedge around the centre (cell space).
        int c = map.Width + map.Height;
        double cx = c / 2.0;
        double maxR = map.AllValidCells().Where(IsUsable).Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cx) * (p.Y - cx)));
        double[] angles = { 18 + rng.NextDouble() * 14, 58 + rng.NextDouble() * 14 };
        foreach (double a in angles)
        {
            double r = maxR * (0.52 + rng.NextDouble() * 0.12);
            var p = new CellPos((int)Math.Round(cx + r * Math.Cos(a * Math.PI / 180)), (int)Math.Round(cx + r * Math.Sin(a * Math.PI / 180)));
            if (!IsGoodStart(p))
                return false;
            canonicalStarts.Add(p);
        }
        return true;
    }

    private bool IsGoodStart(CellPos p) => Disk(p, BaseRadius - 2).All(IsUsable);

    private bool BuildPlayers()
    {
        players.Clear();
        for (int k = 0; k < canonicalStarts.Count; k++)
        {
            var seen = new HashSet<CellPos>();
            for (int t = 0; t < sym.Order; t++)
            {
                var image = sym.Apply(t, canonicalStarts[k]);
                if (!seen.Add(image))
                    continue;
                players.Add(new PlayerSlot { Position = image, Canonical = k, Transform = t });
            }
        }

        if (players.Count != settings.PlayerCount)
            return false;

        for (int i = 0; i < players.Count; i++)
            for (int j = i + 1; j < players.Count; j++)
                if (players[i].Position.DistanceTo(players[j].Position) < MinStartSeparation)
                    return false;

        AssignTeamsAndWaypoints();
        return true;
    }

    private void AssignTeamsAndWaypoints()
    {
        int c = map.Width + map.Height;
        int U(PlayerSlot p) => p.Position.X - p.Position.Y;
        int V(PlayerSlot p) => p.Position.X + p.Position.Y;

        foreach (var p in players)
        {
            p.Team = (settings.Format, sym.Kind) switch
            {
                (MatchFormat.Duel, _) => p.Transform,
                (MatchFormat.FourTeamsOfTwo, SymmetryKind.Diamond) => p.Transform,
                (MatchFormat.FourTeamsOfTwo, _) => (U(p) > 0 ? 1 : 0) + (V(p) > c ? 2 : 0),
                // Two-fold symmetry: the canonical side is one team, its image the other.
                (_, not SymmetryKind.Quad) => p.Transform == 0 ? 0 : 1,
                _ => splitTopBottom ? (V(p) > c ? 1 : 0) : (U(p) > 0 ? 1 : 0),
            };
        }

        // Waypoints team by team, then along the side.
        int wp = 0;
        foreach (var p in players.OrderBy(p => p.Team).ThenBy(p => splitTopBottom ? U(p) : V(p)).ThenBy(p => splitTopBottom ? V(p) : U(p)))
            p.Waypoint = wp++;
    }

    private void ReserveBases()
    {
        foreach (var pl in players)
        {
            foreach (var q in Disk(pl.Position, BaseRadius))
                if (IsUsable(q) && zone[q.X, q.Y] == Zone.Free)
                    zone[q.X, q.Y] = Zone.Base;
            foreach (var q in Disk(pl.Position, BaseCoreRadius))
                if (map.IsValid(q))
                    zone[q.X, q.Y] = Zone.BaseCore;
        }

        // On island layouts lanes over the sea do nothing (the coast is mirrored); on the islands they
        // keep free cliffs and trees off the routes.
        if (terrain.Kind == SymmetryKind.None)
            ReserveLanes();
    }

    // Free terrain only: corridors that lakes, closed cliffs and unmirrored trees must leave open.
    private bool[,] lanes;

    private bool OnLane(CellPos p) => lanes != null && map.IsValid(p) && lanes[p.X, p.Y];

    /// <summary>
    /// With free terrain, a lake or cliff could lengthen one player's routes and not their mirror
    /// image's. The ground round every base plus straight lanes from it to the centre, its teammates
    /// and its two nearest enemies form a symmetric set (the bases are symmetric), so keeping them
    /// open keeps each player's own fields and the main routes equal.
    /// </summary>
    private void ReserveLanes()
    {
        lanes = new bool[map.Span, map.Span];
        foreach (var pl in players)
        {
            // The ground round each base, where its own fields are, stays clear too.
            foreach (var q in Disk(pl.Position, BaseRadius + 10))
                if (map.IsValid(q))
                    lanes[q.X, q.Y] = true;
            Lane(pl.Position, MapCentre());
            foreach (var mate in players.Where(o => o != pl && o.Team == pl.Team))
                Lane(pl.Position, mate.Position);
            foreach (var enemy in players.Where(o => o.Team != pl.Team).OrderBy(o => o.Position.DistanceTo(pl.Position)).Take(2))
                Lane(pl.Position, enemy.Position);
        }

        void Lane(CellPos from, CellPos to)
        {
            double length = from.DistanceTo(to);
            for (double d = 0; d <= length; d += 0.5)
            {
                var c = new CellPos((int)Math.Round(from.X + (to.X - from.X) * d / length), (int)Math.Round(from.Y + (to.Y - from.Y) * d / length));
                foreach (var q in Disk(c, 2))
                    if (map.IsValid(q))
                        lanes[q.X, q.Y] = true;
            }
        }
    }

    /// <summary>Direction (unit vector, cell space) pointing away from the nearest enemies.</summary>
    private (double X, double Y) AwayFromEnemies(int canonical)
    {
        var me = players.First(p => p.Canonical == canonical && p.Transform == 0);
        var enemies = players.Where(p => p.Team != me.Team).ToList();
        double ex = enemies.Average(p => p.Position.X), ey = enemies.Average(p => p.Position.Y);
        double dx = me.Position.X - ex, dy = me.Position.Y - ey;
        double len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        return (dx / len, dy / len);
    }
}
