using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

public class CfrDepartureServiceTests
{
    private static DateTime Utc(int h, int mi) => new(2026, 7, 2, h, mi, 0, DateTimeKind.Utc);

    private static AircraftState GroundDeparture() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            IsOnGround = true,
        };

    [Fact]
    public void Apply_ExplicitTime_SetsWindowAndEchoes()
    {
        var ac = GroundDeparture();
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(1830, Clear: false), Utc(18, 0));
        Assert.Equal(Utc(18, 28), ac.Ground.ReleaseWindowStartUtc);
        Assert.Equal(Utc(18, 31), ac.Ground.ReleaseWindowEndUtc);
        Assert.Equal("N123 released for departure at 1830Z (window 1828–1831Z)", echo);
    }

    [Fact]
    public void Apply_NoTime_SetsWindowFromApproval()
    {
        var ac = GroundDeparture();
        var now = Utc(18, 0);
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(null, Clear: false), now);
        Assert.Equal(now, ac.Ground.ReleaseWindowStartUtc);
        Assert.Equal(now.AddSeconds(180), ac.Ground.ReleaseWindowEndUtc);
        Assert.Equal("N123 released for departure now (window 1800–1803Z)", echo);
    }

    [Fact]
    public void Apply_Clear_RemovesWindow()
    {
        var ac = GroundDeparture();
        ac.Ground.ReleaseWindowStartUtc = Utc(18, 28);
        ac.Ground.ReleaseWindowEndUtc = Utc(18, 31);
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(null, Clear: true), default);
        Assert.Null(ac.Ground.ReleaseWindowStartUtc);
        Assert.Null(ac.Ground.ReleaseWindowEndUtc);
        Assert.Contains("cleared", echo);
    }

    [Fact]
    public void GroundOps_SnapshotRoundTrip_PreservesWindow()
    {
        var ground = new AircraftGroundOps { ReleaseWindowStartUtc = Utc(18, 28), ReleaseWindowEndUtc = Utc(18, 31) };
        var restored = AircraftGroundOps.FromSnapshot(ground.ToSnapshot(), layout: null);
        Assert.Equal(ground.ReleaseWindowStartUtc, restored.ReleaseWindowStartUtc);
        Assert.Equal(ground.ReleaseWindowEndUtc, restored.ReleaseWindowEndUtc);
    }
}
