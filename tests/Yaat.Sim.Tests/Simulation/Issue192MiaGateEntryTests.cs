using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

public class Issue192MiaGateEntryTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue192-mia-d-gate-entry-recording.zip";
    private const string TargetParking = "D32";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Aal6069_TaxiAutoSouthDGate_UsesGateAlleyInsteadOfTaxiwayN()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snap = archive.ReadSnapshotAt(270);
        if (snap is null)
        {
            output.WriteLine("No snapshot near t=270, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);
        engine.ReplayCommand(
            new RecordedCommand(snap.ElapsedSeconds, "AAL6069", $"TAXIAUTO @{TargetParking}", "XX", "") { ReactionDelaySeconds = 0 }
        );
        engine.ReplayRange((int)snap.ElapsedSeconds, (int)snap.ElapsedSeconds + 1, recording.Actions);

        var aircraft = engine.FindAircraft("AAL6069");
        Assert.NotNull(aircraft);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.Equal(TargetParking, route.DestinationParking);
        Assert.NotEmpty(route.Segments);

        var sequence = string.Join(", ", route.Segments.Select(s => s.TaxiwayName));
        output.WriteLine($"Route: {sequence}");

        Assert.DoesNotContain(route.Segments, s => SegmentIncludesTaxiway(s.TaxiwayName, "N"));

        var layout = new TestAirportGroundData().GetLayout("MIA");
        var target = layout?.FindParkingByName(TargetParking);
        Assert.NotNull(target);
        Assert.Equal(target.Id, route.Segments[^1].ToNodeId);
    }

    private static bool SegmentIncludesTaxiway(string taxiwayName, string target)
    {
        var parts = taxiwayName.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, target, StringComparison.OrdinalIgnoreCase));
    }
}
