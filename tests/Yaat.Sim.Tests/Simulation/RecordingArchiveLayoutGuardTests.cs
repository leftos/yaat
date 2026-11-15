using System.IO.Compression;
using System.Text.RegularExpressions;
using Xunit;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Every test source that opens a recording archive carrying its own airport layout (a <c>layouts/</c> entry) builds its
/// engine's ground data with <see cref="RecordingGroundData"/>, stating whether it pins behaviour on today's airport
/// (<see cref="RecordingGroundData.Evergreen"/>) or on the airport as recorded (<see cref="RecordingGroundData.VersionPinned"/>).
///
/// The scan finds archives named by a <c>"TestData/*.zip"</c> string literal outside <c>//</c> comment lines; a path
/// assembled at run time is not seen. A read-only <c>new TestAirportGroundData().GetLayout(…)</c> is allowed.
/// </summary>
public partial class RecordingArchiveLayoutGuardTests
{
    private const string EvergreenCall = "RecordingGroundData.Evergreen(";
    private const string VersionPinnedCall = "RecordingGroundData.VersionPinned(";
    private const string ReadOnlyLiveLayout = "new TestAirportGroundData().GetLayout(";

    [Fact]
    public void LayoutCarryingArchives_AreRestoredThroughRecordingGroundData()
    {
        string testsRoot = Path.Combine(RepositoryRoot.Find(), "tests", "Yaat.Sim.Tests");
        string testDataDir = Path.Combine(testsRoot, "TestData");
        Dictionary<string, bool> carriesLayout = new(StringComparer.OrdinalIgnoreCase);
        List<string> offenders = [];
        int checkedFiles = 0;

        foreach (string file in SourceFiles(testsRoot))
        {
            string code = WithoutCommentLines(File.ReadAllText(file));
            string[] layoutArchives =
            [
                .. ArchiveLiteral()
                    .Matches(code)
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => CarriesLayout(testDataDir, name, carriesLayout)),
            ];
            if (layoutArchives.Length == 0)
            {
                continue;
            }

            checkedFiles++;
            string? problem = Problem(Path.GetFileNameWithoutExtension(file), code);
            if (problem is not null)
            {
                offenders.Add($"{Path.GetRelativePath(testsRoot, file)} ({string.Join(", ", layoutArchives)}): {problem}");
            }
        }

        Assert.True(checkedFiles > 0, $"No test source under {testsRoot} names a layout-carrying archive; the scan found nothing to guard.");
        Assert.True(
            offenders.Count == 0,
            "These test files open an archive that bundles its airport layout but do not build their ground data with "
                + $"{nameof(RecordingGroundData)}.{nameof(RecordingGroundData.Evergreen)} or "
                + $"{nameof(RecordingGroundData)}.{nameof(RecordingGroundData.VersionPinned)}:\n"
                + string.Join("\n", offenders)
        );
    }

    private static string? Problem(string className, string code)
    {
        // The helper's own tests compare its layouts with the archive's, so they read the archive directly by design.
        if ((className == nameof(RecordingGroundDataTests)) || !BuildsGroundData(code))
        {
            return null;
        }

        if (code.Contains(".ReadLayout(", StringComparison.Ordinal) || code.Contains(".ReadAllLayouts(", StringComparison.Ordinal))
        {
            return $"reads an archive layout directly instead of through {VersionPinnedCall}…)";
        }

        if (BareTestAirportGroundData().IsMatch(code.Replace(ReadOnlyLiveLayout, string.Empty, StringComparison.Ordinal)))
        {
            return $"builds its ground data on a bare TestAirportGroundData instead of {EvergreenCall}…)";
        }

        bool statesIntent = code.Contains(EvergreenCall, StringComparison.Ordinal) || code.Contains(VersionPinnedCall, StringComparison.Ordinal);
        return statesIntent ? null : $"never calls {EvergreenCall}…) or {VersionPinnedCall}…)";
    }

    /// <summary>A file that only reads snapshots or actions out of an archive builds no ground data and has nothing to guard.</summary>
    private static bool BuildsGroundData(string code) =>
        code.Contains("new SimulationEngine(", StringComparison.Ordinal)
        || code.Contains("GroundData", StringComparison.Ordinal)
        || code.Contains("ReadLayout", StringComparison.Ordinal)
        || code.Contains("GetLayout(", StringComparison.Ordinal);

    private static string WithoutCommentLines(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static IEnumerable<string> SourceFiles(string testsRoot) =>
        Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(Path.GetRelativePath(testsRoot, f)))
            .Order(StringComparer.Ordinal);

    private static bool IsBuildOutput(string relativePath)
    {
        string first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return (first == "bin") || (first == "obj");
    }

    private static bool CarriesLayout(string testDataDir, string archiveName, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(archiveName, out bool known))
        {
            return known;
        }

        string path = Path.Combine(testDataDir, archiveName);
        bool carries = false;
        if (File.Exists(path))
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            carries = zip.Entries.Any(e => e.FullName.StartsWith("layouts/", StringComparison.Ordinal));
        }

        cache[archiveName] = carries;
        return carries;
    }

    [GeneratedRegex("\"TestData/([^\"]+?\\.zip)\"")]
    private static partial Regex ArchiveLiteral();

    [GeneratedRegex(@"new\s+TestAirportGroundData\s*\(|TestAirportGroundData\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex BareTestAirportGroundData();
}
