using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class VfrCommandGatingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public VfrCommandGatingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 13)));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static AircraftState MakeIfrAircraft()
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = 37.72,
            Longitude = -122.22,
            TrueHeading = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            Departure = "KSFO",
            CruiseAltitude = 35000,
            FlightRules = "IFR",
        };
    }

    private static AircraftState MakeVfrAircraft()
    {
        var ac = MakeIfrAircraft();
        ac.Callsign = "N805FM";
        ac.AircraftType = "C172";
        ac.CruiseAltitude = 3500;
        ac.IndicatedAirspeed = 120;
        ac.FlightRules = "VFR";
        return ac;
    }

    private CommandResult Dispatch(AircraftState aircraft, ParsedCommand command)
    {
        return CommandDispatcher.Dispatch(command, aircraft, null, new Random(0), false);
    }

    [Theory]
    [InlineData("ERD 28R")]
    [InlineData("ELD 28R")]
    [InlineData("ELC 28R")]
    [InlineData("ERC 28R")]
    [InlineData("ELB 28R")]
    [InlineData("ERB 28R")]
    [InlineData("EF 28R")]
    [InlineData("MLT")]
    [InlineData("MRT")]
    [InlineData("TC")]
    [InlineData("TD")]
    [InlineData("TB")]
    [InlineData("EXT")]
    [InlineData("SA")]
    [InlineData("MNA")]
    [InlineData("L360")]
    [InlineData("R360")]
    [InlineData("L270")]
    [InlineData("R270")]
    [InlineData("CA")]
    [InlineData("PS 1.5")]
    [InlineData("MLS")]
    [InlineData("MRS")]
    [InlineData("P270")]
    [InlineData("NO270")]
    [InlineData("TG")]
    [InlineData("SG")]
    [InlineData("LA")]
    [InlineData("COPT")]
    [InlineData("HPPL")]
    [InlineData("HPPR")]
    [InlineData("HPP")]
    public void IfrAircraft_VfrCommand_Rejected(string commandText)
    {
        var ac = MakeIfrAircraft();

        var parseResult = CommandParser.ParseCompound(commandText, ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed for '{commandText}': {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, null, new Random(0), false);

        _output.WriteLine($"{commandText}: Success={result.Success} Message={result.Message}");

        Assert.False(result.Success, $"IFR aircraft should reject '{commandText}'");
        Assert.Contains("CIFR", result.Message!);
    }

    [Theory]
    [InlineData("CVA 28R")]
    [InlineData("RFIS")]
    [InlineData("RTIS")]
    public void IfrAircraft_NonGatedCommand_NotRejectedForFlightRules(string commandText)
    {
        var ac = MakeIfrAircraft();

        var parseResult = CommandParser.ParseCompound(commandText, ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed for '{commandText}': {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, null, new Random(0), false);

        _output.WriteLine($"{commandText}: Success={result.Success} Message={result.Message}");

        // These may fail for other reasons (no runway, no field in sight, etc.)
        // but should NOT fail with the VFR gating message
        if (!result.Success)
        {
            Assert.DoesNotContain("CIFR", result.Message!);
        }
    }

    [Fact]
    public void Cifr_IfrAircraft_BecomesVfr()
    {
        var ac = MakeIfrAircraft();
        Assert.False(ac.IsVfr);
        Assert.Equal(35000, ac.CruiseAltitude);

        var result = Dispatch(ac, new CancelIfrCommand());

        _output.WriteLine($"CIFR: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success);
        Assert.True(ac.IsVfr);
        Assert.Equal("VFR", ac.FlightRules);
        Assert.Equal(0, ac.CruiseAltitude);
    }

    [Fact]
    public void Cifr_VfrAircraft_Rejected()
    {
        var ac = MakeVfrAircraft();
        Assert.True(ac.IsVfr);

        var result = Dispatch(ac, new CancelIfrCommand());

        _output.WriteLine($"CIFR on VFR: Success={result.Success} Message={result.Message}");

        Assert.False(result.Success);
        Assert.Contains("already VFR", result.Message!);
    }

    [Fact]
    public void Cifr_ThenPatternEntry_NotBlockedByFlightRules()
    {
        var ac = MakeIfrAircraft();

        // First: pattern entry should be rejected for flight rules
        var erdResult = Dispatch(ac, new EnterRightDownwindCommand("28R"));
        Assert.False(erdResult.Success);
        Assert.Contains("CIFR", erdResult.Message!);

        // Cancel IFR
        var cifrResult = Dispatch(ac, new CancelIfrCommand());
        Assert.True(cifrResult.Success);
        Assert.True(ac.IsVfr);

        // Now pattern entry should not be rejected for flight rules
        // (will fail for other reasons like no navdata, but NOT for VFR gating)
        var erdResult2 = Dispatch(ac, new EnterRightDownwindCommand("28R"));

        _output.WriteLine($"ERD after CIFR: Success={erdResult2.Success} Message={erdResult2.Message}");

        if (!erdResult2.Success)
        {
            Assert.DoesNotContain("CIFR", erdResult2.Message!);
        }
    }

    [Fact]
    public void Cifr_ParsesCorrectly()
    {
        var result = CommandParser.ParseCompound("CIFR");
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.Single(result.Value!.Blocks[0].Commands);
        Assert.IsType<CancelIfrCommand>(result.Value!.Blocks[0].Commands[0]);
    }
}
