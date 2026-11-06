using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// LAHSO eligibility: the JO 7110.118C para 14.b Table 1 group minima against the available landing distance, and the
/// pilot's decline of a hold-short the type cannot meet (a plain landing clearance stands).
/// </summary>
public class LahsoEligibilityTests(ITestOutputHelper output)
{
    private static readonly TestAirportGroundData GroundData = new();

    [Theory]
    [InlineData(1, 0, 2500)]
    [InlineData(1, 999, 2500)]
    [InlineData(1, 1000, 2550)]
    [InlineData(2, 7000, 3500)]
    [InlineData(2, 6999, 3300)]
    [InlineData(6, 1500, 5100)]
    [InlineData(9, 500, 8000)]
    [InlineData(8, 12000, 7700)]
    public void RequiredDistance_MatchesPublishedTable(int group, double fieldElevationFt, int expectedFt) =>
        Assert.Equal(expectedFt, LahsoGroupMinima.RequiredDistanceFt(group, fieldElevationFt));

    [Fact]
    public void RequiredDistance_Group10_HasNoFigure() => Assert.Null(LahsoGroupMinima.RequiredDistanceFt(10, 0));

    [Theory]
    [InlineData("C172", 2600, 13, true)]
    [InlineData("C172", 2500, 13, true)]
    [InlineData("C172", 2400, 13, false)]
    [InlineData("B738", 7000, 13, true)]
    [InlineData("B738", 6999, 13, false)]
    [InlineData("B744", 12000, 13, false)]
    [InlineData("B763", 8200, 500, true)]
    [InlineData("B763", 8200, 1500, false)]
    [InlineData("TBM9", 12000, 13, false)]
    [InlineData("ZZZZ", 12000, 13, false)]
    [InlineData("R44", 12000, 13, false)]
    public void Judge_DecidesEligibility(string aircraftType, double availableFt, double fieldElevationFt, bool expectedEligible)
    {
        TestVnasData.EnsureInitialized();

        LahsoEligibility verdict = LahsoGroupMinima.Judge(aircraftType, availableFt, fieldElevationFt);

        output.WriteLine($"{aircraftType} ALD {availableFt} elev {fieldElevationFt}: {verdict.Eligible} ({verdict.Reason})");
        Assert.Equal(expectedEligible, verdict.Eligible);
        Assert.Equal(expectedEligible, verdict.Reason is null);
    }

    [Fact]
    public void Judge_DeclineReasons_NameTheCause()
    {
        TestVnasData.EnsureInitialized();

        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize("R44"));
        Assert.Equal(9, FaaAircraftDatabase.Get("B763")?.LahsoGroup);
        Assert.Equal(10, FaaAircraftDatabase.Get("B744")?.LahsoGroup);
        Assert.Null(FaaAircraftDatabase.Get("TBM9")?.LahsoGroup);

