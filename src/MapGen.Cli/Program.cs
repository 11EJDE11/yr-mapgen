using System.Text.Json;
using MapGen.Analysis;
using MapGen.Formats;
using MapGen.Generation;
using MapGen.Terrain;
using MapGen.Theater;

namespace MapGen.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string dataDir = FindDataDirectory();
        switch (args[0].ToLowerInvariant())
        {
            case "analyze":
            case "analyse":
                foreach (string path in args.Skip(1))
                {
                    var map = MapReader.Read(path);
                    var theater = TheaterData.Load(dataDir, map.Theater);
                    Console.WriteLine(FairnessAnalyzer.Analyze(map, theater).ToText());
                }
                return 0;

            case "generate":
                return Generate(args.Skip(1).ToArray(), dataDir);

            default:
                PrintUsage();
                return 1;
        }
    }

    private static int Generate(string[] args, string dataDir)
    {
        var settings = new GeneratorSettings();
        string output = null;
        int count = 1;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i].TrimStart('-').ToLowerInvariant();
            string value = i + 1 < args.Length ? args[i + 1] : null;
            switch (key)
            {
                case "config":
                    settings = JsonSerializer.Deserialize<GeneratorSettings>(File.ReadAllText(value),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                    break;
                case "format": settings.Format = ParseFormat(value); break;
                case "style": settings.Style = Enum.Parse<MapStyle>(value, true); break;
                case "size": settings.Size = Enum.Parse<MapSize>(value, true); break;
                case "shape": settings.Shape = Enum.Parse<MapShape>(value, true); break;
                case "layout": settings.Layout = Enum.Parse<MapLayout>(value, true); break;
                case "theater": settings.Theater = value.ToUpperInvariant(); break;
                case "seed": settings.Seed = int.Parse(value); break;
                case "resources": settings.Resources = Enum.Parse<ResourceLevel>(value, true); break;
                case "gems": settings.Gems = Enum.Parse<GemPlacement>(value, true); break;
                case "economy": settings.Economy = Enum.Parse<EconomyMode>(value, true); break;
                case "hills": settings.Hills = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "cliffs": settings.Cliffs = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "water": settings.Water = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "symmetry": settings.Symmetry = Enum.Parse<SymmetryKind>(value, true); break;
                case "orefields": settings.OreFieldSize = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "gemfields": settings.GemFieldSize = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "drills": settings.Drills = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "trees": settings.Trees = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "time": settings.Time = Enum.Parse<TimeOfDay>(value, true); break;
                case "name": settings.Name = value; break;
                case "attempts": settings.MaxAttempts = int.Parse(value); break;
                case "count": count = int.Parse(value); break;
                case "out": output = value; break;
                default:
                    Console.Error.WriteLine($"Unknown option --{key}");
                    return 1;
            }
            i++;
        }

        var theater = TheaterData.Load(dataDir, settings.Theater);
        var connected = ConnectedTileType.Load(Path.Combine(dataDir, "ConnectedTileDrawer.ini"), theater);
        var generator = new MapGenerator(theater, connected);
        int baseSeed = settings.Seed;

        for (int n = 0; n < count; n++)
        {
            settings.Seed = baseSeed + n * 104729;
            var result = generator.Generate(settings);

            string path = output ?? $"generated_{settings.FormatLabel}_{result.Seed}.map";
            if (count > 1 || Directory.Exists(path))
            {
                string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
                path = Path.Combine(dir, $"gen_{settings.FormatLabel}_{settings.Theater.ToLowerInvariant()}_{result.Seed}.map");
            }

            MapWriter.Write(result.Map, path);
            Console.WriteLine($"Wrote {path} (seed {result.Seed}, {result.Attempts} attempt(s), {result.Map.Width}x{result.Map.Height})");
            Console.WriteLine(result.Report.ToText());
        }
        return 0;
    }

    private static MatchFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "1v1" or "duel" => MatchFormat.Duel,
        "2v2" => MatchFormat.TwoVsTwo,
        "3v3" => MatchFormat.ThreeVsThree,
        "4v4" => MatchFormat.FourVsFour,
        "2v2v2v2" => MatchFormat.FourTeamsOfTwo,
        _ => Enum.Parse<MatchFormat>(value, true),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("yr-mapgen analyze <map>...");
        Console.WriteLine("yr-mapgen generate [--config settings.json] [--format 1v1|2v2|3v3|4v4|2v2v2v2] [--style standard|blitz]");
        Console.WriteLine("                   [--size small|medium|large] [--theater temperate|snow|urban|newurban|desert|lunar]");
        Console.WriteLine("                   [--resources low|medium|high|veryhigh] [--gems none|perplayer|contested|both]");
        Console.WriteLine("                   [--economy ore|oilonly|mixed] [--terrain flat|cliffs] [--symmetry rotate180|mirrorleftright|mirrortopbottom|quad|diamond|none]");
        Console.WriteLine("                   [--trees 0..1] [--time day|dusk|night] [--seed N] [--count N] [--out path]");
    }

    private static string FindDataDirectory()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "data");
            if (File.Exists(Path.Combine(candidate, "temperate.json")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not find the data directory (run tools/TileDump first).");
    }
}
