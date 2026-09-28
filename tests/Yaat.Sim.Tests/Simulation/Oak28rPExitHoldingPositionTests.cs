using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An uninstructed OAK 28R arrival that turns off on P comes to rest beyond the 28R holding position, not in the P/J
/// intersection (S2-OAK-5 report, 2026-09-27: "28R arrivals naturally exit at J and stop half on the runway, half on J").
///
/// <para>P leaves 28R at 53° and dead-ends into J about 141 ft from the centerline, inside the 225 ft
/// <c>holdShortDistance</c> the vNAS map authors for 28R/10L. P has no 28R bar of its own: P and J share J's 28R
/// marking, east of the junction at ~222 ft, so the P exit turns right onto J and ends at that bar. AIM 4-3-21.b: absent
/// ATC instructions the pilot taxis beyond the runway holding position markings, even if that requires entering another
/// taxiway. With J's bar occupied the P exit is unavailable.</para>
/// </summary>
public class Oak28rPExitHoldingPositionTests(ITestOutputHelper output)
{
    private static readonly TrueHeading Runway28RHeading = new(292.2);

    /// <summary>The 28R centerline node P branches from, and the one H branches from (located by position, not id).</summary>
    private static readonly LatLon PBranch = new(37.729433, -122.219017);
    private static readonly LatLon HBranch = new(37.727932, -122.214378);

    private static readonly ExitPreference PRight = new() { Taxiway = "P", Side = ExitSide.Right };

    [Theory]
    [InlineData("C25A")]
    [InlineData("C560")]
    public void UninstructedPExit_StopsWithTheTailBeyondTheRunwayHoldingDistance(string aircraftType)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", aircraftType, "TST1");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        GroundRunway? runway = aircraft.Ground.Layout?.FindRunway("28R");
        Assert.NotNull(runway);
        Assert.NotNull(runway.HoldShortDistanceFt);
        double holdingDistanceFt = runway.HoldShortDistanceFt.Value;
        TrueHeading landingHeading = aircraft.TrueHeading;

