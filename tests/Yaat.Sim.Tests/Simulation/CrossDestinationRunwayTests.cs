using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CROSS &lt;rwy&gt; must work when the runway was the destination of the previous
/// taxi command, not just an intermediate crossing.
///
/// Recording fixture: S2-OAK-5 (Practical Exam, OAK). N436MS (C182) is given the
/// preset TAXI B 28R. It taxis taxiway B to the 28R hold-short (node 188), whose
/// hold-short reason is <see cref="HoldShortReason.DestinationRunway"/>. The
/// shipped CROSS-from-completed-taxi feature only handled intermediate
/// <see cref="HoldShortReason.RunwayCrossing"/> holds, so CROSS 28R here was
/// rejected with "Cannot cross destination runway 28R; use LUAW or CTO".
///
/// At t=10 the route is complete and N436MS is HoldingShort of 28R (the reported
/// case). At t=0 it is still Taxiing toward 28R with the route incomplete (the
/// en-route case). Both must cross to the far-side 28R hold-short.
/// </summary>
public sealed class CrossDestinationRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-cross-dest-runway-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N436MS";
    private const string CrossRunway = "28R";

    [Fact]
    public void CrossRunway_HoldingShortOfDestinationRunway_CrossesToFarSide()
    {
        var restored = RestoreAt(10);
        if (restored is null)
        {
            return;
        }

        using var archive = restored.Value.Archive;
        var engine = restored.Value.Engine;
        var layout = restored.Value.Layout;

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Precondition: arrived at its destination runway, holding short, route complete.
        var holdPhase = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.DestinationRunway, holdPhase.HoldShort.Reason);
        Assert.True(RunwayIdentifier.Parse(holdPhase.HoldShort.TargetName!).Contains(CrossRunway));
        Assert.True(aircraft.Ground.AssignedTaxiRoute?.IsComplete, "Precondition: route completed at the destination-runway hold-short");

        var result = engine.SendCommand(Callsign, $"CROSS {CrossRunway}");
        output.WriteLine($"CROSS {CrossRunway}: success={result.Success} message={result.Message}");
        Assert.True(result.Success, $"CROSS {CrossRunway} should be accepted at the destination-runway hold-short: {result.Message}");

        var observation = TickAndObserve(engine, layout, aircraft.Position, maxSeconds: 40);
        Assert.True(observation.LeftInitialHold, $"{Callsign} should leave HoldingShort after CROSS {CrossRunway}");
        Assert.True(observation.SawCrossing, $"{Callsign} should enter CrossingRunwayPhase across {CrossRunway}");
    }

    [Fact]
    public void CrossRunway_TaxiingTowardDestinationRunway_CrossesOnArrival()
    {
        var restored = RestoreAt(0);
        if (restored is null)
        {
            return;
        }

        using var archive = restored.Value.Archive;
        var engine = restored.Value.Engine;
        var layout = restored.Value.Layout;

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Precondition: still taxiing toward its destination runway, route incomplete.
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.False(aircraft.Ground.AssignedTaxiRoute?.IsComplete ?? true, "Precondition: route still has segments to the 28R hold-short");

        var result = engine.SendCommand(Callsign, $"CROSS {CrossRunway}");
        output.WriteLine($"CROSS {CrossRunway}: success={result.Success} message={result.Message}");
        Assert.True(result.Success, $"CROSS {CrossRunway} should be accepted while taxiing toward the destination runway: {result.Message}");

        var observation = TickAndObserve(engine, layout, aircraft.Position, maxSeconds: 40);
        Assert.True(observation.SawCrossing, $"{Callsign} should cross {CrossRunway} on arrival instead of stopping as a departure hold");
        Assert.True(observation.EndedHoldingInPosition, $"{Callsign} should hold in position on the far side after crossing {CrossRunway}");
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, RecordingArchive Archive)? RestoreAt(double targetSeconds)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        var snapshot = archive.ReadSnapshotAt(targetSeconds);
        if (snapshot is null)
        {
            archive.Dispose();
            return null;
        }

        var layout = archive.ReadLayout("oak");
        var engine = BuildEngine(layout);
        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        engine.RestoreFromSnapshot(snapshot.State);

        // Apply CROSS immediately (no reaction delay) so the result reflects the
        // command outcome and the crossing is observable within the tick window.
        if (engine.Scenario is not null)
        {
            engine.Scenario.CommandRunDelayMinSeconds = 0;
            engine.Scenario.CommandRunDelayMaxSeconds = 0;
        }

        return (engine, layout, archive);
    }

    private SimulationEngine BuildEngine(AirportGroundLayout layout)
    {
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .EnableCategory("CrossingRunwayPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new SingleLayoutGroundData(layout));
    }

    private (bool LeftInitialHold, bool SawCrossing, bool EndedHoldingInPosition) TickAndObserve(
        SimulationEngine engine,
        AirportGroundLayout layout,
        LatLon initialPosition,
        int maxSeconds
    )
    {
        bool leftInitialHold = false;
        bool sawCrossing = false;
        bool endedHoldingInPosition = false;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            var phase = aircraft.Phases?.CurrentPhase;
            sawCrossing |= phase is CrossingRunwayPhase;
            leftInitialHold |= phase is not HoldingShortPhase || GeoMath.DistanceNm(initialPosition, aircraft.Position) > 0.005;
            endedHoldingInPosition = phase is HoldingInPositionPhase;

            NearestNodeHelper.Log(output, $"dt={t} phase={phase?.GetType().Name ?? "null"}", aircraft, layout);
        }

        return (leftInitialHold, sawCrossing, endedHoldingInPosition);
    }

    private sealed class SingleLayoutGroundData(AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId)
        {
            string shortId = airportId.Length == 4 && airportId[0] == 'K' ? airportId[1..] : airportId;
            return string.Equals(shortId, layout.AirportId, StringComparison.OrdinalIgnoreCase) ? layout : null;
        }

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
