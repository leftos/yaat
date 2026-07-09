using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// A facility config may define banks for one flight-rules type but not the other (e.g. an IFR bank with no
/// VFR bank). Drawing for the missing type must still yield a usable discrete code — never 0, which would
/// put an aircraft on the illegal all-zeros squawk.
/// </summary>
public class BeaconCodePoolFallbackTests
{
    [Fact]
    public void VfrDraw_WhenOnlyIfrBankConfigured_FallsBackToDiscreteCode()
    {
        var pool = new BeaconCodePool([
            new BeaconCodeBankConfig
            {
                Type = "Ifr",
                Start = 401,
                End = 436,
            },
        ]);

        var code = pool.AssignNextCode(isVfr: true);

        Assert.NotEqual(0u, code);
        Assert.True(BeaconCodePool.IsAssignableCode(code), $"fallback produced non-assignable code {code:D4}");
    }

    [Fact]
    public void IfrDraw_WhenOnlyVfrBankConfigured_FallsBackToDiscreteCode()
    {
        var pool = new BeaconCodePool([
            new BeaconCodeBankConfig
            {
                Type = "Vfr",
                Start = 101,
                End = 160,
            },
        ]);

        var code = pool.AssignNextCode(isVfr: false);

        Assert.NotEqual(0u, code);
        Assert.True(BeaconCodePool.IsAssignableCode(code), $"fallback produced non-assignable code {code:D4}");
    }

    [Fact]
    public void ExhaustedBank_FallsBackRatherThanReturningZero()
    {
        // A single-code IFR bank: the second draw cannot come from the bank.
        var pool = new BeaconCodePool([
            new BeaconCodeBankConfig
            {
                Type = "Ifr",
                Start = 401,
                End = 401,
            },
        ]);

        Assert.Equal(401u, pool.AssignNextCode(isVfr: false));

        var second = pool.AssignNextCode(isVfr: false);
        Assert.NotEqual(0u, second);
        Assert.NotEqual(401u, second);
        Assert.True(BeaconCodePool.IsAssignableCode(second), $"fallback produced non-assignable code {second:D4}");
    }

    [Fact]
    public void AnyBank_IsPreferredOverSequentialFallback()
    {
        var pool = new BeaconCodePool([
            new BeaconCodeBankConfig
            {
                Type = "Ifr",
                Start = 401,
                End = 401,
            },
            new BeaconCodeBankConfig
            {
                Type = "Any",
                Start = 601,
                End = 610,
            },
        ]);

        Assert.Equal(401u, pool.AssignNextCode(isVfr: false));
        Assert.InRange(pool.AssignNextCode(isVfr: false), 601u, 610u);
    }
}
