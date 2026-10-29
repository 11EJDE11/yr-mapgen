using LightningMap;

namespace MapGen.App;

/// <summary>
/// Renders map previews in-process with the LightningMap library. Its engine indexes the game archives
/// once, so one is kept per game directory and used from one thread at a time.
/// </summary>
internal static class PreviewRenderer
{
    private static readonly object Gate = new();
    private static RenderEngine engine;
    private static string engineDirectory;

    public static bool IsGameDirectory(string dir)
        => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
           && (File.Exists(Path.Combine(dir, "ra2md.mix")) || File.Exists(Path.Combine(dir, "gamemd.exe")));

    /// <summary>
    /// Runs <paramref name="action"/> with the engine for <paramref name="gameDirectory"/>, or with null if
    /// it is not a game directory. Throws if the game files cannot be loaded.
    /// </summary>
    public static T Use<T>(string gameDirectory, Func<RenderEngine, T> action)
    {
        lock (Gate)
        {
            if (!IsGameDirectory(gameDirectory))
                return action(null);
            if (engine == null || !string.Equals(engineDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
            {
                engine?.Dispose();
                engine = null;
                engine = new RenderEngine(gameDirectory);
                engineDirectory = gameDirectory;
            }
            return action(engine);
        }
    }

    /// <summary>Renders <paramref name="mapPath"/> to <paramref name="pngPath"/>. Returns an error message or null.</summary>
    public static Task<string> RenderAsync(string mapPath, string pngPath, string gameDirectory, int width, bool markers)
        => Task.Run(() =>
        {
            if (!IsGameDirectory(gameDirectory))
                return "Set the Yuri's Revenge directory to see previews.";
            try
            {
                Use(gameDirectory, r => r.Render(mapPath, pngPath, new RenderOptions(Width: width, StartMarkers: markers)));
                return null;
            }
            catch (Exception ex)
            {
                return "LightningMap failed: " + ex.Message;
            }
        });
}
