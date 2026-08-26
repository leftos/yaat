using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #393: scripted SFO departures parked at their runway bar taxied the full length of the
/// runway to the reciprocal end.
///
/// Recording: S2-SFO-3 "High Intensity" (trimmed to the first 5 s — the bug is at load). SWA701
/// (M1, 19 ft short of the 1L bar) has presets <c>TAXI 1L ; POS</c>, ASA315 (A1, at the 1R bar)
/// <c>TAXI 1R ; POS</c>, and DLH455 (F, 27 ft short of the 28L bar) <c>TAXI 28L</c>. A lone runway token
/// never became the destination — it stayed a path token meaning "taxi ALONG runway 1L", so the route
/// walked the 01L/19R centerline to the far threshold with no hold-short, the auto-detected departure
/// runway became 19R, and <c>POS</c> had no destination bar to arm against. ACA569 (<c>TAXI A1 1R</c>,
/// two tokens) was correct, which is why only the t=0 aircraft misbehaved.
///
/// A lone runway token is a destination the aircraft must already be at: SWA701/ASA315 hold short of
/// (then line up on) 1L/1R, DLH455 holds short of 28L, and nobody enters the runway surface uncleared.
/// An aircraft that is NOT at the runway gets the bare form rejected rather than a guessed route across
/// the airport — <c>TAXIAUTO</c> is the explicit auto-route.
/// </summary>
public class Issue393SfoBareTaxiRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue393-sfo-bare-taxi-runway-recording.zip";

    /// <summary>Long enough for a 20-30 ft creep to the bar plus a line-up, well short of any runway-length taxi.</summary>
    private const int ObserveSeconds = 90;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static bool IsOnRunwaySurface(TaxiRoute route, string runwayId) =>
        route.Segments.Any(s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway(runwayId));

    private void LogState(string label, AircraftState aircraft)
    {
        var route = aircraft.Ground.AssignedTaxiRoute;
        output.WriteLine(
            $"{label} {aircraft.Callsign}: phase={aircraft.Phases?.CurrentPhase?.GetType().Name} "
                + $"depRwy={aircraft.Procedure.DepartureRunway} assigned={aircraft.Phases?.AssignedRunway?.Designator} "
                + $"route={route?.ToSummary() ?? "(none)"} segs={route?.Segments.Count} "
                + $"hs=[{string.Join(", ", route?.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})") ?? [])}] "
                + $"clearance={aircraft.Phases?.DepartureClearance?.Type}"
        );
    }

    [Fact]
    public void Diagnostic_LogSpawnRoutes()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        foreach (var callsign in new[] { "SWA701", "ASA315", "DLH455", "ACA569" })
        {
            var aircraft = engine.FindAircraft(callsign);
            Assert.NotNull(aircraft);
            LogState("t=0", aircraft);
        }

        for (int t = 1; t <= ObserveSeconds; t++)
        {
            engine.ReplayOneSecond();
            if (t % 10 == 0)
            {
                foreach (var callsign in new[] { "SWA701", "ASA315", "DLH455" })
                {
                    var aircraft = engine.FindAircraft(callsign);
                    if (aircraft is not null)
                    {
                        LogState($"t={t}", aircraft);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("SWA701", "1L", "M1", "01L", true)]
    [InlineData("ASA315", "1R", "A1", "01R", true)]
    [InlineData("DLH455", "28L", "F", "28L", false)]
    public void BareTaxiRunwayPreset_HoldsShortOfItsOwnRunway(string callsign, string runway, string taxiway, string expectedDesignator, bool linesUp)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        var aircraft = engine.FindAircraft(callsign);
        Assert.NotNull(aircraft);
        LogState("t=0", aircraft);

        var layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);
        var bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, runway, taxiway);
        Assert.NotNull(bar);

        Assert.Equal(expectedDesignator, aircraft.Procedure.DepartureRunway);
        Assert.Equal(expectedDesignator, aircraft.Phases?.AssignedRunway?.Designator);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.False(IsOnRunwaySurface(route, runway), $"route must not taxi along runway {runway}: {route.ToSummary()}");
        var destination = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.Equal(bar.Id, destination.NodeId);

        if (linesUp)
        {
            Assert.Equal(ClearanceType.LineUpAndWait, aircraft.Phases?.DepartureClearance?.Type);
        }

        var runwayInfo = aircraft.Phases?.AssignedRunway;
        Assert.NotNull(runwayInfo);
        bool reachedGoal = false;
        for (int t = 1; t <= ObserveSeconds; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(callsign);
            Assert.NotNull(aircraft);

            // Never more than a few hundred feet from the bar it started next to — a runway-length
            // taxi toward the reciprocal end is the bug.
            double fromBarFt = GeoMath.DistanceNm(aircraft.Position, bar.Position) * GeoMath.FeetPerNm;
            Assert.True(fromBarFt < 600, $"t={t}: {callsign} is {fromBarFt:F0} ft from the {runway} bar on {taxiway}");
            Assert.Equal(expectedDesignator, aircraft.Procedure.DepartureRunway);

            var phase = aircraft.Phases?.CurrentPhase;
            if (linesUp ? phase is LinedUpAndWaitingPhase : phase is HoldingShortPhase)
            {
                reachedGoal = true;
                LogState($"t={t}", aircraft);
                break;
            }
        }

        Assert.True(reachedGoal, $"{callsign} never reached {(linesUp ? "LinedUpAndWaiting" : "HoldingShort")} within {ObserveSeconds}s");
    }

    /// <summary>
    /// SKW4775 spawned on taxiway B with <c>TAXI B M1 1L</c>; it is nowhere near the 1L bar. A bare
    /// <c>TAXI 1L</c> to it is under-specified and must be refused with a pointer at the two forms that
    /// carry a route; <c>TAXIAUTO 1L</c> is that explicit auto-route and still resolves.
    /// </summary>
    [Fact]
    public void BareTaxiRunway_AwayFromTheRunway_IsRejected_TaxiAutoStillRoutes()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var aircraft = engine.FindAircraft("SKW4775");
        Assert.NotNull(aircraft);
        LogState("t=0", aircraft);

        var bare = engine.SendCommand("SKW4775", "TAXI 1L");
        output.WriteLine($"TAXI 1L: {bare.Success} {bare.Message}");
        Assert.False(bare.Success);
        Assert.Contains("not at runway 1L", bare.Message);
        Assert.Contains("TAXIAUTO 1L", bare.Message);

        // The rejected command left the original clearance untouched.
        aircraft = engine.FindAircraft("SKW4775");
        Assert.NotNull(aircraft);
        Assert.Equal("01L", aircraft.Procedure.DepartureRunway);
        Assert.NotNull(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains("M1", aircraft.Ground.AssignedTaxiRoute.ToSummary());

        var auto = engine.SendCommand("SKW4775", "TAXIAUTO 1L");
        output.WriteLine($"TAXIAUTO 1L: {auto.Success} {auto.Message}");
        Assert.True(auto.Success, auto.Message);
    }

    /// <summary>
    /// A few hundred feet short of the bar on the same taxiway, facing it, the bare form is still "taxi up to
    /// the runway and hold short": a straight run on M1 to the 1L bar, no turn, no other runway.
    /// </summary>
    [Fact]
    public void BareTaxiRunway_ShortOfTheBarOnItsTaxiway_TaxisUpAndHoldsShort()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var aircraft = engine.FindAircraft("SWA701");
        Assert.NotNull(aircraft);
        var layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);
        var bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "1L", "M1");
        Assert.NotNull(bar);

        // A node on M1 between 150 and 500 ft back from the bar — beyond the at-the-bar radius, inside the short run.
        var start = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("M1")))
            .Select(n => (Node: n, Ft: GeoMath.DistanceNm(n.Position, bar.Position) * GeoMath.FeetPerNm))
            .Where(x => (x.Ft >= 150) && (x.Ft <= 500))
            .OrderBy(x => x.Ft)
            .Select(x => x.Node)
            .FirstOrDefault();
        Assert.NotNull(start);

        aircraft.Position = start.Position;
        aircraft.TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, bar.Position));
        aircraft.IndicatedAirspeed = 0;

        var result = engine.SendCommand("SWA701", "TAXI 1L");
        output.WriteLine(
            $"TAXI 1L from {GeoMath.DistanceNm(start.Position, bar.Position) * GeoMath.FeetPerNm:F0} ft: {result.Success} {result.Message}"
        );
        Assert.True(result.Success, result.Message);

        aircraft = engine.FindAircraft("SWA701");
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.All(route.Segments, s => Assert.Equal("M1", s.TaxiwayName));
        var destination = Assert.Single(route.HoldShortPoints);
        Assert.Equal(HoldShortReason.DestinationRunway, destination.Reason);
        Assert.Equal(bar.Id, destination.NodeId);

        bool holding = false;
        for (int t = 1; t <= ObserveSeconds; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SWA701");
            Assert.NotNull(aircraft);
            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase)
            {
                holding = true;
                break;
            }
        }

        Assert.True(holding, "SWA701 should taxi up M1 and hold short of 1L");
        Assert.True(GeoMath.DistanceNm(aircraft.Position, bar.Position) * GeoMath.FeetPerNm < 150, "should stop at the 1L bar");
    }

    /// <summary>
    /// Holding short of 28L on F with the 28R bar on C beyond it: reaching 28R means crossing 28L (and a
    /// turn onto C), which a bare <c>TAXI 28R</c> cannot authorise — it is refused, not auto-cleared.
    /// </summary>
    [Fact]
    public void BareTaxiRunway_AcrossAnotherRunway_IsRejected()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var aircraft = engine.FindAircraft("SWA701");
        Assert.NotNull(aircraft);
        var layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);
        // Both taxiways carry bars at both runway ends; take the F(28L) / C(28R) pair that sits together.
        var pair = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(layout, "10R", "F")
            .SelectMany(f => TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "C").Select(c => (NearBar: f, FarBar28R: c)))
            .MinBy(x => GeoMath.DistanceNm(x.NearBar.Position, x.FarBar28R.Position));
        var nearBar = pair.NearBar;
        var farBar28R = pair.FarBar28R;
        Assert.NotNull(nearBar);
        Assert.NotNull(farBar28R);
        output.WriteLine($"28L bar on F -> 28R bar on C: {GeoMath.DistanceNm(nearBar.Position, farBar28R.Position) * GeoMath.FeetPerNm:F0} ft");

        aircraft.Position = nearBar.Position;
        aircraft.TrueHeading = new TrueHeading(GeoMath.BearingTo(nearBar.Position, farBar28R.Position));
        aircraft.IndicatedAirspeed = 0;

        var result = engine.SendCommand("SWA701", "TAXI 28R");
        output.WriteLine($"TAXI 28R: {result.Success} {result.Message}");
        Assert.False(result.Success);
        Assert.Contains("not at runway 28R", result.Message);
    }
}
