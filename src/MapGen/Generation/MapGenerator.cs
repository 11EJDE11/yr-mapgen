using MapGen.Analysis;
using MapGen.Model;
using MapGen.Terrain;
using MapGen.Theater;

namespace MapGen.Generation;

public sealed class GenerationResult
{
    public MapModel Map { get; init; }
    public FairnessReport Report { get; init; }
    public GeneratorSettings Settings { get; init; }
    public int Seed { get; init; }
    public int Attempts { get; init; }
    public List<int[]> Teams { get; init; }
    public List<int[]> EquivalentStarts { get; init; }
}

/// <summary>
/// Generates symmetric, fairness-checked multiplayer maps. Every random decision is made once for a
/// canonical player (or a canonical cell) and copied to the other players through the symmetry group,
/// then the result is measured with <see cref="FairnessAnalyzer"/>; unfair attempts are re-rolled.
/// </summary>
public sealed class MapGenerator
{
    private readonly TheaterData theater;
    private readonly IReadOnlyList<ConnectedTileType> connectedTypes;

    /// <param name="connectedTypes">Cliff/shore definitions for the theater (WAE ConnectedTileDrawer.ini); needed for cliffs.</param>
    public MapGenerator(TheaterData theater, IReadOnlyList<ConnectedTileType> connectedTypes = null)
    {
        this.theater = theater;
        this.connectedTypes = connectedTypes;
    }

    public GenerationResult Generate(GeneratorSettings settings)
    {
        GenerationResult best = null;
        double bestScore = double.MaxValue;
        var failures = new List<string>();

        // The next attempt runs ahead in parallel with the current one. Attempts are judged in attempt order
        // and the ones after the winner are cancelled, so the result for a seed is the same as running them
        // one after another. Measured: two at a time saves ~30% on seeds that need retries for ~4% on seeds
        // that pass first time; more at once only slows every attempt down.
        const int Window = 2;
        int attempts = Math.Max(1, settings.MaxAttempts);
        var running = new Task<(GenerationResult Result, string Failure)>[attempts];
        int started = 0;
        // Not disposed: attempts that are still running check its token after Generate returns.
        var stop = new CancellationTokenSource();
        var cancel = stop.Token;
        try
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int last = Math.Min(attempts - 1, attempt + Window - 1);
                for (; started <= last; started++)
                {
                    int seed = settings.Seed + started * 7919;
                    running[started] = Task.Run(() => RunAttempt(settings, seed, cancel));
                }

                var (result, failure) = running[attempt].GetAwaiter().GetResult();
                if (result == null)
                {
                    failures.Add(failure);
                    continue;
                }

                double score = result.Report.WorstSpread + result.Report.Problems.Count;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new GenerationResult
                    {
                        Map = result.Map,
                        Report = result.Report,
                        Settings = settings,
                        Seed = result.Seed,
                        Attempts = attempt + 1,
                        Teams = result.Teams,
                        EquivalentStarts = result.EquivalentStarts,
                    };
                }

                if (result.Report.Problems.Count == 0)
                    break;
            }
        }
        finally
        {
            // Attempts still running stop at their next step.
            stop.Cancel();
        }

        return best ?? throw new InvalidOperationException("Every generation attempt failed: "
            + string.Join("; ", failures.GroupBy(f => f).Select(g => $"{g.Key} (x{g.Count()})")));
    }

    private (GenerationResult, string) RunAttempt(GeneratorSettings settings, int seed, CancellationToken cancel)
    {
        try
        {
            return (new GenerationRun(theater, connectedTypes, settings, seed).Run(cancel), null);
        }
        catch (GenerationFailedException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
    }
}

public sealed class GenerationFailedException : Exception
{
    public GenerationFailedException(string message) : base(message) { }
}
