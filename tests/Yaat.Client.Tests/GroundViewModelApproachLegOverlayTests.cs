using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Client.Tests;

/// <summary>
/// The server bridges the gap between where an aircraft actually stands and where its resolved route starts
/// with a free-space RAMP leg (<see cref="TaxiApproachLeg"/>) — a plain PUSH leaves an aircraft out on the
/// apron ahead of the ramp node the pathfinder picks up from. The client never receives route geometry, so
/// the overlay reconstruction has to rebuild the same leg or the drawn route starts a hundred feet away from
/// the aircraft symbol instead of at its nose.
/// </summary>
public class GroundViewModelApproachLegOverlayTests
{
    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    [Fact]
    public void PushedOntoTheApron_OverlayStartsWithTheApproachLegFromTheAircraft()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        var position = new LatLon(37.710217680439534, -122.21728593336832);
        var ac = new AircraftModel
        {
            Callsign = "DAL2150",
            AircraftType = "B738",
            Position = position,
            Heading = new TrueHeading(53),
            CurrentTaxiway = "RAMP",
            TaxiRoute = "T U W W1",
            AssignedRunway = "30",
            HasActiveTaxiRoute = true,
        };

        TaxiRoute? route = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(route);
        Assert.True(route!.Segments[0].FromNodeId < 0, "the overlay must start with the free-space leg from the aircraft");

        double legStartFt = GeoMath.DistanceNm(position, route.Segments[0].Edge.FromNode.Position) * GeoMath.FeetPerNm;
        Assert.True(legStartFt < 1.0, $"the leg must start at the aircraft, but starts {legStartFt:F1} ft away");

        GroundNode? startNode = layout.FindNearestNode(position);
        Assert.NotNull(startNode);
        Assert.Equal(startNode!.Id, route.Segments[0].ToNodeId);
    }

    /// <summary>SKW5590 at SFO after <c>PUSH T7A</c> off F8: on the apron west of T7A's north end, nosed 254°.</summary>
    private static readonly LatLon Skw5590Position = new(37.62091321146667, -122.38580122316866);

    private static AirportGroundLayout? LoadSfoLayout()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    /// <summary>The route the simulation assigns SKW5590 for <c>TAXI T7A $7A</c> from <paramref name="position"/>.</summary>
    private static TaxiRoute ServerRoute(AirportGroundLayout layout, LatLon position, TrueHeading heading)
    {
        // The dispatch looks the field elevation up in the navigation database; the taxi route never reads it.
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
        var aircraft = new AircraftState
        {
            Callsign = "SKW5590",
            AircraftType = "CRJ7",
            Position = position,
            TrueHeading = heading,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(
            new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = 0,
                GroundLayout = layout,
                Logger = NullLogger.Instance,
            }
        );
        aircraft.Ground.Layout = layout;
        var ctx = new DispatchContext
        {
            GroundLayout = layout,
            Rng = new Random(1),
            Weather = null,
            FindAircraft = null,
            ListAircraft = () => [aircraft],
            ValidateDctFixes = false,
            AutoCrossRunway = false,
            SoloTrainingMode = false,
            RpoShowPilotSpeech = false,
            TerminalEmitter = null,
            ArtccConfig = null,
            ScenarioElapsedSeconds = 0,
            SessionStartUtc = DateTime.UnixEpoch,
            PreserveConditionals = false,
            IsScenarioScripted = false,
        };
        CommandResult result = CommandDispatcher.Dispatch(new TaxiCommand(["T7A"], [], DestinationSpot: "7A"), aircraft, ctx);
        Assert.True(result.Success, result.Message);
        return aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException("the TAXI left no route");
    }

    private static AircraftModel Skw5590(LatLon position, TrueHeading heading, TaxiRoute server) =>
        new()
        {
            Callsign = "SKW5590",
            AircraftType = "CRJ7",
            Position = position,
            Heading = heading,
            TaxiRoute = server.FormatTaxiwaySequence(),
            TaxiDestination = "$7A",
            HasActiveTaxiRoute = true,
            IsOnGround = true,
        };

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// A spot cleared from the ramp is a line-up to leave it (#456); the overlay rebuilds the line-up the simulation
    /// planned for SKW5590 — the same legs across the apron, the same join and the same pull up T7A.
    /// </summary>
    [Fact]
    public void SpotLineUp_FromThePushbackPose_DrawsTheServersRoute()
    {
        AirportGroundLayout? layout = LoadSfoLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        var heading = new TrueHeading(254.0);
        TaxiRoute server = ServerRoute(layout, Skw5590Position, heading);
        Assert.NotNull(server.SpotLineUpPullFromSegment);

        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        TaxiRoute? drawn = vm.ResolveRemainingRoute(Skw5590(Skw5590Position, heading, server));

        Assert.NotNull(drawn);
        Assert.Equal(server.Segments.Count, drawn!.Segments.Count);
        for (int i = 0; i < server.Segments.Count; i++)
        {
            Assert.True(
                DistanceFt(server.Segments[i].Edge.FromNode.Position, drawn.Segments[i].Edge.FromNode.Position) < 1.0,
                $"segment {i} starts elsewhere"
            );
            Assert.True(
                DistanceFt(server.Segments[i].Edge.ToNode.Position, drawn.Segments[i].Edge.ToNode.Position) < 1.0,
                $"segment {i} ends elsewhere"
            );
        }
    }

    /// <summary>
    /// Partway along the line-up's run-in the overlay draws the rest of the run-in and the pull — not a leg back out to
    /// the approach point it has already passed. Earlier on the run-in, and on the apron crossing, the overlay still
    /// re-plans from the nearest node (a backlog item in docs/plans/MAIN.md).
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void SpotLineUp_PartwayAlongTheRunIn_DrawsNoLegBackOut(double fraction)
    {
        AirportGroundLayout? layout = LoadSfoLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        TaxiRoute server = ServerRoute(layout, Skw5590Position, new TrueHeading(254.0));
        Assert.True(
            server.SpotLineUpPullFromSegment is 2,
            $"expected a crossing and a run-in ahead of the pull, got {server.SpotLineUpPullFromSegment}"
        );
        TaxiRouteSegment runIn = server.Segments[1];
        LatLon approach = runIn.Edge.FromNode.Position;
        LatLon join = runIn.Edge.ToNode.Position;
        double runInDeg = GeoMath.BearingTo(approach, join);
        LatLon partway = GeoMath.ProjectPoint(approach, new TrueHeading(runInDeg), DistanceFt(approach, join) * fraction / GeoMath.FeetPerNm);

        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        TaxiRoute? drawn = vm.ResolveRemainingRoute(Skw5590(partway, new TrueHeading(runInDeg), server));

        Assert.NotNull(drawn);
        Assert.True(DistanceFt(drawn!.Segments[0].Edge.FromNode.Position, partway) < 1.0, "the overlay does not start at the aircraft");
        Assert.DoesNotContain(drawn.Segments, s => DistanceFt(s.Edge.ToNode.Position, approach) < 5.0);
        Assert.True(
            GeoMath.AbsBearingDifference(drawn.Segments[0].Edge.DepartureBearing, runInDeg) < 10.0,
            "the first leg does not carry on along the run-in"
        );
        Assert.Equal(server.Segments[^1].ToNodeId, drawn.Segments[^1].ToNodeId);
    }
}
