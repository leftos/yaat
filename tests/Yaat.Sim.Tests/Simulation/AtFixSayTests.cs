using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for `AT &lt;fix&gt; SAY*` deferred pilot reports. Controllers chain a SAY-class
/// verb behind an AT FIX condition so the pilot transmits a report when the aircraft
/// overflies the fix, e.g. `AT MENLO SALT` (say altitude at MENLO) or `AT WAITZ SAY
/// altitude five thousand`.
///
/// Each test dispatches a triggered SAY block and asserts that a TerminalEntry with the
/// expected Kind ("Say", "SayAltitude", "SayHeading", "SayPosition", etc.) is captured
/// by the dispatch context's terminal emitter when the trigger fires.
///
/// Pattern follows AtFixLookaheadTests: synthetic fixes via TestNavDbFactory, direct
/// FlightPhysics.Update ticking, no full SimulationEngine setup needed.
/// </summary>
[Collection("NavDbMutator")]
public class AtFixSayTests(ITestOutputHelper output)
{
    // Three fixes in a line heading roughly north from start position.
    //   FIX_A (37.72, -122.22) → FIX_B (37.74, -122.22) → FIX_C (37.76, -122.22)
    // Aircraft starts at (37.70, -122.22) heading 360 (north) at 5000ft, 250 IAS.
    private static readonly NavigationDatabase NavDb = TestNavDbFactory.WithFixes(
        ("FIX_A", 37.72, -122.22),
        ("FIX_B", 37.74, -122.22),
        ("FIX_C", 37.76, -122.22)
    );

    private static AircraftState MakeAircraft()
    {
        return new AircraftState
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Position = new LatLon(37.70, -122.22),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    private (List<TerminalEntry> Captured, DispatchContext Ctx) MakeContext()
    {
        var captured = new List<TerminalEntry>();
        var ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, terminalEmitter: captured.Add);
        return (captured, ctx);
    }

    private bool TickUntil(AircraftState ac, Func<bool> until, int maxTicks = 600, string? logTag = null)
    {
        for (int t = 0; t < maxTicks; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            if (logTag is not null && t % 20 == 0)
            {
                output.WriteLine($"[{logTag}] t={t} lat={ac.Position.Lat:F4} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0}");
            }
            if (until())
            {
                output.WriteLine($"[{logTag ?? "tick"}] condition met at t={t}");
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void AtFixSay_FreeformBroadcastsAfterOverflight()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SAY altitude five thousand", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        Assert.Empty(captured); // not yet — must overfly first

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[0].IsApplied, logTag: "AT/SAY");
        Assert.True(fired, "AT FIX_B block should have fired within 600 ticks");

        var sayEntry = Assert.Single(captured, e => e.Kind == "Say");
        Assert.Equal("TST01", sayEntry.Callsign);
        Assert.Contains("altitude five thousand", sayEntry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtFixSalt_BroadcastsAltitude()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SALT", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[0].IsApplied, logTag: "AT/SALT");
        Assert.True(fired, "AT FIX_B SALT block should have fired");

        var entry = Assert.Single(captured, e => e.Kind == "SayAltitude");
        Assert.Equal("TST01", entry.Callsign);
        // Aircraft cruising near 5000 ft level, no assigned altitude → bare spoken altitude.
        Assert.Equal("five thousand", entry.Message);
    }

    [Fact]
    public void AtFixShdg_BroadcastsHeading()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SHDG", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[0].IsApplied, logTag: "AT/SHDG");
        Assert.True(fired, "AT FIX_B SHDG block should have fired");

        var entry = Assert.Single(captured, e => e.Kind == "SayHeading");
        Assert.Equal("TST01", entry.Callsign);
        // Aircraft heading 360 north → "Heading three six zero" (AIM 4-2-10)
        Assert.Equal("Heading three six zero", entry.Message);
    }

    [Fact]
    public void AtFixSpos_BroadcastsPosition()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        // BuildPosition restricts candidates to the aircraft's filed route + DCT queue
        // + dep/dest, so FIX_B must be on the route for SPOS to anchor on it.
        ac.FlightPlan.Route = "FIX_B";
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SPOS", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[0].IsApplied, logTag: "AT/SPOS");
        Assert.True(fired, "AT FIX_B SPOS block should have fired");

        var entry = Assert.Single(captured, e => e.Kind == "SayPosition");
        Assert.Equal("TST01", entry.Callsign);
        // At trigger fire time the aircraft is within 0.5 NM of FIX_B → expect "Over FIX_B".
        Assert.Contains("FIX_B", entry.Message);
    }

    [Fact]
    public void AtFixSspd_BroadcastsSpeed()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SSPD", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[0].IsApplied, logTag: "AT/SSPD");
        Assert.True(fired, "AT FIX_B SSPD block should have fired");

        var entry = Assert.Single(captured, e => e.Kind == "SaySpeed");
        Assert.Equal("TST01", entry.Callsign);
        // 250 KIAS at 5000 ft → "two five zero knots" (no Mach below FL240)
        Assert.Equal("two five zero knots", entry.Message);
    }

    [Fact]
    public void AtFixSay_DoesNotFireBeforeOverflight()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        var parsed = CommandParser.ParseCompound("AT FIX_B SAY ready for descent", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess);

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(dispatchResult.Success);

        // Tick a short while — aircraft moves north but doesn't reach FIX_B (~2.4 NM away)
        for (int t = 0; t < 20; t++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.False(ac.Queue.Blocks[0].IsApplied, "Block should not fire before overflight");
        Assert.Empty(captured);
    }

    [Fact]
    public void AtFixSay_FiresViaDctLookaheadPath()
    {
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(NavDb);

        var ac = MakeAircraft();
        var (captured, ctx) = MakeContext();

        // DCT routes the aircraft through FIX_B (sequenced via lookahead at turn-anticipation
        // distance), then continues to FIX_C. AT FIX_B SAY should fire when FIX_B is sequenced.
        var parsed = CommandParser.ParseCompound("DCT FIX_A FIX_B FIX_C; AT FIX_B SAY position report", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");
        Assert.Equal(2, parsed.Value!.Blocks.Count);

        var dispatchResult = CommandDispatcher.DispatchCompound(parsed.Value, ac, ctx);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.Message}");

        bool fired = TickUntil(ac, () => ac.Queue.Blocks[1].IsApplied, logTag: "DCT-AT/SAY");
        Assert.True(fired, "AT FIX_B SAY block should have fired via lookahead while DCT was in progress");

        var sayEntry = Assert.Single(captured, e => e.Kind == "Say");
        Assert.Equal("TST01", sayEntry.Callsign);
        Assert.Contains("position report", sayEntry.Message, StringComparison.OrdinalIgnoreCase);

        // DCT should still be progressing: route still has FIX_C
        Assert.Contains(ac.Targets.NavigationRoute, r => r.Name == "FIX_C");
    }
}
