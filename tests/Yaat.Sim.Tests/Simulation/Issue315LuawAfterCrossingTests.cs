using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #315: LUAW is accepted but the aircraft never moves,
/// after a re-taxi that crosses one parallel runway to reach the other.
///
/// Recording: S2-SFO-4 | Shared Dep/Arr on Parallel Runways.
///
/// SKW5237 is set up for 28L, then re-taxied at t=536 with
/// <c>TAXI F C HS 10R RWY 28R</c> — an explicit hold-short at 10R/28L followed by a
/// DestinationRunway hold-short at 28R. At the time, that explicit hold-short bound to the
/// bar on the FAR side of 28L, so the crossing started from there and exited at the 28R
/// hold-short node (SFO taxiway C has a single painted bar between the two runways), which
/// consumed the rest of the route and terminated the phase chain with HoldingInPositionPhase
/// instead of HoldingShortPhase(28R).
///
/// LUAW (t=624) therefore took DepartureClearanceHandler.LineUpFromPosition, which
/// built its PhaseContext without a ground layout — LineUpPhase faulted immediately
/// with "ctx.GroundLayout is null" while the command still reported
/// "OK — Line up and wait runway 28R". The aircraft sat on the hold line until the
/// controller deleted it.
///
/// SKW3473 is the control case: its 10R/28L bar was an auto-cleared RunwayCrossing,
/// so no CrossingRunwayPhase was built, it reached the 28R bar through the normal
/// TaxiingPhase path, got HoldingShortPhase, and departed normally.
///
/// <para><b>Since issue #316</b> the hold-short binds to the bar on the aircraft's own side, so
/// SKW5237 now holds on Foxtrot until the RES at t=588, crosses 28L, exits at C's 10R/28L bar,
/// and taxis on to the 28R bar as a normal segment. The end state these tests assert is unchanged,
/// but the crossing-exit terminus they were written against is no longer on SKW5237's path —
/// <see cref="CrossingExitingOntoTheTerminalBar_HoldsShortOfTheDepartureRunway"/> guards it directly.</para>
/// </summary>
public class Issue315LuawAfterCrossingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue315-luaw-after-crossing-recording.zip";

    /// <summary>Callsign for the synthetic (non-recording) crossing-exit terminus case below.</summary>
    private const string SyntheticCallsign = "SKW9316";

    /// <summary>Recording time at which SKW5237 is stopped on the 28R hold line, before the LUAW at t=624.</summary>
    private const int Skw5237HoldingTime = 620;

    /// <summary>Recording time just past the LUAW at t=624. Ticking beyond t=666 would replay the controller's panic re-taxi.</summary>
    private const int Skw5237PostLuawTime = 630;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Skw5237_HoldsShortOfDepartureRunwayAfterCrossing()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, Skw5237HoldingTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);

        var phase = ac.Phases?.CurrentPhase;
        output.WriteLine($"t={Skw5237HoldingTime}: SKW5237 phase={phase?.Name} ias={ac.IndicatedAirspeed:F1}");

        var holding = Assert.IsType<HoldingShortPhase>(phase);
        Assert.Equal(HoldShortReason.DestinationRunway, holding.HoldShort.Reason);
        Assert.Equal("28R", holding.HoldShort.TargetName);
    }

    [Fact]
    public void Skw5237_StopsShortOfTheHoldLine_NotInsideTheMarkings()
    {
        // The crossing appends a half-fuselage tail-clearance overshoot past its exit node. When that exit node
        // is itself an uncleared hold-short bar, the overshoot carries the aircraft inside runway 28R's holding
        // position markings while it reports holding short of them — a runway incursion (AIM 4-3-18.a.6).
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, Skw5237HoldingTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);

        var holdShort = ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(hs => hs.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(holdShort);
        Assert.NotNull(holdShort.Latitude);
        Assert.NotNull(holdShort.Longitude);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(holdShort.NodeId, out var barNode), $"hold-short node {holdShort.NodeId} missing from the SFO layout");

        double toBarFt = GeoMath.DistanceNm(ac.Position, barNode.Position) * GeoMath.FeetPerNm;
        double toStopFt = GeoMath.DistanceNm(ac.Position, new LatLon(holdShort.Latitude.Value, holdShort.Longitude.Value)) * GeoMath.FeetPerNm;
        double barToStopFt =
            GeoMath.DistanceNm(barNode.Position, new LatLon(holdShort.Latitude.Value, holdShort.Longitude.Value)) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"t={Skw5237HoldingTime}: SKW5237 {toBarFt:F1}ft from the 28R bar, {toStopFt:F1}ft from its stop point (bar->stop {barToStopFt:F1}ft)"
        );

        // Stopping on the approach side means the aircraft is nearer its own stop point than the painted bar is
        // to that stop point — an overshoot past the bar puts it on the far side and inverts the comparison.
        Assert.True(toStopFt < barToStopFt, $"SKW5237 stopped {toBarFt:F1}ft past the 28R holding position markings instead of short of them");
    }

    [Fact]
    public void Skw5237_LuawFromDestinationHoldShort_LinesUpOnRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past the recorded LUAW, then tick physics only. The recording's later
        // actions (TAXI 28R C at t=666, DEL at t=686) are the controller working around
        // this very bug and would destroy the line-up under test.
        engine.Replay(recording, Skw5237PostLuawTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);
        output.WriteLine($"t={Skw5237PostLuawTime}: SKW5237 phase={ac.Phases?.CurrentPhase?.Name}");

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SKW5237");
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;
            if (phase is LineUpPhase lineUp)
            {
                Assert.False(
                    lineUp.CurrentState == LineUpPhase.State.Faulted,
                    $"LineUpPhase faulted {t}s after LUAW — the aircraft accepted the clearance but cannot line up"
                );
            }

            if (phase is LinedUpAndWaitingPhase)
            {
                output.WriteLine($"t={Skw5237PostLuawTime + t}: SKW5237 lined up and waiting");
                return;
            }
        }

        Assert.Fail($"SKW5237 never reached LinedUpAndWaiting; phase={ac.Phases?.CurrentPhase?.Name} ias={ac.IndicatedAirspeed:F1}");
    }

    /// <summary>
    /// Direct guard on the crossing-exit terminus itself, independent of the recording.
    ///
    /// <para>The #316 fix (hold-shorts bind to the bar on the aircraft's own side of a crossing) moved SKW5237
    /// off this code path: its crossing now exits at a real paired 10R/28L bar and the route continues to the
    /// 28R bar through a normal <see cref="TaxiingPhase"/>. The replay tests above still assert the right end
    /// state, but they no longer reach <c>BuildTerminalPhasesAtCrossingExit</c>. This does.</para>
    ///
    /// <para>Geometry: SFO taxiway C northbound from the 10R/28L centerline. The first bar ahead is C's
    /// 10R/28L bar with no paired bar beyond it, and the very next node is the 28R hold line — so the crossing
    /// exits directly onto the route's terminal bar. That must leave the aircraft <see cref="HoldingShortPhase"/>
    /// of 28R (so LUAW/CTO route through the hold-short path), stopped short of the markings.</para>
    /// </summary>
    [Fact]
    public void CrossingExitingOntoTheTerminalBar_HoldsShortOfTheDepartureRunway()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // C meets 10R/28L once, so its bar there is unambiguous. The 28R bar is taken from the resolved
        // route rather than by name — C has one on each side of 28R and only the route says which is the
        // terminus.
        var crossingBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "C");
        if (crossingBar is null)
        {
            return;
        }

        // Start on the 10R/28L centerline where C crosses it, nosed along C toward 28R.
        var start = layout.FindNearestCenterlineNode(crossingBar.Position.Lat, crossingBar.Position.Lon, new TrueHeading(298), "28L");
        if (start is null)
        {
            return;
        }

        var aircraft = SpawnOnTaxiway(start, new TrueHeading(GeoMath.BearingTo(start.Position, crossingBar.Position)), layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-issue315-crossing-exit-terminus",
            ScenarioName = "Crossing exits onto the terminal bar",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
            AutoCrossRunway = false,
        };

        var taxi = engine.SendCommand(SyntheticCallsign, "TAXI C 28R");
        Assert.True(taxi.Success, taxi.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"route={route.ToSummary()}");
        foreach (var point in route.HoldShortPoints)
        {
            output.WriteLine($"  hold-short #{point.NodeId} {point.TargetName} {point.Reason}");
        }

        // Pre-condition: the crossing bar is the last node before the terminal 28R bar, so the crossing
        // exits straight onto it. If the layout ever changes this, the test is no longer guarding the branch.
        Assert.Equal(crossingBar.Id, route.Segments[^1].FromNodeId);
        Assert.True(layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out var departureBar), "route terminus missing from the layout");
        Assert.Equal(GroundNodeType.RunwayHoldShort, departureBar.Type);
        Assert.True(departureBar.RunwayId?.Contains("28R") == true, $"route terminus #{departureBar.Id} is not a 28R bar");

        var atCrossing = TickUntilHoldingShort(engine, "10R", 180);
        Assert.NotNull(atCrossing);
        Assert.Equal(crossingBar.Id, atCrossing.HoldShort.NodeId);

        var cross = engine.SendCommand(SyntheticCallsign, "CROSS");
        Assert.True(cross.Success, cross.Message);

        var atDeparture = TickUntilHoldingShort(engine, "28R", 180);
        Assert.NotNull(atDeparture);
        Assert.Equal(HoldShortReason.DestinationRunway, atDeparture.HoldShort.Reason);
        Assert.Equal(departureBar.Id, atDeparture.HoldShort.NodeId);

        // ...and short of the painted bar, not a half-fuselage past it.
        aircraft = engine.FindAircraft(SyntheticCallsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(atDeparture.HoldShort.Latitude);
        Assert.NotNull(atDeparture.HoldShort.Longitude);
        var stopPoint = new LatLon(atDeparture.HoldShort.Latitude.Value, atDeparture.HoldShort.Longitude.Value);
        double toStopFt = GeoMath.DistanceNm(aircraft.Position, stopPoint) * GeoMath.FeetPerNm;
        double barToStopFt = GeoMath.DistanceNm(departureBar.Position, stopPoint) * GeoMath.FeetPerNm;
        output.WriteLine($"stopped {toStopFt:F1}ft from its stop point (bar->stop {barToStopFt:F1}ft)");
        Assert.True(toStopFt < barToStopFt, $"aircraft overshot the 28R holding position markings ({toStopFt:F1}ft from its stop point)");

        // LUAW from here must line up rather than fault — the whole point of routing through HoldingShortPhase.
        var luaw = engine.SendCommand(SyntheticCallsign, "LUAW");
        Assert.True(luaw.Success, luaw.Message);
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            var phase = engine.FindAircraft(SyntheticCallsign)?.Phases?.CurrentPhase;
            if (phase is LineUpPhase lineUp)
            {
                Assert.False(lineUp.CurrentState == LineUpPhase.State.Faulted, $"LineUpPhase faulted {t}s after LUAW");
            }

            if (phase is LinedUpAndWaitingPhase)
            {
                return;
            }
        }

        Assert.Fail($"never reached LinedUpAndWaiting; phase={engine.FindAircraft(SyntheticCallsign)?.Phases?.CurrentPhase?.Name}");
    }

    private HoldingShortPhase? TickUntilHoldingShort(SimulationEngine engine, string runwayDesignator, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(SyntheticCallsign);
            if (aircraft is null)
            {
                return null;
            }

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hold
                && hold.HoldShort.TargetName is { } target
                && RunwayIdentifier.Parse(target).Contains(runwayDesignator)
            )
            {
                output.WriteLine($"t={t}: holding short of {target} at node #{hold.HoldShort.NodeId}");
                return hold;
            }
        }

        var last = engine.FindAircraft(SyntheticCallsign);
        output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private static AircraftState SpawnOnTaxiway(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = SyntheticCallsign,
            AircraftType = "E75L",
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KPSC",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(37000),
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    [Fact]
    public void Skw3473_LuawFromHoldShort_StillDeparts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Control case: TAXI F C 28R (t=768), LUAW (t=885), CTO (t=948). Airborne by t=990.
        engine.Replay(recording, 990);

        var ac = engine.FindAircraft("SKW3473");
        Assert.NotNull(ac);
        output.WriteLine($"t=990: SKW3473 phase={ac.Phases?.CurrentPhase?.Name} alt={ac.Altitude:F0} onGround={ac.IsOnGround}");

        Assert.False(ac.IsOnGround, "SKW3473 should be airborne after CTO at t=948");
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
    }
}
