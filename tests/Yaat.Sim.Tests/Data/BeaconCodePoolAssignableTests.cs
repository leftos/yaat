using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="BeaconCodePool.IsAssignableCode"/> is the single gate on which beacon codes may be
/// auto-assigned. Codes are decimal digits representing octal (1200u = squawk 1200). The "ends in 00" rule
/// only catches the non-discrete reserved codes; the reserved *discrete* special-purpose codes — notably the
/// 7600 radio-failure and 7700 emergency series — must be excluded by range or they raise a false RF / EMRG
/// indicator on a controller's scope.
/// </summary>
public class BeaconCodePoolAssignableTests
{
    [Theory]
    // Non-discrete SPCs and block codes (last two octal digits zero).
    [InlineData(0000u)]
    [InlineData(1200u)] // VFR conspicuity
    [InlineData(4000u)] // military
    [InlineData(7400u)] // UAS lost link
    [InlineData(7500u)] // hijack
    [InlineData(7600u)] // radio failure
    [InlineData(7700u)] // emergency
    [InlineData(1300u)]
    // Discrete members of the radio-failure and emergency series — these still trigger RF / EMRG.
    [InlineData(7601u)]
    [InlineData(7607u)]
    [InlineData(7615u)]
    [InlineData(7677u)]
    [InlineData(7701u)]
    [InlineData(7776u)]
    [InlineData(7777u)] // military interceptor
    [InlineData(7501u)]
    // Monitored VFR conspicuity codes besides 1200.
    [InlineData(1202u)]
    [InlineData(1203u)]
    [InlineData(1255u)]
    [InlineData(1276u)]
    [InlineData(1277u)]
    // DoD-allocated block.
    [InlineData(5001u)]
    [InlineData(5062u)]
    public void ReservedCodes_AreNotAssignable(uint code)
    {
        Assert.False(BeaconCodePool.IsAssignableCode(code), $"{code:D4} must never be auto-assigned");
    }

    [Theory]
    [InlineData(0001u)]
    [InlineData(0401u)] // inside ZOA's real IFR bank
    [InlineData(0436u)]
    [InlineData(0101u)] // inside ZOA's real VFR bank
    [InlineData(1201u)] // adjacent to 1200 but not reserved
    [InlineData(1204u)]
    [InlineData(4777u)]
    [InlineData(5063u)] // just past the DoD block
    [InlineData(7477u)] // just below the 7500 block
    public void OrdinaryDiscreteCodes_AreAssignable(uint code)
    {
        Assert.True(BeaconCodePool.IsAssignableCode(code), $"{code:D4} should be assignable");
    }

    /// <summary>
    /// The sequential fallback (no configured banks) is the only path where IsAssignableCode is the sole
    /// guard, so it must never hand out a reserved code even when drawing thousands of them.
    /// </summary>
    [Fact]
    public void SequentialFallback_NeverAssignsReservedCode()
    {
        var pool = new BeaconCodePool();
        for (int i = 0; i < 2000; i++)
        {
            var code = pool.AssignNextCode(isVfr: false);
            if (code == 0)
            {
                break;
            }

            Assert.True(BeaconCodePool.IsAssignableCode(code), $"sequential fallback produced reserved code {code:D4}");
        }
    }
}
