using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public sealed class StructuredAdvisoryCommandTests
{
    public StructuredAdvisoryCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Parse_RtisDescriptiveForm_ProducesCanonicalPhrase()
    {
        var result = CommandParser.Parse("RTIS 3 5 W B737 024");

        var command = Assert.IsType<ReportTrafficAdvisoryCommand>(result.Value);
        Assert.Equal("RTIS 3 5 W B737 024", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, 3 o'clock, 5 miles, westbound, Boeing 737, 2,400, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Parse_RtisDescriptiveForm_OptionalAltitude_OmitsAltitude()
    {
        var result = CommandParser.Parse("RTIS 3 5 W B737");

        var command = Assert.IsType<ReportTrafficAdvisoryCommand>(result.Value);
        Assert.Null(command.Details.Altitude);
        Assert.Equal("RTIS 3 5 W B737", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, 3 o'clock, 5 miles, westbound, Boeing 737, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Theory]
    [InlineData("RFIS 11 18", "Field's at your 11 o'clock, 18 miles, report it in sight.")]
    [InlineData("RFIS 11 1", "Field's at your 11 o'clock, 1 mile, report it in sight.")]
    public void Parse_RfisDescriptiveForm_ProducesCanonicalPhrase(string text, string expected)
    {
        var result = CommandParser.Parse(text);

        var command = Assert.IsType<ReportFieldAdvisoryCommand>(result.Value);
        Assert.Equal(text, CommandDescriber.DescribeCommand(command));
        Assert.Equal(expected, CommandDescriber.DescribeNatural(command));
    }

    [Theory]
    [InlineData("SAFAL 12 1", "Traffic alert, 12 o'clock, 1 mile.")]
    [InlineData("SAFAL 12 1 L", "Traffic alert, 12 o'clock, 1 mile, advise you turn left immediately.")]
    [InlineData("SAFAL 12 1 D", "Traffic alert, 12 o'clock, 1 mile, advise you descend immediately.")]
    [InlineData("SAFAL 12 1 R C", "Traffic alert, 12 o'clock, 1 mile, advise you turn right immediately, advise you climb immediately.")]
    [InlineData("SAFAL 12 1 C R", "Traffic alert, 12 o'clock, 1 mile, advise you turn right immediately, advise you climb immediately.")]
    public void Parse_SafetyAlert_ProducesCanonicalPhrase(string text, string expected)
    {
        var result = CommandParser.Parse(text);

        var command = Assert.IsType<SafetyAlertCommand>(result.Value);
        Assert.Equal(expected, CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Parse_Cwt_ProducesCanonicalPhrase()
    {
        var result = CommandParser.Parse("CWT");

        var command = Assert.IsType<WakeAdvisoryCommand>(result.Value);
        Assert.Equal("CWT", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Caution wake turbulence", CommandDescriber.DescribeNatural(command));
    }

    [Theory]
    [InlineData("CLAND CWT", false)]
    [InlineData("CLAND NODEL CWT", true)]
    public void Parse_ClearedToLandCwtSuffix_PreservesNoDelete(string text, bool expectedNoDelete)
    {
        var result = CommandParser.Parse(text);

        var command = Assert.IsType<ClearedToLandCommand>(result.Value);
        Assert.Equal(expectedNoDelete, command.NoDelete);
        Assert.True(command.CautionWakeTurbulence);
        Assert.Equal(text, CommandDescriber.DescribeCommand(command));
    }

    [Theory]
    [InlineData("RTIS 3 5 W")]
    [InlineData("RTIS 13 5 W B737 024")]
    [InlineData("RFIS 11")]
    [InlineData("SAFAL 12 1 L R")]
    public void Parse_InvalidDescriptiveForms_Fail(string text)
    {
        var result = CommandParser.Parse(text);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("RTIS NR 2 C172", "NR", 2, "C172")]
    [InlineData("RTIS NOSE 1 CESSNA", "NOSE", 1, "CESSNA")]
    [InlineData("RTIS TAIL 4 B737", "TAIL", 4, "B737")]
    public void Parse_RtisRelativeForm_ProducesRelativeCommand(string text, string position, int miles, string type)
    {
        var result = CommandParser.Parse(text);

        var command = Assert.IsType<ReportTrafficRelativeCommand>(result.Value);
        Assert.Equal(position, command.Details.Position);
        Assert.Equal(miles, command.Details.Miles);
        Assert.Equal(type, command.Details.AircraftType);
    }

    [Theory]
    [InlineData("RTIS BASE R 2 28R C172", PatternEntryLeg.Base, PatternDirection.Right, 2, "28R", "C172")]
    [InlineData("RTIS DW L 3 28R C172", PatternEntryLeg.Downwind, PatternDirection.Left, 3, "28R", "C172")]
    [InlineData("RTIS XW R 1 8R MOONEY", PatternEntryLeg.Crosswind, PatternDirection.Right, 1, "08R", "MOONEY")]
    public void Parse_RtisPatternForm_ProducesPatternCommand(
        string text,
        PatternEntryLeg leg,
        PatternDirection side,
        int miles,
        string runway,
        string type
    )
    {
        var result = CommandParser.Parse(text);

        var command = Assert.IsType<ReportTrafficPatternCommand>(result.Value);
        Assert.Equal(leg, command.Details.Leg);
        Assert.Equal(side, command.Details.Side);
        Assert.Equal(miles, command.Details.Miles);
        Assert.Equal(runway, command.Details.RunwayId);
        Assert.Equal(type, command.Details.AircraftType);
    }

    [Fact]
    public void Parse_RtisPatternFinal_HasNoSide()
    {
        var result = CommandParser.Parse("RTIS FINAL 2 28R C172");

        var command = Assert.IsType<ReportTrafficPatternCommand>(result.Value);
        Assert.Equal(PatternEntryLeg.Final, command.Details.Leg);
        Assert.Null(command.Details.Side);
        Assert.Equal(2, command.Details.Miles);
        Assert.Equal("28R", command.Details.RunwayId);
    }

    [Fact]
    public void Parse_RtisLandmarkForm_ProducesLandmarkCommand()
    {
        var result = CommandParser.Parse("RTIS OVER VPCOL C172");

        var command = Assert.IsType<ReportTrafficLandmarkCommand>(result.Value);
        Assert.Equal("VPCOL", command.Details.FixName);
        Assert.Equal("C172", command.Details.AircraftType);
    }

    [Fact]
    public void Parse_RtisCallsign_StaysRpoForm()
    {
        var result = CommandParser.Parse("RTIS UAL2");

        var command = Assert.IsType<ReportTrafficInSightCommand>(result.Value);
        Assert.Equal("UAL2", command.TargetCallsign);
    }

    [Theory]
    [InlineData("RTIS NR 2")] // relative missing type
    [InlineData("RTIS NR C172")] // relative miles not numeric
    [InlineData("RTIS BASE 2 28R C172")] // non-final leg missing side
    [InlineData("RTIS BASE X 2 28R C172")] // bad side token
    [InlineData("RTIS FINAL R 2 28R C172")] // final must not carry a side
    [InlineData("RTIS OVER VPCOL")] // landmark missing type
    [InlineData("RTIS OVER")] // landmark missing fix + type
    public void Parse_InvalidVfrForms_Fail(string text)
    {
        var result = CommandParser.Parse(text);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Describe_RtisRelativeForm_RoundTripsCanonicalAndPhrase()
    {
        var command = Assert.IsType<ReportTrafficRelativeCommand>(CommandParser.Parse("RTIS NR 2 CESSNA").Value);

        Assert.Equal("RTIS NR 2 CESSNA", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, off your nose and to the right, 2 miles, a Cessna, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Describe_RtisPatternForm_RoundTripsCanonicalAndPhrase()
    {
        var command = Assert.IsType<ReportTrafficPatternCommand>(CommandParser.Parse("RTIS BASE R 2 28R M20P").Value);

        Assert.Equal("RTIS BASE R 2 28R M20P", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, 2-mile right base for runway 28R, a Mooney, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Describe_RtisPatternFinal_OmitsSide()
    {
        var command = Assert.IsType<ReportTrafficPatternCommand>(CommandParser.Parse("RTIS FINAL 2 28R CESSNA").Value);

        Assert.Equal("RTIS FINAL 2 28R CESSNA", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, 2-mile final for runway 28R, a Cessna, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Describe_RtisLandmarkForm_UsesFixPronunciation()
    {
        var command = Assert.IsType<ReportTrafficLandmarkCommand>(CommandParser.Parse("RTIS OVER VPCOL CESSNA").Value);

        Assert.Equal("RTIS OVER VPCOL CESSNA", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, over Oakland Colliseum, a Cessna, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Describe_RtisRelativeRearForm_UsesSlightlyBehindWording()
    {
        var command = Assert.IsType<ReportTrafficRelativeCommand>(CommandParser.Parse("RTIS RR 4 CESSNA").Value);

        Assert.Equal("RTIS RR 4 CESSNA", CommandDescriber.DescribeCommand(command));
        Assert.Equal("Traffic, off your right, slightly behind, 4 miles, a Cessna, report it in sight.", CommandDescriber.DescribeNatural(command));
    }

    [Fact]
    public void Dispatch_RelativeRtis_ResolvesTargetAndCanAcquireTraffic()
    {
        var ownship = Aircraft("AAL1", "B738", new LatLon(37.0, -122.0), heading: 0, track: 0, altitude: 2400);
        var target = Aircraft(
            "UAL2",
            "C172",
            GeoMath.ProjectPoint(ownship.Position, new TrueHeading(30), 2.0),
            heading: 180,
            track: 180,
            altitude: 2400
        );

        var result = CommandDispatcher.Dispatch(
            new ReportTrafficRelativeCommand(new TrafficRelativeDetails("NR", 2, "C172")),
            ownship,
            TestDispatch.Context(
                Random.Shared,
                findAircraft: cs => cs == target.Callsign ? target : null,
                listAircraft: () => [ownship, target],
                soloTrainingMode: true
            )
        );

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Traffic, off your nose and to the right, 2 miles, ", result.Message);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal(target.Callsign, ownship.Approach.LastReportedTrafficCallsign);
    }

    [Fact]
    public void Dispatch_LandmarkRtis_ResolvesTargetOverLandmark()
    {
        var landmarkPos = NavigationDatabase.Instance.GetFixPosition("VPCOL");
        Assert.NotNull(landmarkPos);
        var landmark = new LatLon(landmarkPos.Value.Lat, landmarkPos.Value.Lon);

        var ownship = Aircraft("AAL1", "B738", GeoMath.ProjectPoint(landmark, new TrueHeading(0), 3.0), heading: 180, track: 180, altitude: 2400);
        var target = Aircraft("N9225L", "C172", GeoMath.ProjectPoint(landmark, new TrueHeading(0), 1.5), heading: 180, track: 180, altitude: 2400);

        var result = CommandDispatcher.Dispatch(
            new ReportTrafficLandmarkCommand(new TrafficLandmarkDetails("VPCOL", "C172")),
            ownship,
            TestDispatch.Context(
                Random.Shared,
                findAircraft: cs => cs == target.Callsign ? target : null,
                listAircraft: () => [ownship, target],
                soloTrainingMode: true
            )
        );

        Assert.True(result.Success, result.Message);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal(target.Callsign, ownship.Approach.LastReportedTrafficCallsign);
    }

    [Fact]
    public void Dispatch_StructuredRtis_ResolvesTargetAndCanAcquireTraffic()
    {
        var ownship = Aircraft("AAL1", "B738", new LatLon(37.0, -122.0), heading: 0, track: 0, altitude: 2400);
        var target = Aircraft(
            "UAL2",
            "B738",
            GeoMath.ProjectPoint(ownship.Position, new TrueHeading(0), 5.0),
            heading: 270,
            track: 270,
            altitude: 2400
        );

        var result = CommandDispatcher.Dispatch(
            new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "W", "B737", 2400)),
            ownship,
            TestDispatch.Context(
                Random.Shared,
                findAircraft: cs => cs == target.Callsign ? target : null,
                listAircraft: () => [ownship, target],
                soloTrainingMode: true
            )
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal("Traffic, 12 o'clock, 5 miles, westbound, Boeing 737, 2,400, report it in sight.", result.Message);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal(target.Callsign, ownship.Approach.LastReportedTrafficCallsign);
    }

    [Fact]
    public void Dispatch_SafetyAlert_ResolvesTargetWithoutTrafficInSight()
    {
        var ownship = Aircraft("AAL1", "B738", new LatLon(37.0, -122.0), heading: 0, track: 0, altitude: 2400);
        var target = Aircraft(
            "UAL2",
            "A320",
            GeoMath.ProjectPoint(ownship.Position, new TrueHeading(0), 1.0),
            heading: 180,
            track: 180,
            altitude: 2400
        );

        var result = CommandDispatcher.Dispatch(
            new SafetyAlertCommand(new SafetyAlertDetails(12, 1, SafetyAlertTurn.Left, null)),
            ownship,
            TestDispatch.Context(Random.Shared, listAircraft: () => [ownship, target], soloTrainingMode: true)
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal("Traffic alert, 12 o'clock, 1 mile, advise you turn left immediately.", result.Message);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
    }

    [Fact]
    public void Dispatch_Cwt_DoesNotSetTrafficInSight()
    {
        var ownship = Aircraft("AAL1", "B738", new LatLon(37.0, -122.0), heading: 0, track: 0, altitude: 2400);

        var result = CommandDispatcher.Dispatch(new WakeAdvisoryCommand(), ownship, TestDispatch.Context(Random.Shared, soloTrainingMode: true));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Caution wake turbulence", result.Message);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        Assert.Null(ownship.Approach.LastReportedTrafficCallsign);
    }

    [Fact]
    public void Dispatch_RpoShortcutsRejectInSoloTraining()
    {
        var ownship = Aircraft("AAL1", "B738", new LatLon(37.0, -122.0), heading: 0, track: 0, altitude: 2400);

        Assert.False(
            CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ownship, TestDispatch.Context(Random.Shared, soloTrainingMode: true)).Success
        );
        Assert.False(
            CommandDispatcher
                .Dispatch(new ReportTrafficInSightCommand("UAL2"), ownship, TestDispatch.Context(Random.Shared, soloTrainingMode: true))
                .Success
        );
        Assert.False(
            CommandDispatcher
                .Dispatch(new ReportFieldInSightForcedCommand(), ownship, TestDispatch.Context(Random.Shared, soloTrainingMode: true))
                .Success
        );
        Assert.False(
            CommandDispatcher
                .Dispatch(new ReportTrafficInSightForcedCommand("UAL2"), ownship, TestDispatch.Context(Random.Shared, soloTrainingMode: true))
                .Success
        );
    }

    private static AircraftState Aircraft(string callsign, string type, LatLon position, double heading, double track, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(track),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = type,
                FlightRules = "IFR",
            },
        };
}
