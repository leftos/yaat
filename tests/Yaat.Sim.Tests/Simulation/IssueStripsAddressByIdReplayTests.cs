using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Sanity test for the "always address strips by id" rule's replay-fallback
/// guarantee: legacy bundles that exercised text-keyed / label-keyed strip
/// commands (HSD &lt;text&gt;, SEPD &lt;bay&gt; &lt;label&gt;, STRIPD,
/// STRIPO, AN &lt;box&gt;) must still parse. The id form is the new default
/// emit shape from UI / translator paths, but parser/handler keep accepting
/// the older shorthand so saved recordings (and terminal entry) keep working.
///
/// <para>The fixture is the OAK S2-OAK-4 bundle attached to the bug report
/// where two half-strips with duplicate first-line text caused multi-match
/// errors. Replaying its full action log via the parser asserts every strip
/// command in the recording still produces a non-null parsed result.</para>
/// </summary>
public class IssueStripsAddressByIdReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/strips-id-addressing-recording.yaat-bug-report-bundle.zip";

    private static readonly HashSet<string> StripVerbs =
    [
        "STRIP",
        "STRIPD",
        "STRIPO",
        "STRIPSCAN",
        "SCAN",
        "AN",
        "ANNOTATE",
        "BOX",
        "HSC",
        "HSA",
        "HSD",
        "HSE",
        "HSM",
        "HSO",
        "HSS",
        "HALFSTRIPCREATE",
        "HALFSTRIPAMEND",
        "HALFSTRIPDEL",
        "SEP",
        "SEPD",
        "SEPE",
        "SEPM",
        "BLANK",
        "BLANKD",
    ];

    [Fact]
    public void LegacyBundle_AllStripCommandsStillParse()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            output.WriteLine($"Recording not available at {RecordingPath}, skipping.");
            return;
        }

        var stripCommands = recording.Actions.OfType<RecordedCommand>().Where(c => IsStripCommand(c.Command)).ToList();

        Assert.NotEmpty(stripCommands);
        output.WriteLine($"Bundle contains {stripCommands.Count} strip-related recorded commands.");

        var failures = new List<string>();
        foreach (var cmd in stripCommands)
        {
            var parsed = CommandParser.Parse(cmd.Command);
            if (parsed.Value is null)
            {
                failures.Add($"t={cmd.ElapsedSeconds:F0} '{cmd.Command}' (callsign='{cmd.Callsign}'): {parsed.Reason}");
            }
        }

        if (failures.Count > 0)
        {
            output.WriteLine("Parse failures:");
            foreach (var f in failures.Take(10))
            {
                output.WriteLine("  " + f);
            }
        }
        Assert.Empty(failures);
    }

    private static bool IsStripCommand(string canonical)
    {
        var trimmed = canonical.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        var verb = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        return StripVerbs.Contains(verb.ToUpperInvariant());
    }
}
