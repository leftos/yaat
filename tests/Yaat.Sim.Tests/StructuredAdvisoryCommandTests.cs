using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
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
