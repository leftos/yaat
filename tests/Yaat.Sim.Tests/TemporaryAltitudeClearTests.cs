using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The temporary-altitude clear form (CRC F7 "M Δ000", RPO bare/0 TA) parses to
/// <c>TemporaryAltitudeCommand(0)</c>; the engine must null the field so CRC blanks
/// the FDB altitude line instead of rendering "A000" forever (leftos/yaat#385).
/// </summary>
public class TemporaryAltitudeClearTests
{
    [Fact]
    public void HandleTemporaryAltitude_Zero_ClearsToNull()
    {
        var ac = new AircraftState { Callsign = "N123AB", AircraftType = "C172" };
        TrackEngine.HandleTemporaryAltitude(ac, 50);
        Assert.Equal(50, ac.Stars.TemporaryAltitude);

        var result = TrackEngine.HandleTemporaryAltitude(ac, 0);

        Assert.True(result.Success);
        Assert.Null(ac.Stars.TemporaryAltitude);
    }

    [Fact]
    public void HandleTemporaryAltitude_NonZero_Stores()
    {
        var ac = new AircraftState { Callsign = "N123AB", AircraftType = "C172" };

        var result = TrackEngine.HandleTemporaryAltitude(ac, 110);

        Assert.True(result.Success);
        Assert.Equal(110, ac.Stars.TemporaryAltitude);
    }
}
