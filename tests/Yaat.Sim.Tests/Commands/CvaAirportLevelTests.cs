using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <c>CVA {airport}</c>, the airport-level visual approach (7110.65 7-4-3.c.4 "cleared visual approach to (airport name)"):
/// refused at a towered field, which must name the runway; refused with <c>FOLLOW</c>; at an untowered field the pilot picks
/// the runway, first the visual runway already expected, then the room's active arrival ends, then every end of the field;
/// each set drops ends past the tailwind limit and takes the most headwind (the track breaks ties), or the straight-in end in
/// calm or variable wind, and every candidate is an end the type can land on.
/// </summary>
public class CvaAirportLevelTests
{
    public CvaAirportLevelTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ToweredAirports ZoaAndZla() =>
        ToweredAirports.From([
            TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing"),
            TestArtccConfig.LoadZla() ?? throw new InvalidOperationException("ZLA snapshot missing"),
        ]);

    private static AircraftState MakeArrival(string type, string destination, LatLon position, double trackDeg) =>
        new()
        {
            Callsign = "N123",
            AircraftType = type,
            TrueHeading = new TrueHeading(trackDeg),
            TrueTrack = new TrueHeading(trackDeg),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            Position = position,
            FlightPlan = new AircraftFlightPlan { Destination = destination },
            Approach = { HasReportedFieldInSight = true },
        };

    // Eight miles south-east of Petaluma (O69), an untowered field in ZOA.
    private static AircraftState PetalumaArrival(double trackDeg) => MakeArrival("C172", "O69", new LatLon(38.17, -122.50), trackDeg);

