using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="MagneticDeclination"/> evaluates the WMM at a whole UTC day, so two processes started seconds apart
/// (a seeded soak run and its replay) compute identical declinations instead of drifting by the wall clock.
/// </summary>
public class MagneticDeclinationEvaluationDateTests
{
    [Fact]
    public void EvaluationDate_IsAWholeUtcDay()
    {
        var date = MagneticDeclination.EvaluationDateUtc;

        Assert.Equal(TimeSpan.Zero, date.TimeOfDay);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
    }

    [Fact]
    public void Declination_IsStableAcrossCalls()
    {
        var oak = new LatLon(37.7213, -122.2208);

        Assert.Equal(MagneticDeclination.GetDeclination(oak), MagneticDeclination.GetDeclination(oak));
        Assert.InRange(MagneticDeclination.GetDeclination(oak), 11.0, 15.0);
    }
}