        Assert.Equal("C172 needs 2,500 ft, 2,400 ft available", LahsoGroupMinima.Judge("C172", 2400, 13).Reason);
        Assert.Equal("B744 needs a letter of authorization", LahsoGroupMinima.Judge("B744", 12000, 13).Reason);
        Assert.Equal("B763 needs a letter of authorization", LahsoGroupMinima.Judge("B763", 9000, 1500).Reason);
        Assert.Equal("TBM9 has no LAHSO group", LahsoGroupMinima.Judge("TBM9", 12000, 13).Reason);
        Assert.Equal("ZZZZ is not in the FAA aircraft data", LahsoGroupMinima.Judge("ZZZZ", 12000, 13).Reason);
        Assert.Equal("R44 is a helicopter", LahsoGroupMinima.Judge("R44", 12000, 13).Reason);
    }

    [Fact]
    public void Lahso_C172OnSfo28R_HoldsShortOf1L()
    {
        (AircraftState aircraft, CommandResult result)? run = DispatchLahso("C172", 90);
        if (run is null)
        {
            return;
        }

        (AircraftState aircraft, CommandResult result) = run.Value;
        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared to land, Runway 28R, hold short runway 1L", result.Message);
        Assert.NotNull(aircraft.Phases!.LahsoHoldShort);
        Assert.Equal("1L", aircraft.Phases.LahsoHoldShort.CrossingRunwayId);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
    }

    [Fact]
    public void Lahso_B738OnSfo28R_DeclinesForDistance()
    {
        (AircraftState aircraft, CommandResult result)? run = DispatchLahso("B738", 145);
        if (run is null)
        {
            return;
        }

        (AircraftState aircraft, CommandResult result) = run.Value;
        Assert.True(result.Success, result.Message);
        Assert.Null(aircraft.Phases!.LahsoHoldShort);
        Assert.Equal("Cleared to land, Runway 28R; unable to hold short runway 1L: B738 needs 7,000 ft, 5,601 ft available", result.Message);
    }

    /// <summary>
    /// An accepted hold-short followed by one the crew declines: the decline leaves a plain landing clearance, so the earlier
    /// target must go with it.
    /// </summary>
    [Fact]
    public void Lahso_AcceptedThenDeclined_ClearsTheEarlierTarget()
    {
        // An A320 (group 7, 6,000 ft) fits the longer distance to 1R but not the 5,601 ft to 1L.
        (AircraftState aircraft, CommandResult result)? run = DispatchLahso("A320", 135, crossingRunway: "1R");
        if (run is null)
        {
            return;
        }

        (AircraftState aircraft, CommandResult accepted) = run.Value;
        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal("1R", aircraft.Phases!.LahsoHoldShort?.CrossingRunwayId);

        CommandResult declined = Dispatch(aircraft, "LAHSO 1L");
        Assert.True(declined.Success, declined.Message);
        Assert.Equal("Cleared to land, Runway 28R; unable to hold short runway 1L: A320 needs 6,000 ft, 5,601 ft available", declined.Message);
        Assert.Null(aircraft.Phases.LahsoHoldShort);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
    }

    [Fact]
    public void GroupDeclineReason_GroupOutsideTable_IsNoGroup() =>
        Assert.Equal("XXXX has no LAHSO group", LahsoGroupMinima.GroupDeclineReason("XXXX", 11, 12000, 13));

    [Fact]
    public void Lahso_B744OnSfo28R_DeclinesAndKeepsPlainLandingClearance()
    {
        (AircraftState aircraft, CommandResult result)? run = DispatchLahso("B744", 150);
        if (run is null)
        {
            return;
        }

        (AircraftState aircraft, CommandResult result) = run.Value;
        Assert.True(result.Success, result.Message);
        Assert.Null(aircraft.Phases!.LahsoHoldShort);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Equal("28R", aircraft.Phases.ClearedRunwayId);
        Assert.Equal("Cleared to land, Runway 28R; unable to hold short runway 1L: B744 needs a letter of authorization", result.Message);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal(CanonicalCommandType.LandAndHoldShort, result.PilotReadback.Command);
        Assert.Equal("cleared to land runway 28R, unable to hold short of runway 1L", result.PilotReadback.Speech.Terminal);
        Assert.Equal("cleared to land runway two eight right, unable to hold short of runway one left", result.PilotReadback.Speech.Tts);
    }

    /// <summary>
    /// Spawns <paramref name="aircraftType"/> on 1 nm final for SFO 28R, as <c>LahsoRolloutTests</c> does, and dispatches
    /// <c>LAHSO {crossingRunway}</c>. Null when the layout or nav data is absent (silent skip).
    /// </summary>
    private (AircraftState Aircraft, CommandResult Result)? DispatchLahso(string aircraftType, double approachSpeedKts, string crossingRunway = "1L")
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("LahsoGroupMinima", LogLevel.Debug).InitializeSimLog();

        NavigationDatabase navDb = NavigationDatabase.Instance;
        RunwayInfo? runway28R = navDb.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);
        PrintAvailableLandingDistance(navDb, layout, runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            TrueTrack = runway28R.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(
                GeoMath.DistanceNm(new LatLon(acLat, acLon), LandingThreshold.Resolve(runway28R, layout)),
                runway28R.ElevationFt,
                AircraftCategorization.Categorize(aircraftType)
            ),
            IndicatedAirspeed = approachSpeedKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };

        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        return (aircraft, Dispatch(aircraft, $"LAHSO {crossingRunway}"));
    }

    /// <summary>Dispatches <paramref name="text"/> to <paramref name="aircraft"/> against the SFO layout.</summary>
    private CommandResult Dispatch(AircraftState aircraft, string text)
    {
        ParsedCommand command = CommandParser.Parse(text).Value!;
        DispatchContext ctx = TestDispatch.Context(new Random(1), groundLayout: GroundData.GetLayout("SFO"));
        CommandResult result = CommandDispatcher.Dispatch(command, aircraft, ctx);
        output.WriteLine($"{aircraft.AircraftType} {text}: {result.Success} {result.Message}");
        return result;
    }

    /// <summary>The available landing distance for SFO 28R holding short of 1L, measured as the LAHSO command measures it.</summary>
    private void PrintAvailableLandingDistance(NavigationDatabase navDb, AirportGroundLayout layout, RunwayInfo runway28R)
    {
        GroundRunway? landing = layout.FindGroundRunway("28R");
        GroundRunway? crossing = layout.FindGroundRunway("1L");
        Assert.NotNull(landing);
        Assert.NotNull(crossing);
        (double Lat, double Lon, double DistFromStartNm)? intersection = RunwayIntersectionCalculator.FindIntersection(landing, crossing);
        Assert.NotNull(intersection);
        double holdShortFt =
            RunwayIntersectionCalculator.ComputeHoldShortDistanceNm(intersection.Value.DistFromStartNm, "28R", landing, crossing.WidthFt)
            * GeoMath.FeetPerNm;
        double ldaFt = RunwayLandability.LandingDistanceAvailableFt(navDb, runway28R);
        output.WriteLine(
            $"SFO 28R/1L: hold-short {holdShortFt:F0} ft, LDA {ldaFt:F0} ft, ALD {Math.Min(holdShortFt, ldaFt):F0} ft, "
                + $"field elevation {runway28R.AirportElevationFt:F0} ft"
        );
    }
}