        string? exitTaxiway = null;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase)
            {
                exitTaxiway ??= aircraft.Ground.CurrentTaxiway;
            }

            if (aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase holding)
            {
                continue;
            }

            Assert.Equal("P", exitTaxiway);
            Assert.NotNull(holding.HoldShortNodeId);
            Assert.False(holding.StoppedInsideHoldingDistance);
            GroundNode bar = aircraft.Ground.Layout!.Nodes[holding.HoldShortNodeId.Value];
            Assert.Contains(bar.Edges, edge => edge.MatchesTaxiway("J"));
            Assert.DoesNotContain(bar.Edges, edge => edge.MatchesTaxiway("P"));

            double halfLengthNm = AircraftLength.ResolveFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;
            LatLon tail = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading.ToReciprocal(), halfLengthNm);
            double tailPastBarFt = GeoMath.AlongTrackDistanceNm(tail, bar.Position, aircraft.TrueHeading) * GeoMath.FeetPerNm;
            double tailFt = CrossTrackFt(runway, tail);
            output.WriteLine(
                $"{aircraftType}: stopped at t+{t}s, tail {tailPastBarFt:F1} ft past bar #{bar.Id}; "
                    + $"centroid {CrossTrackFt(runway, aircraft.Position):F0} ft "
                    + $"and tail {tailFt:F0} ft from the 28R centerline; holding distance {holdingDistanceFt:F0} ft"
            );
            Assert.True(
                tailPastBarFt >= -AirportGroundLayout.HoldingDistanceToleranceFt,
                $"{aircraftType} stopped with its tail {-tailPastBarFt:F1} ft short of the 28R bar #{bar.Id} on J"
            );
            Assert.True(
                tailFt >= holdingDistanceFt - AirportGroundLayout.HoldingDistanceToleranceFt,
                $"{aircraftType} exited on P and stopped with its tail {tailFt:F0} ft from the 28R centerline, inside the "
                    + $"{holdingDistanceFt:F0} ft runway holding distance"
            );

            // It turned right onto J, away from the landing direction, and reads as on J.
            Assert.Equal("J", aircraft.Ground.CurrentTaxiway);
            double turnFromLandingDeg = Math.Abs(landingHeading.SignedAngleTo(aircraft.TrueHeading));
            Assert.True(
                turnFromLandingDeg > 90.0,
                $"{aircraftType} held on J heading {aircraft.TrueHeading.Degrees:F0}°, {turnFromLandingDeg:F0}° from the landing heading "
                    + $"{landingHeading.Degrees:F0}°; expected it eastbound on J, turned back away from the landing direction"
            );
            return;
        }

        Assert.Fail($"{aircraftType} never finished its runway exit within 300 s");
    }

    /// <summary>The P exit ends at J's 28R bar and is still named P; with that bar occupied there is no P exit at all.</summary>
    [Fact]
    public void PExit_EndsAtJsBar_AndIsUnavailableWhenItIsOccupied()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode? centerline = layout.FindNearestCenterlineNode(PBranch.Lat, PBranch.Lon, Runway28RHeading, "28R");
        Assert.NotNull(centerline);

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? exit = layout.FindAdjacentHoldShort(
            centerline,
            "28R",
            Runway28RHeading,
            PRight
        );
        Assert.NotNull(exit);
        Assert.Equal("P", exit.Value.Taxiway);
        Assert.Contains(exit.Value.Node.Edges, edge => edge.MatchesTaxiway("J"));
        Assert.Single(exit.Value.Path, n => n.Type == GroundNodeType.RunwayHoldShort);

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? occupied = layout.FindAdjacentHoldShort(
            centerline,
            "28R",
            Runway28RHeading,
            PRight,
            excludeHoldShortNodes: [exit.Value.Node.Id]
        );

        // Only P's crossing to the south (left) side remains, the search's off-side fallback.
        Assert.False(occupied is { Side: ExitSide.Right }, $"P still exits right, to #{occupied?.Node.Id}");
        Assert.NotEqual(exit.Value.Node.Id, occupied?.Node.Id);
    }

    /// <summary>
    /// With an aircraft holding at J's 28R bar, an uninstructed arrival does not take P (it would end at that bar) nor J
    /// right (the same bar), and takes the next exit it can brake for on the ramp side instead: C1.
    /// </summary>
    [Fact]
    public void UninstructedRollout_WithJsBarOccupied_TakesTheNextExit()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "C25A", "TST1");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        GroundNode? centerline = layout.FindNearestCenterlineNode(PBranch.Lat, PBranch.Lon, Runway28RHeading, "28R");
        Assert.NotNull(centerline);
        GroundNode jBar = layout.FindAdjacentHoldShort(centerline, "28R", Runway28RHeading, PRight)!.Value.Node;
        engine.World.AddAircraft(HolderAt(jBar, layout));

        string? exitTaxiway = null;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase)
            {
                exitTaxiway ??= aircraft.Ground.CurrentTaxiway;
            }

            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase holding)
            {
                output.WriteLine($"exited on {exitTaxiway} at t+{t}s, holding at bar #{holding.HoldShortNodeId}");
                Assert.NotEqual(jBar.Id, holding.HoldShortNodeId);
                Assert.Equal("C1", exitTaxiway);
                return;
            }
        }

        Assert.Fail("the arrival never finished its runway exit within 300 s");
    }

    /// <summary>
    /// <c>ER P</c> with an aircraft holding at J's 28R bar: P's right-hand exit is gone. The controller named the side, so
    /// the arrival never turns left onto P's crossing to the south; it takes the next exit ahead on the right and the
    /// pilot says it cannot make P.
    /// </summary>
    [Fact]
    public void InstructedRightP_WithJsBarOccupied_TakesTheNextRightExitAndSaysUnable()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "C25A", "TST1");
        if (spawned is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        GroundNode? centerline = layout.FindNearestCenterlineNode(PBranch.Lat, PBranch.Lon, Runway28RHeading, "28R");
        Assert.NotNull(centerline);
        GroundNode jBar = layout.FindAdjacentHoldShort(centerline, "28R", Runway28RHeading, PRight)!.Value.Node;
        engine.World.AddAircraft(HolderAt(jBar, layout));
        Assert.True(engine.SendCommand("TST1", "ER P").Success);

        var unableCalls = new List<string>();
        void Capture(string callsign, string line)
        {
            if ((callsign == aircraft.Callsign) && line.Contains("negative on the exit", StringComparison.OrdinalIgnoreCase))
            {
                unableCalls.Add(line);
            }
        }

        engine.WarningEmitted += Capture;
        engine.TerminalEntryEmitted += entry => Capture(entry.Callsign, entry.Message);
        string? exitTaxiway = null;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase)
            {
                exitTaxiway ??= aircraft.Ground.CurrentTaxiway;
            }

            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase holding)
            {
                GroundNode bar = layout.Nodes[holding.HoldShortNodeId!.Value];
                double barSideDeg = Runway28RHeading.SignedAngleTo(new TrueHeading(GeoMath.BearingTo(PBranch, bar.Position)));
                output.WriteLine(
                    $"exited on {exitTaxiway} at t+{t}s, holding at bar #{bar.Id} ({barSideDeg:F0}° off the runway heading); "
                        + $"calls: {string.Join(" | ", unableCalls)}"
                );
                Assert.NotEqual("P", exitTaxiway);
                Assert.True(barSideDeg > 0, $"held at #{bar.Id}, left of 28R");
                Assert.Contains(unableCalls, line => line.Contains("negative on the exit at P", StringComparison.OrdinalIgnoreCase));
                return;
            }
        }

        Assert.Fail("the arrival never finished its runway exit within 300 s");
    }

    /// <summary>An ordinary exit whose bar is already at the holding distance (H, right side) is not continued.</summary>
    [Fact]
    public void HExit_BarAtTheHoldingDistance_PathEndsAtItsOwnBar()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode? centerline = layout.FindNearestCenterlineNode(HBranch.Lat, HBranch.Lon, Runway28RHeading, "28R");
        Assert.NotNull(centerline);
        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? result = layout.FindAdjacentHoldShort(
            centerline,
            "28R",
            Runway28RHeading,
            new ExitPreference { Taxiway = "H", Side = ExitSide.Right }
        );

        Assert.NotNull(result);
        Assert.Equal("H", result.Value.Taxiway);
        Assert.Equal(result.Value.Node.Id, result.Value.Path[^1].Id);
        Assert.Single(result.Value.Path, n => n.Type == GroundNodeType.RunwayHoldShort);
        Assert.Contains(result.Value.Node.Edges, edge => edge.MatchesTaxiway("H"));
    }

    /// <summary>An aircraft that exited 28R earlier and holds at <paramref name="bar"/>, which occupies it.</summary>
    private static AircraftState HolderAt(GroundNode bar, AirportGroundLayout layout)
    {
        var holder = new AircraftState
        {
            Callsign = "HOLD1",
            AircraftType = "C25A",
            Position = bar.Position,
            TrueHeading = new TrueHeading(60),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        holder.Ground.Layout = layout;
        holder.Phases.Add(new HoldingAfterExitPhase("28R", "J", bar.Id));
        holder.Phases.Start(CommandDispatcher.BuildMinimalContext(holder, layout));
        return holder;
    }

    /// <summary>Distance from the runway's centerline in the layout's own frame, the one the hold-short placement uses.</summary>
    private static double CrossTrackFt(GroundRunway runway, LatLon position)
    {
        (double Lat, double Lon) first = runway.Coordinates[0];
        (double Lat, double Lon) last = runway.Coordinates[^1];
        var reference = new LatLon(first.Lat, first.Lon);
        var heading = new TrueHeading(GeoMath.BearingTo(first.Lat, first.Lon, last.Lat, last.Lon));
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, reference, heading)) * GeoMath.FeetPerNm;
    }
}