    private static WeatherProfile Wind(double directionMagnetic, double speedKt, bool variable = false) =>
        new()
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = directionMagnetic,
                    Speed = speedKt,
                    Variable = variable,
                },
            ],
        };

    private static CommandResult Dispatch(string text, AircraftState aircraft, ActiveRunways? activeRunways = null, WeatherProfile? weather = null) =>
        CommandDispatcher.Dispatch(
            CommandParser.Parse(text).Value!,
            aircraft,
            TestDispatch.Context(new Random(1), weather: weather, toweredAirports: ZoaAndZla(), activeRunways: activeRunways)
        );

    [Fact]
    public void ToweredField_IsRefused_AskingForTheRunway()
    {
        AircraftState aircraft = MakeArrival("B738", "OAK", new LatLon(37.735, -122.12), 295);

        CommandResult result = Dispatch("CVA OAK", aircraft);

        Assert.False(result.Success);
        Assert.Equal("Unable, say the runway for a visual approach to KOAK", result.Message);
        Assert.Null(aircraft.Phases?.ActiveApproach);
    }

    [Fact]
    public void UntoweredField_WithFollow_IsRefused()
    {
        AircraftState aircraft = PetalumaArrival(290);
        aircraft.Approach.HasReportedTrafficInSight = true;
        aircraft.Approach.LastReportedTrafficCallsign = "N456";

        CommandResult result = Dispatch("CVA O69 FOLLOW N456", aircraft);

        Assert.False(result.Success);
        Assert.Equal("Unable, FOLLOW needs a runway for a visual approach to O69", result.Message);
        Assert.Null(aircraft.Phases?.ActiveApproach);
    }

    /// <summary>
    /// Both ends of Petaluma's runway active, a 12 kt wind down one of them: the pilot lands into the wind (AC 90-66B 11.6),
    /// whichever end suits the track; the other end's 12 kt tailwind is past the 10 kt dry limit.
    /// </summary>
    [Theory]
    [InlineData(110, 290, "29")]
    [InlineData(290, 110, "11")]
    public void UntoweredField_PicksTheActiveArrivalEndIntoTheWind_OverTheOneAlignedWithTheTrack(double trackDeg, double windDeg, string expected)
    {
        AircraftState aircraft = PetalumaArrival(trackDeg);
        ActiveRunways active = ActiveRunways.Empty.With(
            "O69",
            [new ActiveRunway("29", ActiveRunwayUse.Both), new ActiveRunway("11", ActiveRunwayUse.Arrival)]
        );

        CommandResult result = Dispatch("CVA O69", aircraft, active, Wind(windDeg, 12));

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Cleared visual approach to O69, landing runway {expected}", result.Message);
        Assert.Equal($"VIS{expected}", aircraft.Approach.Expected);
        Assert.Equal(expected, aircraft.Procedure.DestinationRunway);
        Assert.Equal($"VIS{expected}", aircraft.Phases?.ActiveApproach?.ApproachId);
    }

    [Fact]
    public void UntoweredField_SkipsADepartureOnlyActiveRunway()
    {
        AircraftState aircraft = PetalumaArrival(290);
        ActiveRunways active = ActiveRunways.Empty.With(
            "O69",
            [new ActiveRunway("29", ActiveRunwayUse.Departure), new ActiveRunway("11", ActiveRunwayUse.Both)]
        );

        CommandResult result = Dispatch("CVA O69", aircraft, active);

        Assert.True(result.Success, result.Message);
        Assert.Equal("11", aircraft.Procedure.DestinationRunway);
    }

    [Theory]
    [InlineData(290, "29")]
    [InlineData(110, "11")]
    public void UntoweredField_WithNoActiveRunway_PicksTheRunwayInUseByWind(double windDeg, string expected)
    {
        AircraftState aircraft = PetalumaArrival(200);

        CommandResult result = Dispatch("CVA", aircraft, weather: Wind(windDeg, 12));

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Cleared visual approach to O69, landing runway {expected}", result.Message);
        Assert.Equal($"VIS{expected}", aircraft.Approach.Expected);
    }

    /// <summary>The only active arrival end has a 12 kt tailwind: it is dropped, and the pilot lands on the end into the wind.</summary>
    [Fact]
    public void UntoweredField_DropsAnActiveEndWithTooMuchTailwind()
    {
        AircraftState aircraft = PetalumaArrival(110);
        ActiveRunways active = ActiveRunways.Empty.With("O69", [new ActiveRunway("11", ActiveRunwayUse.Arrival)]);

        CommandResult result = Dispatch("CVA O69", aircraft, active, Wind(290, 12));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared visual approach to O69, landing runway 29", result.Message);
    }

    /// <summary>Calm or variable wind and no runway in use: the pilot takes the straight-in end, the one aligned with the track.</summary>
    [Theory]
    [InlineData(290, 0, false, "29")]
    [InlineData(110, 0, false, "11")]
    [InlineData(290, 8, true, "29")]
    public void UntoweredField_InCalmOrVariableWind_PicksTheStraightInEnd(double trackDeg, double windKt, bool variable, string expected)
    {
        AircraftState aircraft = PetalumaArrival(trackDeg);

        CommandResult result = Dispatch("CVA O69", aircraft, weather: Wind(110, windKt, variable));

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Cleared visual approach to O69, landing runway {expected}", result.Message);
    }

    /// <summary>
    /// Longville (XVG), untowered, one runway: 13 has 3,109 ft to land on, short for a Dash 8-100 (3,395 ft needed), and 31
    /// has 3,549 ft. The 8 kt wind favours 13; the pilot lands on 31 (8 kt tailwind, inside the limit) rather than refusing.
    /// </summary>
    [Fact]
    public void UntoweredField_WindEndNotLandable_PicksTheOtherEnd()
    {
        AircraftState aircraft = MakeArrival("DH8A", "XVG", new LatLon(46.90, -94.10), 315);

        CommandResult result = Dispatch("CVA XVG", aircraft, weather: Wind(130, 8));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared visual approach to KXVG, landing runway 31", result.Message);
    }

    [Fact]
    public void UntoweredField_TakesTheExpectedVisualRunwayFirst()
    {
        AircraftState aircraft = PetalumaArrival(290);
        Assert.True(Dispatch("EAPP VIS11", aircraft).Success);

        CommandResult result = Dispatch("CVA O69", aircraft, weather: Wind(290, 12));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared visual approach to O69, landing runway 11", result.Message);
    }

    /// <summary>
    /// The pilot reads back the airport-level clearance with the runway it will land on as its own intention ("landing
    /// runway"), never as a runway ATC assigned, and the field by its spoken name; a pattern side is the pilot's intention too.
    /// </summary>
    [Theory]
    [InlineData("CVA O69", "")]
    [InlineData("CVA O69 RIGHT", ", right traffic")]
    public void UntoweredField_ReadsBackTheAirportAndTheLandingRunway(string command, string traffic)
    {
        AircraftState aircraft = PetalumaArrival(200);

        CommandResult result = Dispatch(command, aircraft, weather: Wind(290, 12));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal($"cleared visual approach to O69, landing runway 29{traffic}", result.PilotReadback.Speech.Terminal);
        Assert.Equal($"cleared visual approach to Petaluma, landing runway two nine{traffic}", result.PilotReadback.Speech.Tts);
    }

    /// <summary>
    /// Byron (C83), untowered: the wind favours 5, on the 3,000 ft runway a Dash 8-100 cannot land on, so its pilot takes
    /// the longer runway's end most nearly into the wind; a Cessna 172 takes 5.
    /// </summary>
    [Theory]
    [InlineData("C172", "5")]
    [InlineData("DH8A", "12")]
    public void UntoweredField_PicksOnlyARunwayTheTypeCanLandOn(string type, string expected)
    {
        AircraftState aircraft = MakeArrival(type, "C83", new LatLon(37.75, -121.55), 320);

        CommandResult result = Dispatch("CVA C83", aircraft, weather: Wind(50, 12));

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Cleared visual approach to C83, landing runway {expected}", result.Message);
    }
}
