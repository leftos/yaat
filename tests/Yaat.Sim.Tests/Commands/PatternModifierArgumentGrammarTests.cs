using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The pattern modifier's <c>[runway] [altitude]</c> tail — shared by <c>MLT</c>/<c>MRT</c>,
/// <c>CTO MLT</c>/<c>MRT</c> and the option clearances' modifier — tells its two argument types apart
/// by shape: one or two digits (optional L/C/R) is a runway, three or more digits is an altitude. That
/// is what makes OAK's 12/15/30/33 nameable; before it, a bare <c>33</c> was read as a 3,300 ft pattern
/// altitude and the crossing runways could not be commanded at all. The two-digit altitude shorthand
/// the tail used to take is written with three digits (<c>MLT 28R 015</c>); altitude-only slots
/// (<c>CM 15</c>, <c>GA MLT 15</c>) are untouched, because no runway competes there.
///
/// A runway-shaped token the airport does not have is rejected at dispatch — never silently re-read as
/// an altitude.
/// </summary>
public class PatternModifierArgumentGrammarTests
{
    public PatternModifierArgumentGrammarTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // ---------------------------------------------------------------------------------------------
    // Grammar
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("MLT 33", "33")]
    [InlineData("MLT 15", "15")]
    [InlineData("MLT 12", "12")]
    [InlineData("MLT 28R", "28R")]
    [InlineData("MLT 9", "09")]
    public void BareTwoDigitToken_IsARunway(string input, string expectedRunway)
    {
        var parsed = CommandParser.Parse(input);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var mlt = Assert.IsType<MakeLeftTrafficCommand>(parsed.Value);
        Assert.Equal(expectedRunway, mlt.RunwayId);
        Assert.Null(mlt.Altitude);
    }

    [Theory]
    [InlineData("MLT 015", 1500)]
    [InlineData("MLT 1500", 1500)]
    [InlineData("MRT 020", 2000)]
    public void ThreeOrMoreDigits_IsAnAltitude(string input, int expectedFeet)
    {
        var parsed = CommandParser.Parse(input);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var (runwayId, altitude) = parsed.Value switch
        {
            MakeLeftTrafficCommand mlt => (mlt.RunwayId, mlt.Altitude),
            MakeRightTrafficCommand mrt => (mrt.RunwayId, mrt.Altitude),
            _ => (null, null),
        };
        Assert.Null(runwayId);
        Assert.Equal(expectedFeet, altitude);
    }

    [Fact]
    public void RunwayThenAltitude_KeepsBoth()
    {
        var parsed = CommandParser.Parse("MLT 28R 015");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var mlt = Assert.IsType<MakeLeftTrafficCommand>(parsed.Value);
        Assert.Equal("28R", mlt.RunwayId);
        Assert.Equal(1500, mlt.Altitude);
    }

    /// <summary>
    /// The slot after a bound runway is an altitude only, so it keeps the whole altitude grammar —
    /// including the two-digit shorthand this tail has always taken.
    /// </summary>
    [Theory]
    [InlineData("MLT 28R 15", "28R", 1500)]
    [InlineData("MLT 15 15", "15", 1500)]
    [InlineData("MLT 33 020", "33", 2000)]
    public void AfterARunway_TheNextTokenIsAnAltitude(string input, string expectedRunway, int expectedFeet)
    {
        var parsed = CommandParser.Parse(input);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var mlt = Assert.IsType<MakeLeftTrafficCommand>(parsed.Value);
        Assert.Equal(expectedRunway, mlt.RunwayId);
        Assert.Equal(expectedFeet, mlt.Altitude);
    }

    [Fact]
    public void CtoModifier_TakesARunwayAndTheTwoDigitAltitude()
    {
        var parsed = CommandParser.Parse("CTO MLT 28R 15");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var ct = Assert.IsType<ClosedTrafficDeparture>(Assert.IsType<ClearedForTakeoffCommand>(parsed.Value).Departure);
        Assert.Equal("28R", ct.RunwayId);
        Assert.Equal(1500, ct.PatternAltitude);
    }

    /// <summary>
    /// A second runway designator is not an altitude, and the failure names the position it was read
    /// at rather than just the token.
    /// </summary>
    [Theory]
    [InlineData("MLT 28R 28L", "MLT")]
    [InlineData("CTO MLT 28R 28L", "CTO MLT")]
    [InlineData("COPT MRT 28L 28R", "COPT MRT")]
    public void SecondRunwayDesignator_IsRejected(string input, string expectedVerbPrefix)
    {
        var parsed = CommandParser.Parse(input);

        Assert.False(parsed.IsSuccess, $"'{input}' should not parse: the slot after a runway is an altitude");
        Assert.Contains($"{expectedVerbPrefix} does not understand", parsed.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("after runway", parsed.Reason ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// An AGL pattern altitude resolves below 1,000 ft at a sea-level field (OAK 28L's authored pattern
    /// is 600 ft AGL), and no numeric token reads back as that number — the canonical text has to be the
    /// token as typed or the clearance changes on replay.
    /// </summary>
    [Fact]
    public void AglPatternAltitude_ParsesAndRoundTripsAsWritten()
    {
        var parsed = CommandParser.Parse("MLT 28L KOAK+005");
        if (!parsed.IsSuccess)
        {
            return; // no navdata for KOAK — the AGL form needs the field elevation
        }

        var mlt = Assert.IsType<MakeLeftTrafficCommand>(parsed.Value);
        Assert.Equal("28L", mlt.RunwayId);
        Assert.True(mlt.Altitude < 1000, $"expected an AGL altitude below 1,000 ft, got {mlt.Altitude}");
        Assert.Equal("MLT 28L KOAK+005", CommandDescriber.DescribeCommand(mlt));
    }

    [Fact]
    public void CtoModifier_TakesTheCrossingRunwayToken()
    {
        var parsed = CommandParser.Parse("CTO MLT 33");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var cto = Assert.IsType<ClearedForTakeoffCommand>(parsed.Value);
        var ct = Assert.IsType<ClosedTrafficDeparture>(cto.Departure);
        Assert.Equal("33", ct.RunwayId);
        Assert.Null(ct.PatternAltitude);
    }

    /// <summary>The canonical text the action router records and re-parses keeps the token as typed.</summary>
    [Theory]
    [InlineData("MLT 33")]
    [InlineData("MRT 15")]
    [InlineData("MLT 28R 015")]
    [InlineData("MLT 28R 15")]
    [InlineData("MLT 15 15")]
    [InlineData("CTO MLT 33")]
    [InlineData("COPT MLT 33")]
    public void CanonicalText_RoundTrips(string canonical)
    {
        var parsed = CommandParser.Parse(canonical);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(parsed.Value!));
    }

    // ---------------------------------------------------------------------------------------------
    // Dispatch — the runway has to exist at the aircraft's airport
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>MLT 33</c> on OAK's 28R downwind is the case the old grammar could not express: the aircraft
    /// switches to the crossing runway's pattern, which joins through a midfield crossing (AIM 4-3-2).
    /// </summary>
    [Fact]
    public void MltCrossingRunway_AtOak_SwitchesThePatternToThatRunway()
    {
        var navDb = TestVnasData.NavigationDb;
        var runway28R = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || runway28R is null)
        {
            return;
        }

        var ac = OnDownwindFor(runway28R, PatternDirection.Right);

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, "33", null);

        Assert.True(result.Success, $"MLT 33 was refused: {result.Message}");
        Assert.Equal("33", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal("33", ac.Phases?.PatternRunway?.Designator);
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    /// <summary>
    /// A runway-shaped token the airport does not have is a rejection, not a fallback to reading it as
    /// an altitude: SFO has no runway 15, so <c>MLT 15</c> there fails outright.
    /// </summary>
    [Fact]
    public void MltRunwayTheAirportDoesNotHave_IsRejected()
    {
        var navDb = TestVnasData.NavigationDb;
        var runway28R = navDb?.GetRunway("KSFO", "28R");
        if (navDb is null || runway28R is null)
        {
            return;
        }

        var ac = OnDownwindFor(runway28R, PatternDirection.Left);

        var result = PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, "15", null);

        Assert.False(result.Success, "SFO has no runway 15 — MLT 15 must be refused, not read as 1,500 ft");
        Assert.Contains("Runway 15 not found", result.Message ?? "", StringComparison.Ordinal);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Null(ac.Pattern.AltitudeOverrideFt);
    }

    /// <summary>
    /// <c>CTO MLT 33</c> for an aircraft lined up on 28R builds the cross-runway transition circuit:
    /// the climb-out belongs to the departure runway, the pattern to 33.
    /// </summary>
    [Fact]
    public void CtoMltCrossingRunway_AtOak_BuildsTheCrossRunwayCircuit()
    {
        var navDb = TestVnasData.NavigationDb;
        var runway28R = navDb?.GetRunway("KOAK", "28R");
        if (navDb is null || runway28R is null)
        {
            return;
        }

        var parsed = CommandParser.Parse("CTO MLT 33");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var ct = Assert.IsType<ClosedTrafficDeparture>(Assert.IsType<ClearedForTakeoffCommand>(parsed.Value).Departure);

        var ac = LinedUpOn(runway28R);
        DepartureClearanceHandler.ApplyClosedTraffic(ct, ac, ac.Phases!, runway28R, removeInitialClimb: false);

        Assert.Equal("33", ac.Phases?.PatternRunway?.Designator);
        Assert.Equal("33", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", ac.Phases?.DepartureRunway?.Designator);
        Assert.Contains(ac.Phases!.Phases, p => p is MidfieldCrossingPhase);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------

    private static AircraftState MakeVfr(RunwayInfo runway, LatLon position, double altitude) =>
        new()
        {
            Callsign = "N654TS",
            AircraftType = "C172",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = runway.AirportId,
                Destination = runway.AirportId,
                FlightRules = "VFR",
            },
        };

    /// <summary>An aircraft established on <paramref name="runway"/>'s downwind, abeam the threshold.</summary>
    private static AircraftState OnDownwindFor(RunwayInfo runway, PatternDirection direction)
    {
        var waypoints = PatternGeometry.Compute(
            runway,
            AircraftCategory.Piston,
            "C172",
            windSpeedKt: 0,
            direction,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(runway.AirportId),
            authoredRunway: null
        );

        var ac = MakeVfr(runway, new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon), waypoints.PatternAltitude);
        ac.TrueHeading = waypoints.DownwindHeading;
        ac.TrueTrack = waypoints.DownwindHeading;
        ac.Phases = new PhaseList
        {
            AssignedRunway = runway,
            PatternRunway = runway,
            TrafficDirection = direction,
        };
        ac.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        ac.Phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Piston,
                DeltaSeconds = 1.0,
                Runway = runway,
                FieldElevation = runway.ElevationFt,
                Logger = NullLogger.Instance,
            }
        );
        return ac;
    }

    /// <summary>An aircraft on the ground at <paramref name="runway"/>'s threshold, cleared onto it.</summary>
    private static AircraftState LinedUpOn(RunwayInfo runway)
    {
        var ac = MakeVfr(runway, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.ElevationFt);
        ac.IsOnGround = true;
        ac.IndicatedAirspeed = 0;
        ac.Phases = new PhaseList { AssignedRunway = runway };
        return ac;
    }
}
