namespace Yaat.GuideCapture.Capture;

// Per-run shared state. ServerUrl points at the in-process yaat-server
// (Phase B); RepoRoot lets scenes locate fixed assets (scenario JSONs,
// airport layouts) by walking up from AppContext.BaseDirectory.
internal sealed class CaptureContext
{
    public required string ServerUrl { get; init; }

    public string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "yaat.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root (yaat.slnx) starting from " + AppContext.BaseDirectory);
    }
}
