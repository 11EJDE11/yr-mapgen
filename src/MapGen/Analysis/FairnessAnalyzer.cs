using System.Text;
using MapGen.Model;
using MapGen.Theater;

namespace MapGen.Analysis;

public sealed class StartReport
{
    public int Waypoint { get; set; }
    public CellPos Position { get; set; }

    /// <summary>Credits of ore/gems reachable within each travel-distance band (see <see cref="FairnessAnalyzer.EconomyBands"/>).</summary>
    public double[] Economy { get; set; }

    /// <summary>Travel distance until 4000 credits of resources are reachable (first refinery's worth).</summary>
    public double DistanceToFirstEconomy { get; set; }

    public double GemsWithin30 { get; set; }
    public Dictionary<string, double> NearestTech { get; set; } = new();
    public double NearestEnemy { get; set; }
    public double NearestAlly { get; set; }
    public int BuildableCells { get; set; }
    public bool ReachesAllStarts { get; set; }
}

public sealed class FairnessReport
{
    public string MapName { get; set; }
    public List<StartReport> Starts { get; set; } = new();
    public List<string> Problems { get; set; } = new();

    /// <summary>Worst relative spread across all compared metrics (0 = identical).</summary>
    public double WorstSpread { get; set; }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fairness report: {MapName}");
        sb.Append("wp  pos       ");
        foreach (int band in FairnessAnalyzer.EconomyBands)
            sb.Append($"eco<={band,-3}  ");
        sb.AppendLine("firstEco gems30 enemy  ally  build  tech");
        foreach (var s in Starts)
        {
            sb.Append($"{s.Waypoint,-3} {s.Position,-9} ");
            foreach (double e in s.Economy)
                sb.Append($"{e,8:0}  ");
            sb.Append($"{Fmt(s.DistanceToFirstEconomy),8} {s.GemsWithin30,6:0} {Fmt(s.NearestEnemy),5} {Fmt(s.NearestAlly),5} {s.BuildableCells,5}  ");
            sb.AppendLine(string.Join(" ", s.NearestTech.OrderBy(k => k.Key).Select(k => $"{k.Key}:{Fmt(k.Value)}")));
        }
        sb.AppendLine($"Worst spread: {WorstSpread:P1}");
        foreach (var p in Problems)
            sb.AppendLine("  ! " + p);
        return sb.ToString();
    }

    private static string Fmt(double d) => double.IsInfinity(d) ? "-" : d.ToString("0");
}

/// <summary>
/// Measures how evenly a map treats its start positions using ground travel distances
/// (wheeled locomotion, like harvesters), not straight-line distances.
/// </summary>
public static class FairnessAnalyzer
{
    public static readonly int[] EconomyBands = { 15, 25, 40, 60 };

