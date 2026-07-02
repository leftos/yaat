using Xunit;
using Yaat.Client.Core.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CfrAlertMonitorTests
{
    private static DateTime Utc(int h, int mi) => new(2026, 7, 2, h, mi, 0, DateTimeKind.Utc);

    private static readonly DateTime Start = Utc(18, 28);
    private static readonly DateTime End = Utc(18, 31);

    [Fact]
    public void LateTakeoff_FiresOnceThenLatches()
    {
        var m = new CfrAlertMonitor();
        // Seed the latch while grounded and in-window — no alert.
        Assert.Null(m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 29)));
        // Wheels-up after the window end fires the late alert.
        Assert.Equal(CfrAlertKind.LateTakeoff, m.Evaluate("N1", Start, End, isOnGround: false, wasOnGround: true, Utc(18, 32)));
        // Subsequent airborne observations don't re-fire.
        Assert.Null(m.Evaluate("N1", Start, End, isOnGround: false, wasOnGround: false, Utc(18, 33)));
    }

    [Fact]
    public void EarlyTakeoff_Fires()
    {
        var m = new CfrAlertMonitor();
        Assert.Equal(CfrAlertKind.EarlyTakeoff, m.Evaluate("N1", Start, End, isOnGround: false, wasOnGround: true, Utc(18, 27)));
    }

    [Fact]
    public void ExpiredGrounded_FiresOnce()
    {
        var m = new CfrAlertMonitor();
        Assert.Equal(CfrAlertKind.ExpiredGrounded, m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 32)));
        Assert.Null(m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 33)));
    }

    [Fact]
    public void NewWindow_ResetsLatch()
    {
        var m = new CfrAlertMonitor();
        Assert.Equal(CfrAlertKind.ExpiredGrounded, m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 32)));
        // A re-issued window (different bounds) resets the latch and can alert again.
        var start2 = Utc(18, 40);
        var end2 = Utc(18, 43);
        Assert.Null(m.Evaluate("N1", start2, end2, isOnGround: true, wasOnGround: true, Utc(18, 41)));
        Assert.Equal(CfrAlertKind.ExpiredGrounded, m.Evaluate("N1", start2, end2, isOnGround: true, wasOnGround: true, Utc(18, 44)));
    }

    [Fact]
    public void ClearedWindow_DropsStateAndReArms()
    {
        var m = new CfrAlertMonitor();
        Assert.Equal(CfrAlertKind.ExpiredGrounded, m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 32)));
        // Window cleared (null) — state dropped, no alert.
        Assert.Null(m.Evaluate("N1", null, null, isOnGround: true, wasOnGround: true, Utc(18, 33)));
        // The same window re-issued is treated fresh (prior latch was dropped), so it fires again.
        Assert.Equal(CfrAlertKind.ExpiredGrounded, m.Evaluate("N1", Start, End, isOnGround: true, wasOnGround: true, Utc(18, 34)));
    }
}
