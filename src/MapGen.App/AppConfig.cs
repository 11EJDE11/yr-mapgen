using System.Text.Json;
using MapGen.Generation;

namespace MapGen.App;

/// <summary>Remembered between runs: game directory and the last settings used.</summary>
internal sealed class AppConfig
{
    public string GameDirectory { get; set; }
    public bool RandomSeed { get; set; } = true;
    public bool DefaultTech { get; set; } = true;
    public GeneratorSettings Settings { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yr-mapgen", "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch (Exception)
        {
            // A corrupt or outdated settings file just means starting from defaults.
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Not being able to remember settings is not worth interrupting the user for.
        }
    }
}
