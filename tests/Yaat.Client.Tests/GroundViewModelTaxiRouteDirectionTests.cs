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
/// Issue #475: UPS2951 at OAK, <c>PUSH S; TAXI S HS B</c>. The simulation taxis north-west along S to the S/B
/// intersection, but the hover overlay drew S the other way, south-east to the S/T intersection. The broadcast
/// route is the bare string <c>"S"</c>, so the only thing that tells the reconstruction which way along S the
/// aircraft is going is its heading — the same <see cref="ExplicitPathOptions.StartHeadingTrue"/> the server
/// resolved the clearance with.
/// </summary>
public class GroundViewModelTaxiRouteDirectionTests
{
    /// <summary>Where UPS2951 stood, nose 11.6° true, when its pushback ended and the TAXI S HS B route was resolved.</summary>
    private static readonly LatLon PushedPose = new(37.71418366380452, -122.21639525332785);

    private const double PushedHeadingDeg = 11.584137246286673;

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    /// <summary>The route the simulation assigns UPS2951 for <c>TAXI S HS B</c> from the end of its pushback.</summary>
    private static TaxiRoute ServerRoute(AirportGroundLayout layout)
    {
        // The dispatch looks the field elevation up in the navigation database; the taxi route never reads it.
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
        var aircraft = new AircraftState
        {
            Callsign = "UPS2951",
            AircraftType = "B752",
            Position = PushedPose,
            TrueHeading = new TrueHeading(PushedHeadingDeg),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "RFD" },
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
            SoloRpoCommandsAllowed = false,
            RpoShowPilotSpeech = false,
            TerminalEmitter = null,
            ArtccConfig = null,
            ScenarioElapsedSeconds = 0,
            SessionStartUtc = DateTime.UnixEpoch,
            PreserveConditionals = false,
            IsScenarioScripted = false,
            FacilityHint = null,
        };
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("TAXI S HS B");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.Dispatch(parsed.Value!, aircraft, ctx);
        Assert.True(result.Success, result.Message);
        return aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException("the TAXI left no route");
    }

    /// <summary>
    /// Poses from the issue's recording: rolling the free-space leg onto S (t=1030), north-west along S (t=1055),
    /// and stopped at the B hold-short (t=1065 on, where it waits — the likeliest moment to hover it).
    /// </summary>
    [Theory]
    [InlineData(37.714259481365524, -122.21646098135605, 286.6, "RAMP")]
    [InlineData(37.714944, -122.217306, 319.0, "S")]
    [InlineData(37.715372, -122.217783, 321.0, "S")]
    public void SingleTaxiwayRoute_OverlayFollowsTheAircraftsHeading_ToTheServersTerminus(
        double lat,
        double lon,
        double headingDeg,
        string currentTaxiway
    )
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        TaxiRoute server = ServerRoute(layout);
        Assert.Equal("S", server.FormatTaxiwaySequence());

        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        vm.SetDomainLayoutForTesting(layout);
        var ac = new AircraftModel
        {
            Callsign = "UPS2951",
            AircraftType = "B752",
            Position = new LatLon(lat, lon),
            Heading = new TrueHeading(headingDeg),
            CurrentTaxiway = currentTaxiway,
            TaxiRoute = server.FormatTaxiwaySequence(),
            AssignedRunway = "",
            TaxiDestination = "",
            HasActiveTaxiRoute = true,
            IsOnGround = true,
        };

        TaxiRoute? drawn = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(drawn);
        Assert.True(
            drawn!.Segments[^1].ToNodeId == server.Segments[^1].ToNodeId,
            $"the overlay ends at #{drawn.Segments[^1].ToNodeId}, the simulation's route at #{server.Segments[^1].ToNodeId} (S/B)"
        );
        double offNoseDeg = GeoMath.AbsBearingDifference(drawn.Segments[0].Edge.DepartureBearing, headingDeg);
        Assert.True(offNoseDeg < 90.0, $"the overlay's first leg leaves {offNoseDeg:F0}° off the aircraft's nose");
    }
}
