using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>CIFR</c> semantics, plus the invariant that the dispatcher does <em>not</em> gate commands on
/// flight rules. Since issue #317 the VFR-only restriction is a controller preference the desktop
/// client enforces before a command reaches the wire — see <see cref="VfrCommandPolicy"/> and
/// <c>VfrCommandPolicyTests</c> for the classification itself.
/// </summary>
[Collection("NavDbMutator")]
public class VfrCommandGatingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public VfrCommandGatingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(
            TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 13))
        );
    }

    public void Dispose() => _navDbScope.Dispose();

    private static AircraftState MakeIfrAircraft()
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
    }

    private static AircraftState MakeVfrAircraft()
    {
        var ac = MakeIfrAircraft();
        ac.Callsign = "N805FM";
        ac.AircraftType = "C172";
        ac.FlightPlan.Altitude = PlannedAltitude.Vfr(3500);
        ac.IndicatedAirspeed = 120;
        ac.FlightPlan.FlightRules = "VFR";
        return ac;
    }

    private CommandResult Dispatch(AircraftState aircraft, ParsedCommand command)
    {
        return CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));
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
    public void IfrAircraft_VfrCommand_NotGatedByTheDispatcher(string commandText)
    {
        var ac = MakeIfrAircraft();

        var parseResult = CommandParser.ParseCompound(commandText, ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed for '{commandText}': {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

        _output.WriteLine($"{commandText}: Success={result.Success} Message={result.Message}");

        // The command may still fail for a real reason (no runway, wrong phase); what must not
        // happen any more is a flight-rules refusal telling the controller to cancel IFR first.
        if (!result.Success)
        {
            Assert.DoesNotContain("CIFR", result.Message!);
        }
    }

    /// <summary>
    /// The commands above are still classified VFR-only — the client refuses them for an IFR
    /// aircraft unless the controller has opted in. <c>EF</c> is the one the default setting
    /// (<see cref="VfrCommandsForIfr.EnterFinalOnly"/>) lets through.
    /// </summary>
    [Theory]
    [InlineData("ERD 28R", false)]
    [InlineData("EF 28R", true)]
    [InlineData("MLT", false)]
    [InlineData("TG", false)]
    public void IfrAircraft_VfrCommand_GatedByPolicyInsteadOfDispatcher(string commandText, bool allowedByDefault)
    {
        var parseResult = CommandParser.ParseCompound(commandText);
        Assert.True(parseResult.IsSuccess, $"Parse failed for '{commandText}': {parseResult.Reason}");
        var parsed = Assert.Single(Assert.Single(parseResult.Value!.Blocks).Commands);

        Assert.True(VfrCommandPolicy.IsVfrOnly(parsed));
        Assert.False(VfrCommandPolicy.AllowsForIfr(parsed, VfrCommandsForIfr.None));
        Assert.Equal(allowedByDefault, VfrCommandPolicy.AllowsForIfr(parsed, VfrCommandsForIfr.EnterFinalOnly));
        Assert.True(VfrCommandPolicy.AllowsForIfr(parsed, VfrCommandsForIfr.All));
    }

    [Theory]
    [InlineData("CVA 28R")]
    [InlineData("RFIS")]
    [InlineData("RTIS")]
    public void IfrAircraft_NonGatedCommand_NotRejectedForFlightRules(string commandText)
    {
        var ac = MakeIfrAircraft();

        var parseResult = CommandParser.ParseCompound(commandText, ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed for '{commandText}': {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

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
        Assert.False(ac.FlightPlan.IsVfr);
        Assert.Equal(35000, ac.FlightPlan.Altitude.CruiseFeet);

        var result = Dispatch(ac, new CancelIfrCommand());

        _output.WriteLine($"CIFR: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success);
        Assert.True(ac.FlightPlan.IsVfr);
        Assert.Equal("VFR", ac.FlightPlan.FlightRules);
        Assert.Null(ac.FlightPlan.Altitude.CruiseFeet);
    }

    [Fact]
    public void Cifr_VfrAircraft_Rejected()
    {
        var ac = MakeVfrAircraft();
        Assert.True(ac.FlightPlan.IsVfr);

        var result = Dispatch(ac, new CancelIfrCommand());

        _output.WriteLine($"CIFR on VFR: Success={result.Success} Message={result.Message}");

        Assert.False(result.Success);
        Assert.Contains("already VFR", result.Message!);
    }

    [Fact]
    public void Cifr_ThenPatternEntry_NotBlockedByFlightRules()
    {
        var ac = MakeIfrAircraft();

        // Cancel IFR
        var cifrResult = Dispatch(ac, new CancelIfrCommand());
        Assert.True(cifrResult.Success);
        Assert.True(ac.FlightPlan.IsVfr);

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
