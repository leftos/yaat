using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The matcher's plain input record: <see cref="TrafficAdvisoryCandidate.From"/> reads exactly what the server sends on the
/// training-hub wire (the pattern position, the maneuvering flag), and a candidate built by hand from the same field values
/// resolves the same way, which is what lets the client run the matcher over its own aircraft list.
/// </summary>
public sealed class TrafficAdvisoryCandidateTests
{
    private const string Airport = "OAK";
    private const string Designator = "28R";

    public TrafficAdvisoryCandidateTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void From_OnARightBase_CarriesTheClassifiedPatternPosition()
    {
        RunwayInfo runway = OakRunway();
        AircraftState onBase = PatternAircraft("N111", runway, MakeBasePhase(runway, PatternDirection.Right), distanceNm: 2.0);

        var candidate = TrafficAdvisoryCandidate.From(onBase);

        Assert.NotNull(candidate.PatternPosition);
        Assert.Equal(PatternCommandHandler.ClassifyPatternPosition(onBase, runway), candidate.PatternPosition);
        Assert.Equal(PatternEntryLeg.Base, candidate.PatternPosition.Leg);
        Assert.Equal(PatternDirection.Right, candidate.PatternPosition.Side);
        Assert.Equal(Designator, candidate.AssignedRunwayId);
    }

    [Fact]
    public void From_OnFinal_HasNoSide()
    {
        RunwayInfo runway = OakRunway();
        AircraftState onFinal = PatternAircraft("N222", runway, new FinalApproachPhase(), distanceNm: 4.0);

        var candidate = TrafficAdvisoryCandidate.From(onFinal);

        Assert.Equal(PatternCommandHandler.ClassifyPatternPosition(onFinal, runway), candidate.PatternPosition);
        Assert.Equal(PatternEntryLeg.Final, candidate.PatternPosition?.Leg);
        Assert.Null(candidate.PatternPosition?.Side);
    }

    [Fact]
    public void From_OutsideAPattern_HasNoPatternPosition()
    {
        var candidate = TrafficAdvisoryCandidate.From(Aircraft("N333", new LatLon(37.75, -122.30), bankDeg: 0));

        Assert.Null(candidate.PatternPosition);
        Assert.Null(candidate.AssignedRunwayId);
    }

    [Theory]
    [InlineData(8.0, true)]
    [InlineData(-8.0, true)]
    [InlineData(6.0, false)]
    [InlineData(-6.0, false)]
    public void IsManeuvering_PastSevenDegreesOfBank(double bankDeg, bool expected)
    {
        Assert.Equal(expected, TrafficAdvisoryMatcher.IsManeuvering(bankDeg));
        Assert.Equal(expected, TrafficAdvisoryCandidate.From(Aircraft("N444", new LatLon(37.75, -122.30), bankDeg)).IsManeuvering);
    }

    [Fact]
    public void StructuredResolver_HandBuiltCandidates_AgreeWithFrom()
    {
        AircraftState recipient = Aircraft("N436MS", new LatLon(37.75, -122.30), bankDeg: 13);
        AircraftState near = Aircraft("N9225L", GeoMath.ProjectPoint(recipient.Position, new TrueHeading(240), 3.0), bankDeg: 0);
        AircraftState far = Aircraft("N346G", GeoMath.ProjectPoint(recipient.Position, new TrueHeading(150), 4.0), bankDeg: 0);
        var details = new TrafficAdvisoryDetails(4, 3, "E", "C172", 2000);

        TrafficAdvisoryTargetMatch? fromState = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            TrafficAdvisoryCandidate.From(recipient),
            details,
            [.. new[] { recipient, near, far }.Select(TrafficAdvisoryCandidate.From)],
            out string fromStateError
        );
        TrafficAdvisoryTargetMatch? byHand = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            ByHand(recipient, pattern: null),
            details,
            [ByHand(recipient, pattern: null), ByHand(near, pattern: null), ByHand(far, pattern: null)],
            out string byHandError
        );

        Assert.NotNull(fromState);
        Assert.Equal(fromState, byHand);
        Assert.Equal(fromStateError, byHandError);
    }

    [Fact]
    public void PatternResolver_HandBuiltCandidates_AgreeWithFrom()
    {
        RunwayInfo runway = OakRunway();
        AircraftState recipient = Aircraft("N436MS", new LatLon(37.75, -122.30), bankDeg: 0);
        AircraftState onBase = PatternAircraft("N111", runway, MakeBasePhase(runway, PatternDirection.Right), distanceNm: 2.0);
        AircraftState onFinal = PatternAircraft("N222", runway, new FinalApproachPhase(), distanceNm: 5.0);
        var details = new TrafficPatternDetails(PatternEntryLeg.Base, PatternDirection.Right, 2, Designator, "C172");

        TrafficAdvisoryTargetMatch? fromState = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
            TrafficAdvisoryCandidate.From(recipient),
            details,
            [.. new[] { recipient, onBase, onFinal }.Select(TrafficAdvisoryCandidate.From)],
            out string fromStateError
        );
        TrafficAdvisoryTargetMatch? byHand = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
            ByHand(recipient, pattern: null),
            details,
            [
                ByHand(recipient, pattern: null),
                ByHand(onBase, PatternCommandHandler.ClassifyPatternPosition(onBase, runway)),
                ByHand(onFinal, PatternCommandHandler.ClassifyPatternPosition(onFinal, runway)),
            ],
            out string byHandError
        );

        Assert.NotNull(fromState);
        Assert.Equal("N111", fromState.Target.Callsign);
        Assert.Equal(fromState, byHand);
        Assert.Equal(fromStateError, byHandError);
    }

    /// <summary>A candidate built field by field, as the client builds one from the training-hub aircraft DTO.</summary>
    private static TrafficAdvisoryCandidate ByHand(AircraftState aircraft, PatternPosition? pattern) =>
        new(
            aircraft.Callsign,
            new LatLon(aircraft.Position.Lat, aircraft.Position.Lon),
            aircraft.TrueHeading.Degrees,
            aircraft.TrueTrack.Degrees,
            aircraft.Altitude,
            aircraft.AircraftType,
            AircraftState.StripTypePrefix(aircraft.AircraftType),
            TrafficAdvisoryMatcher.IsManeuvering(aircraft.BankAngle),
            aircraft.Phases?.AssignedRunway?.Designator,
            pattern
        );

    private static RunwayInfo OakRunway() =>
        NavigationDatabase.Instance.GetRunway(Airport, Designator) ?? throw new InvalidOperationException($"{Airport} {Designator} not in navdata");

    private static BasePhase MakeBasePhase(RunwayInfo runway, PatternDirection side) =>
        new()
        {
            Waypoints = PatternGeometry.Compute(
                runway,
                AircraftCategory.Piston,
                "",
                0,
                side,
                null,
                null,
                null,
                authoredRunway: null,
                activeRunways: ActiveRunways.Empty,
                commanded: PatternCommanded.None
            ),
        };

    private static AircraftState PatternAircraft(string callsign, RunwayInfo runway, Phase phase, double distanceNm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        AircraftState ac = Aircraft(callsign, GeoMath.ProjectPoint(threshold, new TrueHeading(60), distanceNm), bankDeg: 0);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(phase);
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
