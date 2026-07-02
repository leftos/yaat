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
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(1830, CfrAction.Set), Utc(18, 0));
        Assert.Equal(Utc(18, 28), ac.Ground.ReleaseWindowStartUtc);
        Assert.Equal(Utc(18, 31), ac.Ground.ReleaseWindowEndUtc);
        Assert.Equal("N123 released for departure at 1830Z (window 1828–1831Z)", echo);
    }

    [Fact]
    public void Apply_NoTime_SetsWindowFromApproval()
    {
        var ac = GroundDeparture();
        var now = Utc(18, 0);
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(null, CfrAction.Set), now);
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
        var echo = CfrDepartureService.Apply(ac, new CfrDepartureCommand(null, CfrAction.Clear), default);
        Assert.Null(ac.Ground.ReleaseWindowStartUtc);
        Assert.Null(ac.Ground.ReleaseWindowEndUtc);
        Assert.Contains("cleared", echo);
    }

    [Fact]
    public void DescribeStatus_NoWindow_ReadsInactive()
    {
        var ac = GroundDeparture();
        Assert.Equal("N123 has no active release window", CfrDepartureService.DescribeStatus(ac, Utc(18, 0)));
    }

    [Fact]
    public void DescribeStatus_Open_ReportsTimeToClose()
    {
        var ac = GroundDeparture();
        ac.Ground.ReleaseWindowStartUtc = Utc(18, 28);
        ac.Ground.ReleaseWindowEndUtc = Utc(18, 31);
        Assert.Equal("N123 release window 1828–1831Z — closes in 1:30", CfrDepartureService.DescribeStatus(ac, Utc(18, 29).AddSeconds(30)));
    }

    [Fact]
    public void DescribeStatus_BeforeOpen_ReportsTimeToOpen()
    {
        var ac = GroundDeparture();
        ac.Ground.ReleaseWindowStartUtc = Utc(18, 28);
        ac.Ground.ReleaseWindowEndUtc = Utc(18, 31);
        Assert.Equal("N123 release window 1828–1831Z — opens in 2:00", CfrDepartureService.DescribeStatus(ac, Utc(18, 26)));
    }

    [Fact]
    public void DescribeStatus_Expired_ReportsElapsed()
    {
        var ac = GroundDeparture();
        ac.Ground.ReleaseWindowStartUtc = Utc(18, 28);
        ac.Ground.ReleaseWindowEndUtc = Utc(18, 31);
        Assert.Equal("N123 release window 1828–1831Z — expired 0:45 ago", CfrDepartureService.DescribeStatus(ac, Utc(18, 31).AddSeconds(45)));
    }

    [Fact]
    public void DescribeStatus_Airborne_ReadsInactive()
    {
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            IsOnGround = false,
        };
        ac.Ground.ReleaseWindowStartUtc = Utc(18, 28);
        ac.Ground.ReleaseWindowEndUtc = Utc(18, 31);
        Assert.Equal("N123 has no active release window", CfrDepartureService.DescribeStatus(ac, Utc(18, 29)));
    }

    [Theory]
    [InlineData(18, 26, 0, CfrPhase.BeforeOpen, 120)]
    [InlineData(18, 29, 30, CfrPhase.Open, 90)]
    [InlineData(18, 31, 45, CfrPhase.Expired, 45)]
    public void CfrCountdown_Evaluate_PhaseAndSeconds(int h, int mi, int sec, CfrPhase phase, int seconds)
    {
        var window = new ReleaseWindow(Utc(18, 28), Utc(18, 31));
        var r = CfrCountdown.Evaluate(window, Utc(h, mi).AddSeconds(sec));
        Assert.Equal(phase, r.Phase);
        Assert.Equal(seconds, r.Seconds);
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
