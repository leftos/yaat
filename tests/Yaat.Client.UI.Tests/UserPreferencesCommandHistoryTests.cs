using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a
// per-process temp directory. Each test instance rewrites preferences.json from
// scratch through SetCommandHistory; isolation is guaranteed by xUnit creating a
// fresh fixture per test method, but tests still scope their data with unique
// scenario ids so they would not collide even if reordered into one process.
public class UserPreferencesCommandHistoryTests
{
    [Fact]
    public void GetCommandHistory_UnknownScenario_ReturnsEmpty()
    {
        var prefs = new UserPreferences();

        var history = prefs.GetCommandHistory("ZZZ-no-such-scenario");

        Assert.Empty(history);
    }

    [Fact]
    public void SetCommandHistory_RoundTripsCallsignAndCommandThroughDisk()
    {
        const string scenarioId = "TEST-roundtrip-ABC";
        var entries = new[]
        {
            new CommandHistoryEntry("UAL1", "fh 270"),
            new CommandHistoryEntry("AAL2", "DH 5000"),
            new CommandHistoryEntry("", "PAUSE"),
        };

        var writer = new UserPreferences();
        writer.SetCommandHistory(scenarioId, entries);

        // A fresh instance reads preferences.json from disk, proving persistence — including
        // the per-entry callsign, uppercased to match the in-memory normalization.
        var reader = new UserPreferences();
        var loaded = reader.GetCommandHistory(scenarioId);

        Assert.Equal(
            [new CommandHistoryEntry("UAL1", "FH 270"), new CommandHistoryEntry("AAL2", "DH 5000"), new CommandHistoryEntry("", "PAUSE")],
            loaded
        );
    }

    [Fact]
    public void SetCommandHistory_DedupesByCallsignAndCommand()
    {
        const string scenarioId = "TEST-normalize-case-GHI";

        var prefs = new UserPreferences();
        // Same command text on different aircraft must survive; case variants collapse.
        prefs.SetCommandHistory(
            scenarioId,
            [new CommandHistoryEntry("UAL1", "cland"), new CommandHistoryEntry("UAL1", "CLAND"), new CommandHistoryEntry("AAL2", "cland")]
        );

        var loaded = new UserPreferences().GetCommandHistory(scenarioId);

        Assert.Equal([new CommandHistoryEntry("UAL1", "CLAND"), new CommandHistoryEntry("AAL2", "CLAND")], loaded);
    }

    [Fact]
    public void SetCommandHistory_OverwritesPreviousSnapshot()
    {
        const string scenarioId = "TEST-overwrite-DEF";

        var prefs = new UserPreferences();
        prefs.SetCommandHistory(scenarioId, [new CommandHistoryEntry("", "OLD1"), new CommandHistoryEntry("", "OLD2")]);
        prefs.SetCommandHistory(scenarioId, [new CommandHistoryEntry("", "NEW")]);

        var loaded = new UserPreferences().GetCommandHistory(scenarioId);

        Assert.Equal([new CommandHistoryEntry("", "NEW")], loaded);
    }

    [Fact]
    public void SetCommandHistory_KeepsScenariosIndependent()
    {
        const string scenarioA = "TEST-iso-A";
        const string scenarioB = "TEST-iso-B";

        var prefs = new UserPreferences();
        prefs.SetCommandHistory(scenarioA, [new CommandHistoryEntry("", "A1"), new CommandHistoryEntry("", "A2")]);
        prefs.SetCommandHistory(scenarioB, [new CommandHistoryEntry("", "B1")]);

        var reader = new UserPreferences();
        Assert.Equal([new CommandHistoryEntry("", "A1"), new CommandHistoryEntry("", "A2")], reader.GetCommandHistory(scenarioA));
        Assert.Equal([new CommandHistoryEntry("", "B1")], reader.GetCommandHistory(scenarioB));
    }

    [Fact]
    public void SoloPacingRates_PersistAndClamp()
    {
        var writer = new UserPreferences();
        writer.SetSoloPacingRates(-10, 125);

        var reader = new UserPreferences();

        Assert.Equal(0, reader.SoloParkingInitialCallupRatePercent);
        Assert.Equal(100, reader.SoloArrivalGeneratorRatePercent);
    }

    // Regression for the CI-only flake where two threads racing Save() on the
    // same prefs.json clobbered each other's intermediate .tmp file. With
    // per-call unique .tmp suffixes Save calls are independent and can't race.
    [Fact]
    public void Save_ConcurrentWritersDoNotRaceOnTmpFile()
    {
        const int writerCount = 16;
        const int writesPerWriter = 8;

        Parallel.For(
            0,
            writerCount,
            i =>
            {
                var prefs = new UserPreferences();
                for (int j = 0; j < writesPerWriter; j++)
                {
                    prefs.SetCommandHistory($"TEST-race-{i}-{j}", [new CommandHistoryEntry("", $"CMD{i}-{j}")]);
                }
            }
        );

        // Final reader sees a valid prefs.json — at minimum its own write
        // round-trips. Other writers' scenarios may have been overwritten;
        // we only assert the file is parseable and the last write survives.
        var final = new UserPreferences();
        final.SetCommandHistory("TEST-race-final", [new CommandHistoryEntry("", "FINAL")]);
        var reread = new UserPreferences();
        Assert.Equal([new CommandHistoryEntry("", "FINAL")], reread.GetCommandHistory("TEST-race-final"));
    }
}
