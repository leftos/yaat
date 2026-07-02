using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class CfrWindowTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    // ---- Resolver ----

    [Fact]
    public void Resolve_NoTime_IsImmediateWindowFromNow()
    {
        // Bare CFR = immediate release: assigned time is 2 min out, so the −2/+1 window opens now.
        var now = Utc(2026, 7, 2, 18, 0);
        var w = CfrWindowResolver.Resolve(null, now);
        Assert.Equal(now, w.StartUtc);
        Assert.Equal(now.AddSeconds(180), w.EndUtc);
    }

    [Fact]
    public void Resolve_ExplicitTime_BracketsCenterMinus2Plus1()
    {
        // FAA 7110.65 §4-3-4.e.5: airborne within 2 min prior to 1 min after the assigned time.
        var now = Utc(2026, 7, 2, 18, 0);
        var w = CfrWindowResolver.Resolve(1830, now);
        Assert.Equal(Utc(2026, 7, 2, 18, 28), w.StartUtc);
        Assert.Equal(Utc(2026, 7, 2, 18, 31), w.EndUtc);
    }

    [Fact]
    public void Resolve_CrossesMidnightForward_RollsToNextDay()
    {
        // 23:58Z, release 0001 -> center 00:01 next day (3 min ahead), not ~24 h ago.
        var now = Utc(2026, 7, 2, 23, 58);
        var w = CfrWindowResolver.Resolve(1, now);
        Assert.Equal(Utc(2026, 7, 2, 23, 59), w.StartUtc);
        Assert.Equal(Utc(2026, 7, 3, 0, 2), w.EndUtc);
    }

    [Fact]
    public void Resolve_CrossesMidnightBackward_RollsToPriorDay()
    {
        // 00:02Z, release 2359 -> center 23:59 prior day (3 min ago), not ~24 h ahead.
        var now = Utc(2026, 7, 2, 0, 2);
        var w = CfrWindowResolver.Resolve(2359, now);
        Assert.Equal(Utc(2026, 7, 1, 23, 57), w.StartUtc);
        Assert.Equal(Utc(2026, 7, 2, 0, 0), w.EndUtc);
    }

    // ---- Evaluator ----

    private static readonly ReleaseWindow Window = new(Utc(2026, 7, 2, 18, 28), Utc(2026, 7, 2, 18, 31));

    [Fact]
    public void Evaluate_WheelsUpBeforeStart_Early() =>
        Assert.Equal(
            CfrAlertKind.EarlyTakeoff,
            CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 27), isOnGround: false, wasOnGround: true, CfrAlertKind.None)
        );

    [Fact]
    public void Evaluate_WheelsUpAfterEnd_Late() =>
        Assert.Equal(
            CfrAlertKind.LateTakeoff,
            CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 32), isOnGround: false, wasOnGround: true, CfrAlertKind.None)
        );

    [Fact]
    public void Evaluate_WheelsUpInWindow_Silent() =>
        Assert.Null(CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 29), isOnGround: false, wasOnGround: true, CfrAlertKind.None));

    [Fact]
    public void Evaluate_GroundedPastEnd_Expired() =>
        Assert.Equal(
            CfrAlertKind.ExpiredGrounded,
            CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 32), isOnGround: true, wasOnGround: true, CfrAlertKind.None)
        );

    [Fact]
    public void Evaluate_GroundedBeforeEnd_Silent() =>
        Assert.Null(CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 30), isOnGround: true, wasOnGround: true, CfrAlertKind.None));

    [Fact]
    public void Evaluate_LateAlreadyLatched_Silent() =>
        Assert.Null(CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 32), isOnGround: false, wasOnGround: true, CfrAlertKind.LateTakeoff));

    [Fact]
    public void Evaluate_ExpiredAlreadyLatched_Silent() =>
        Assert.Null(CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 32), isOnGround: true, wasOnGround: true, CfrAlertKind.ExpiredGrounded));

    [Fact]
    public void Evaluate_AirborneNoTransition_Silent() =>
        Assert.Null(CfrAlertEvaluator.Evaluate(Window, Utc(2026, 7, 2, 18, 40), isOnGround: false, wasOnGround: false, CfrAlertKind.None));
}
