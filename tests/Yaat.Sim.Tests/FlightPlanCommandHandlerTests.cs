using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="FlightPlanCommandHandler.TryChangeDestination"/>.
///
/// The handler resolves any user-typed airport identifier (FAA "OAK" or ICAO
/// "KOAK") through <c>NavigationDatabase.TryResolveAirport</c>, writes the
/// canonical ICAO form to <c>aircraft.FlightPlan.Destination</c>, and rejects
/// unknown airports with a clear error so APT no longer silently accepts typos.
/// </summary>
[Collection("NavDbMutator")]
public class FlightPlanCommandHandlerTests
{
    public FlightPlanCommandHandlerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string? initialDestination = null)
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan { Destination = initialDestination ?? "" },
        };
    }

    [Fact]
    public void TryChangeDestination_FaaCode_ResolvesToIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "OAK");

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Equal("Destination changed to KOAK", result.Message);
    }

    [Fact]
    public void TryChangeDestination_Icao_StoresIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "KOAK");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_LowercaseFaa_ResolvesToIcao()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft();

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "oak");

        Assert.True(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Unknown_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "ZZZZ");

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Unknown airport", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZZZZ", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pre-existing destination must not be clobbered by a rejected change.
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Empty_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_Whitespace_Rejects()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "   ");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }

    [Fact]
    public void TryChangeDestination_FixIdent_Rejects()
    {
        // BERKS is a fix, not an airport — mirrors the NavigationDatabase test.
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var aircraft = MakeAircraft(initialDestination: "KSFO");

        var result = FlightPlanCommandHandler.TryChangeDestination(aircraft, "BERKS");

        Assert.False(result.Success);
        Assert.Equal("KSFO", aircraft.FlightPlan.Destination);
    }
}
