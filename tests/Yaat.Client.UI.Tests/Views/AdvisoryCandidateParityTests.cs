using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The client's traffic-advisory candidates (<see cref="AdvisoryCandidates"/>), built from an aircraft model fed the training-hub
/// DTO the server builds from its <see cref="AircraftState"/>, equal the candidates the sim's own dispatch matches against
/// (<see cref="TrafficAdvisoryCandidate.From"/>), so the live check and Enter run the matcher over the same values.
/// </summary>
public class AdvisoryCandidateParityTests
{
    [Fact]
    public void ClientCandidate_MatchesServerCandidate_OnATurningAircraft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("ATL", "08R") ?? throw new InvalidOperationException("ATL 08R not in navdata");
        AircraftState turning = Aircraft("N436MS", new LatLon(33.70, -84.55), bankDeg: 20);
        AircraftState onBase = PatternAircraft("N111", runway);

        TrafficAdvisoryCandidate turningClient = AdvisoryCandidates.From(AircraftModel.FromDto(DtoOf(turning)));
        TrafficAdvisoryCandidate onBaseClient = AdvisoryCandidates.From(AircraftModel.FromDto(DtoOf(onBase)));

        Assert.Equal(TrafficAdvisoryCandidate.From(turning), turningClient);
        Assert.True(turningClient.IsManeuvering);
        Assert.Null(turningClient.AssignedRunwayId);
        Assert.Equal(TrafficAdvisoryCandidate.From(onBase), onBaseClient);
        Assert.Equal("08R", onBaseClient.AssignedRunwayId);
        Assert.Equal(PatternEntryLeg.Base, onBaseClient.PatternPosition?.Leg);
    }

    /// <summary>The training-hub DTO for <paramref name="state"/>, with the fields the server's DTO converter sets from it.</summary>
    private static AircraftDto DtoOf(AircraftState state)
    {
        PatternPosition? pattern = TrafficAdvisoryCandidate.PatternPositionOf(state);
        return new AircraftDto(
            Callsign: state.Callsign,
            AircraftType: state.AircraftType,
            Latitude: state.Position.Lat,
            Longitude: state.Position.Lon,
            Heading: state.TrueHeading.Degrees,
            Altitude: state.Altitude,
            GroundSpeed: 90,
            BeaconCode: 1200,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "KATL",
            Destination: "KATL",
            Route: "",
            FlightRules: "VFR",
            Status: "",
            AssignedRunway: state.Phases?.AssignedRunway?.Designator ?? "",
            TrueTrack: state.TrueTrack.Degrees,
            IsManeuvering: TrafficAdvisoryMatcher.IsManeuvering(state.BankAngle),
            PatternLeg: pattern?.Leg,
            PatternSide: pattern?.Side,
            PatternDistanceNm: pattern?.DistanceNm
        );
    }

    private static AircraftState PatternAircraft(string callsign, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        AircraftState ac = Aircraft(callsign, GeoMath.ProjectPoint(threshold, new TrueHeading(30), 2.0), bankDeg: 0);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(
            new BasePhase
            {
                Waypoints = PatternGeometry.Compute(
                    runway,
                    AircraftCategory.Piston,
                    "",
                    0,
                    PatternDirection.Left,
                    null,
                    null,
                    null,
                    authoredRunway: null,
                    activeRunways: ActiveRunways.Empty,
                    commanded: PatternCommanded.None
                ),
            }
        );
        return ac;
    }

    private static AircraftState Aircraft(string callsign, LatLon position, double bankDeg) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(100),
            TrueTrack = new TrueHeading(95),
            Altitude = 2000,
            IndicatedAirspeed = 90,
            BankAngle = bankDeg,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
}
