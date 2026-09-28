using Xunit;
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
/// <c>holdShortDistance</c> the vNAS map authors for 28R/10L. The layout builder cannot fit a bar on P at that distance,
/// so it places one 25 ft short of the junction (~121 ft), and the exit ends half a fuselage past it: nose on J, tail on
/// the bar, the whole airframe inside the runway's holding distance. J's own 28R bar is east of the junction at ~218 ft.
/// AIM 4-3-21.b: absent ATC instructions the pilot taxis beyond the runway holding position markings, even if that
/// requires entering another taxiway.</para>
/// </summary>
public class Oak28rPExitHoldingPositionTests(ITestOutputHelper output)
{
    private static readonly TrueHeading Runway28RHeading = new(292.2);

    /// <summary>The 28R centerline node P branches from, and the one H branches from (located by position, not id).</summary>
    private static readonly LatLon PBranch = new(37.729433, -122.219017);
    private static readonly LatLon HBranch = new(37.727932, -122.214378);

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
            GroundNode bar = aircraft.Ground.Layout!.Nodes[holding.HoldShortNodeId.Value];
            Assert.Contains(bar.Edges, edge => edge.MatchesTaxiway("J"));
            Assert.DoesNotContain(bar.Edges, edge => edge.MatchesTaxiway("P"));

            double halfLengthNm = AircraftLength.ResolveFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;
            LatLon tail = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading.ToReciprocal(), halfLengthNm);
            double tailPastBarFt = GeoMath.AlongTrackDistanceNm(tail, bar.Position, aircraft.TrueHeading) * GeoMath.FeetPerNm;
            double tailFt = CrossTrackFt(runway, tail);
            output.WriteLine(
                $"{aircraftType}: stopped at t+{t}s, tail {tailPastBarFt:F1} ft past bar #{bar.Id}; centroid {CrossTrackFt(runway, aircraft.Position):F0} ft "
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

    /// <summary>
    /// With J's 28R bar occupied, the P exit ends where it did before the continuation existed: at P's own short bar,
    /// on the same path up to it.
    /// </summary>
    [Fact]
    public void PExit_WithJBarOccupied_EndsAtPsOwnBar()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode? centerline = layout.FindNearestCenterlineNode(PBranch.Lat, PBranch.Lon, Runway28RHeading, "28R");
        Assert.NotNull(centerline);
        var pref = new ExitPreference { Taxiway = "P", Side = ExitSide.Right };

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? continued = layout.FindAdjacentHoldShort(
            centerline,
            "28R",
            Runway28RHeading,
            pref
        );
        Assert.NotNull(continued);
        GroundNode pBar = AirportGroundLayout.FirstHoldShortOnPath(continued.Value.Path, continued.Value.Node);
        Assert.NotEqual(pBar.Id, continued.Value.Node.Id);
        Assert.Contains(pBar.Edges, edge => edge.MatchesTaxiway("P"));

        (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? occupied = layout.FindAdjacentHoldShort(
            centerline,
            "28R",
            Runway28RHeading,
            pref,
            excludeHoldShortNodes: [continued.Value.Node.Id]
        );
        Assert.NotNull(occupied);
        Assert.Equal(pBar.Id, occupied.Value.Node.Id);
        Assert.Equal("P", occupied.Value.Taxiway);
        List<int> expectedPath = [.. continued.Value.Path.TakeWhile(n => n.Id != pBar.Id).Select(n => n.Id), pBar.Id];
        Assert.Equal(expectedPath, occupied.Value.Path.Select(n => n.Id));
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
