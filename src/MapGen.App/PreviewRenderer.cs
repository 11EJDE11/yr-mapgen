using System.Diagnostics;

namespace MapGen.App;

/// <summary>Renders map previews by running LightningMap (shipped next to the exe).</summary>
internal static class PreviewRenderer
{
    public static string ExePath
    {
        get
        {
            string local = Path.Combine(AppContext.BaseDirectory, "LightningMap.exe");
            return File.Exists(local) ? local : null;
        }
    }

    public static bool IsGameDirectory(string dir)
        => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
           && (File.Exists(Path.Combine(dir, "ra2md.mix")) || File.Exists(Path.Combine(dir, "gamemd.exe")));

    /// <summary>Renders <paramref name="mapPath"/> to <paramref name="pngPath"/>. Returns an error message or null.</summary>
    public static async Task<string> RenderAsync(string mapPath, string pngPath, string gameDirectory, int width, bool markers)
    {
        if (ExePath == null)
            return "LightningMap.exe was not found next to the generator, so there is no preview.";
        if (!IsGameDirectory(gameDirectory))
            return "Set the Yuri's Revenge directory to see previews.";

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(mapPath);
        psi.ArgumentList.Add("--game");
        psi.ArgumentList.Add(gameDirectory);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(pngPath);
        if (width > 0)
        {
            psi.ArgumentList.Add("--width");
            psi.ArgumentList.Add(width.ToString());
        }
        if (!markers)
        {
            psi.ArgumentList.Add("--no-markers");
        }
        else
        {
            // Half-size markers (LightningMap 1.2+) so they do not hide the bases.
            psi.ArgumentList.Add("--marker-scale");
            psi.ArgumentList.Add("0.5");
        }

        using var process = Process.Start(psi);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(pngPath))
            return $"LightningMap failed (exit {process.ExitCode}): {(await stderr).Trim()} {(await stdout).Trim()}".Trim();
        return null;
    }
}
