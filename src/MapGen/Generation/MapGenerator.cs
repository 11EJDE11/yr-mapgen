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

        int attempts = Math.Max(1, settings.MaxAttempts);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int seed = settings.Seed + attempt * 7919;
            GenerationResult result;
            try
            {
                result = new GenerationRun(theater, connectedTypes, settings, seed).Run();
            }
            catch (GenerationFailedException ex)
            {
                failures.Add(ex.Message);
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
                    Seed = seed,
                    Attempts = attempt + 1,
                    Teams = result.Teams,
                    EquivalentStarts = result.EquivalentStarts,
                };
            }

            if (result.Report.Problems.Count == 0)
                break;
        }

        return best ?? throw new InvalidOperationException("Every generation attempt failed: "
            + string.Join("; ", failures.GroupBy(f => f).Select(g => $"{g.Key} (x{g.Count()})")));
    }
}

public sealed class GenerationFailedException : Exception
{
    public GenerationFailedException(string message) : base(message) { }
}
