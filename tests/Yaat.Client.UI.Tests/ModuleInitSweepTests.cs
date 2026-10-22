using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Yaat.Client.UI.Tests;

// ModuleInit's startup sweep must never reap a sibling test process's folder before that process has written its pid
// marker: a run creates the folder a moment before the marker, and dotnet test starts the assemblies concurrently.
public class ModuleInitSweepTests : IDisposable
{
    private const string PidMarker = ".yaat-pid";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-sweep-tests", Guid.NewGuid().ToString("N"));

    public ModuleInitSweepTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FreshFolderWithoutMarker_IsKept()
    {
        string subdir = NewSubfolder();

        ModuleInit.SweepStaleDirs(_root);

        Assert.True(Directory.Exists(subdir), $"a folder with no marker yet is not provably stale, but the sweep reaped {subdir}");
    }

    [Fact]
    public void FreshFolderWithEmptyMarker_IsKept()
    {
        string subdir = NewSubfolder();
        File.WriteAllText(Path.Combine(subdir, PidMarker), string.Empty);

        ModuleInit.SweepStaleDirs(_root);

        Assert.True(Directory.Exists(subdir), $"a marker written but not yet filled in is not provably stale, but the sweep reaped {subdir}");
    }

    [Fact]
    public void OldFolderWithoutMarker_IsReaped()
    {
        string subdir = NewSubfolder();
        Directory.SetLastWriteTimeUtc(subdir, DateTime.UtcNow.AddHours(-2));

        ModuleInit.SweepStaleDirs(_root);

        Assert.False(Directory.Exists(subdir), $"a folder with no marker for two hours is stale, but the sweep kept {subdir}");
    }

    [Fact]
    public void FolderOfExitedProcess_IsReaped()
    {
        string subdir = NewSubfolder();
        using Process process =
            Process.Start("dotnet", "--version")
            ?? throw new InvalidOperationException("the sweep test needs a child process, but `dotnet --version` could not be started");
        process.WaitForExit();
        File.WriteAllText(Path.Combine(subdir, PidMarker), process.Id.ToString(CultureInfo.InvariantCulture));

        ModuleInit.SweepStaleDirs(_root);

        Assert.False(Directory.Exists(subdir), $"an exited process left {subdir} behind, but the sweep kept it");
    }

    [Fact]
    public void FolderOfLiveProcess_IsKept()
    {
        string subdir = NewSubfolder();
        File.WriteAllText(Path.Combine(subdir, PidMarker), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        ModuleInit.SweepStaleDirs(_root);

        Assert.True(Directory.Exists(subdir), $"a live process owns {subdir}, but the sweep reaped it");
    }

    private string NewSubfolder()
    {
        string subdir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subdir);
        return subdir;
    }
}
