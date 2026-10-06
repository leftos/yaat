using Xunit;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// The per-callsign FNV-1a draw and the fixed per-aircraft values it drives. Each expectation is a literal computed once
/// from the published FNV-1a 32 constants, so a change to the hash, a salt or a range shows up here.
/// </summary>
public class DeterministicHashTests
{
    [Fact]
    public void EmptySalt_IsThePlainFnv1aOfTheCallsign() => Assert.Equal(760618703u, DeterministicHash.Fnv1a("", "AAL123"));

    [Fact]
    public void Salt_ChangesTheDraw() => Assert.NotEqual(DeterministicHash.Fnv1a("", "AAL123"), DeterministicHash.Fnv1a("salt:", "AAL123"));

    [Fact]
    public void ReleaseAutoCtoJitter_KeepsItsUnsaltedValue() => Assert.Equal(20.0, SimulationEngine.ReleaseAutoCtoJitterSeconds("AAL123"));

    [Fact]
    public void AfterTaxiArrivalDelay_IsPinned()
    {
        Assert.Equal(13.0, InitialCallupCall.AfterTaxiArrivalDelaySeconds("AFR83"));
        Assert.Equal(12.0, InitialCallupCall.AfterTaxiArrivalDelaySeconds("N43778"));
    }

    [Fact]
    public void ReleaseRequestDelay_IsPinned() => Assert.Equal(8.0, RunwaySpawnCall.ReleaseRequestDelaySeconds("N513SJ"));

    [Fact]
    public void VfrDepartureDirection_WithEverySectorClear_IsPinned()
    {
        var field = new LatLon(37.72, -122.22);

        Assert.Equal("west", VfrDepartureDirection.Choose("N152SP", field, []));
        Assert.Equal("east", VfrDepartureDirection.Choose("N738SP", field, []));
    }
}