    public static readonly Dictionary<string, string> TechKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CAOILD"] = "oil", ["CAOUTP"] = "repair", ["CATHOSP"] = "hospital", ["CAHOSP"] = "hospital",
        ["CAAIRP"] = "airport", ["CAMACH"] = "machineshop", ["CAPOWR"] = "power", ["CASLAB"] = "seclab",
    };

    public static FairnessReport Analyze(MapModel map, TheaterData theater, IReadOnlyList<int[]> teams = null,
        IReadOnlyList<int[]> equivalentStarts = null, bool expectGroundRoutes = true)
    {
        var pass = new Passability(map, theater);
        var report = new FairnessReport { MapName = map.Name };

        var gems = new HashSet<int>(theater.OverlayTypes.Where(o => o.Tiberium && o.Name.StartsWith("GEM", StringComparison.OrdinalIgnoreCase)).Select(o => o.Index));
        var ores = new HashSet<int>(theater.OverlayTypes.Where(o => o.Tiberium).Select(o => o.Index));

        var starts = map.Waypoints.Where(w => w.Key < 8).OrderBy(w => w.Key).ToList();
        var fields = starts.Select(s => Distances(map, pass, s.Value)).ToList();

        for (int i = 0; i < starts.Count; i++)
        {
            var dist = fields[i];
            var r = new StartReport { Waypoint = starts[i].Key, Position = starts[i].Value, Economy = new double[EconomyBands.Length] };

            var resourceByDistance = new List<(double d, double v)>();
            foreach (var p in map.AllValidCells())
            {
                var c = map[p];
                if (c.Overlay < 0 || !ores.Contains(c.Overlay))
                    continue;
                double d = ReachDistance(map, dist, p);
                if (double.IsInfinity(d))
                    continue;
                bool gem = gems.Contains(c.Overlay);
                double value = (c.OverlayData + 1) * (gem ? 50 : 25);
                resourceByDistance.Add((d, value));
                if (gem && d <= 30)
                    r.GemsWithin30 += value;
            }

            for (int b = 0; b < EconomyBands.Length; b++)
                r.Economy[b] = resourceByDistance.Where(t => t.d <= EconomyBands[b]).Sum(t => t.v);

            double acc = 0;
            r.DistanceToFirstEconomy = double.PositiveInfinity;
            foreach (var (d, v) in resourceByDistance.OrderBy(t => t.d))
            {
                acc += v;
                if (acc >= 4000) { r.DistanceToFirstEconomy = d; break; }
            }

            foreach (var s in map.Structures)
            {
                if (!TechKinds.TryGetValue(s.Type, out string kind))
                    continue;
                var b = theater.Building(s.Type);
                var (w, h) = b != null ? b.FoundationSize : (1, 1);
                double best = double.PositiveInfinity;
                for (int dx = -1; dx <= w; dx++)
                    for (int dy = -1; dy <= h; dy++)
                    {
                        var q = new CellPos(s.Position.X + dx, s.Position.Y + dy);
                        if (map.IsValid(q))
                            best = Math.Min(best, dist[q.X, q.Y]);
                    }
                if (!r.NearestTech.TryGetValue(kind, out double cur) || best < cur)
                    r.NearestTech[kind] = best;
            }

            r.NearestEnemy = double.PositiveInfinity;
            r.NearestAlly = double.PositiveInfinity;
            r.ReachesAllStarts = true;
            int[] myTeam = teams?.FirstOrDefault(t => t.Contains(r.Waypoint));
            for (int j = 0; j < starts.Count; j++)
            {
                if (j == i)
                    continue;
                double d = dist[starts[j].Value.X, starts[j].Value.Y];
                if (double.IsInfinity(d))
                    r.ReachesAllStarts = false;
                bool ally = myTeam != null && myTeam.Contains(starts[j].Key);
                if (ally) r.NearestAlly = Math.Min(r.NearestAlly, d);
                else r.NearestEnemy = Math.Min(r.NearestEnemy, d);
            }

            var sp = starts[i].Value;
            for (int dx = -10; dx <= 10; dx++)
                for (int dy = -10; dy <= 10; dy++)
                {
                    var q = new CellPos(sp.X + dx, sp.Y + dy);
                    if (dx * dx + dy * dy <= 100 && map.IsValid(q) && pass.Buildable[q.X, q.Y] && !double.IsInfinity(dist[q.X, q.Y])
                        && map[q].Overlay < 0 && map[q].Level == map[sp].Level)
                        r.BuildableCells++;
                }

            report.Starts.Add(r);
        }

        // Starts that are meant to be equivalent (e.g. all corner players of a 4v4) are compared with
        // each other; without that information every start is compared with every other.
        var groups = equivalentStarts ?? new[] { report.Starts.Select(s => s.Waypoint).ToArray() };
        foreach (var group in groups)
        {
            var members = report.Starts.Where(s => group.Contains(s.Waypoint)).ToList();
            string label = groups.Count > 1 ? $"[{string.Join(",", group)}] " : "";
            Compare(report, members, label, expectGroundRoutes);
        }

        // Whole teams must be equal even when their members are not (front line vs back line).
        if (teams != null && teams.Count > 1)
        {
            var teamTotals = teams.Select((t, i) => new StartReport
            {
                Waypoint = i,
                Economy = EconomyBands.Select((_, b) => report.Starts.Where(s => t.Contains(s.Waypoint)).Sum(s => s.Economy[b])).ToArray(),
                GemsWithin30 = report.Starts.Where(s => t.Contains(s.Waypoint)).Sum(s => s.GemsWithin30),
                BuildableCells = report.Starts.Where(s => t.Contains(s.Waypoint)).Sum(s => s.BuildableCells),
            }).ToList();
            CompareTotals(report, teamTotals);
        }

        if (expectGroundRoutes)
            foreach (var s in report.Starts.Where(s => !s.ReachesAllStarts))
                report.Problems.Add($"start {s.Waypoint} cannot reach every other start by ground");

        return report;
    }

    // floor: differences are measured relative to at least this much, so 0 vs 600 credits is not a "100%" spread.
    private static void Check(FairnessReport report, IReadOnlyList<StartReport> starts, string label,
        Func<StartReport, double> metric, double tolerance, double floor = 1)
    {
        var values = starts.Select(metric).ToList();
        if (values.Any(double.IsInfinity))
        {
            if (!values.All(double.IsInfinity))
                report.Problems.Add($"{label}: unreachable for some starts but not others");
            return;
        }
        double max = values.Max(), min = values.Min();
        if (max <= 0)
            return;
        double spread = (max - min) / Math.Max(max, floor);
        report.WorstSpread = Math.Max(report.WorstSpread, spread);
        if (spread > tolerance)
            report.Problems.Add($"{label}: spread {spread:P0} (min {min:0}, max {max:0})");
    }

    private static void CompareTotals(FairnessReport report, List<StartReport> teams)
    {
        for (int b = 0; b < EconomyBands.Length; b++)
        {
            int band = b;
            Check(report, teams, $"team economy within {EconomyBands[b]}", s => s.Economy[band], 0.1, 10000);
        }
        Check(report, teams, "team gems within 30", s => s.GemsWithin30, 0.1, 10000);
        Check(report, teams, "team buildable area", s => s.BuildableCells, 0.1);
    }

    private static void Compare(FairnessReport report, List<StartReport> starts, string prefix, bool expectGroundRoutes)
    {
        if (starts.Count < 2)
            return;

        void Check(string label, Func<StartReport, double> metric, double tolerance, double floor = 1)
            => FairnessAnalyzer.Check(report, starts, prefix + label, metric, tolerance, floor);

        for (int b = 0; b < EconomyBands.Length; b++)
        {
            int band = b;
            Check($"economy within {EconomyBands[b]}", s => s.Economy[band], 0.15, 5000);
        }
        Check("gems within 30", s => s.GemsWithin30, 0.15, 5000);
        Check("distance to first 4000 credits", s => s.DistanceToFirstEconomy, 0.15, 10);
        Check("buildable area", s => s.BuildableCells, 0.15);
        if (expectGroundRoutes)
            Check("nearest enemy", s => s.NearestEnemy, 0.15);

        var kinds = starts.SelectMany(s => s.NearestTech.Keys).Distinct();
        foreach (string kind in kinds)
            Check($"nearest {kind}", s => s.NearestTech.TryGetValue(kind, out double d) ? d : double.PositiveInfinity, 0.2, 10);
    }

    /// <summary>Distance to the nearest passable neighbour (resources sit on cells harvesters enter).</summary>
    private static double ReachDistance(MapModel map, double[,] dist, CellPos p)
    {
        double best = dist[p.X, p.Y];
        foreach (var o in CellPos.Neighbours8)
        {
            var q = p + o;
            if (map.IsValid(q))
                best = Math.Min(best, dist[q.X, q.Y] + 1);
        }
        return best;
    }

    /// <summary>Dijkstra over the 8-connected cell grid, weighted by 1/wheel speed.</summary>
    public static double[,] Distances(MapModel map, Passability pass, CellPos from)
    {
        int n = map.Span;
        var dist = new double[n, n];
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
                dist[x, y] = double.PositiveInfinity;

        var queue = new PriorityQueue<CellPos, double>();
        dist[from.X, from.Y] = 0;
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out var p, out double d))
        {
            if (d > dist[p.X, p.Y])
                continue;
            for (int k = 0; k < 8; k++)
            {
                var o = CellPos.Neighbours8[k];
                var q = p + o;
                if (!pass.IsPassable(q))
                    continue;
                if (Math.Abs(map[q].Level - map[p].Level) > 2)
                    continue;
                bool diagonal = k >= 4;
                if (diagonal && (!pass.IsPassable(new CellPos(p.X + o.X, p.Y)) || !pass.IsPassable(new CellPos(p.X, p.Y + o.Y))))
                    continue;
                double step = (diagonal ? 1.41421356 : 1.0) / Math.Max(pass.Speed[q.X, q.Y], 0.05);
                double nd = d + step;
                if (nd < dist[q.X, q.Y])
                {
                    dist[q.X, q.Y] = nd;
                    queue.Enqueue(q, nd);
                }
            }
        }
        return dist;
    }
}
