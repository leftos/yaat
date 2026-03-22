using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for AT fix-name lookahead: when a compound command has a block with an AT fix
/// condition following a DCT block, the AT condition should fire when the aircraft reaches
/// that fix mid-route, not wait until the DCT route fully completes.
///
/// Bug: "cm 020, dct vpcol oak30num vpmid, at oak30num cm 014" — the AT condition should
/// trigger a descent to 1400 when the aircraft crosses OAK30NUM, while continuing to VPMID.
/// </summary>
public class AtFixLookaheadTests(ITestOutputHelper output)
{
    // Three fixes in a line heading roughly north from start position:
    //   FIX_A (37.72, -122.22) → FIX_B (37.74, -122.22) → FIX_C (37.76, -122.22)
    // Aircraft starts at (37.70, -122.22) heading 360 (north) at 3000ft.
    // Command: DCT FIX_A FIX_B FIX_C; AT FIX_B CM 014
    // Expected: when aircraft reaches FIX_B, altitude target changes to 1400 while route continues to FIX_C.

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
            Latitude = 37.70,
            Longitude = -122.22,
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = 3000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    [Fact]
    public void AtFix_LookaheadFiresDuringDct()
    {
        NavigationDatabase.SetInstance(NavDb);

        var ac = MakeAircraft();

        // Parse the compound command: DCT through three fixes, AT middle fix descend
        var parseResult = CommandParser.ParseCompound("DCT FIX_A FIX_B FIX_C; AT FIX_B CM 014", ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");
        Assert.Equal(2, parseResult.Value!.Blocks.Count);

        // Dispatch
        var result = CommandDispatcher.DispatchCompound(parseResult.Value, ac, null, Random.Shared, false);
        Assert.True(result.Success, $"Dispatch failed: {result.Message}");

        // Block 0 = DCT FIX_A FIX_B FIX_C (applied immediately)
        // Block 1 = AT FIX_B CM 014 (should fire mid-route via lookahead)
        Assert.Equal(2, ac.Queue.Blocks.Count);
        Assert.True(ac.Queue.Blocks[0].IsApplied, "DCT block should be applied immediately");
        Assert.False(ac.Queue.Blocks[1].IsApplied, "AT block should not be applied yet");

        // Verify the AT block has a ReachFix trigger
        Assert.NotNull(ac.Queue.Blocks[1].Trigger);
        Assert.Equal(BlockTriggerType.ReachFix, ac.Queue.Blocks[1].Trigger!.Type);

        // Tick until aircraft reaches FIX_B or timeout
        bool atBlockFired = false;
        bool routeStillHasFixC = false;

        for (int tick = 0; tick < 600; tick++)
        {
            FlightPhysics.Update(ac, 1.0);

            if (tick % 30 == 0)
            {
                output.WriteLine(
                    $"t={tick, 4}: lat={ac.Latitude:F4} alt={ac.Altitude:F0} "
                        + $"tgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "null"} "
                        + $"route=[{string.Join(", ", ac.Targets.NavigationRoute.Select(r => r.Name))}] "
                        + $"queueIdx={ac.Queue.CurrentBlockIndex} blk1Applied={ac.Queue.Blocks[1].IsApplied}"
                );
            }

            // Check if AT block fired (its commands were applied)
            if (ac.Queue.Blocks[1].IsApplied && !atBlockFired)
            {
                atBlockFired = true;
                routeStillHasFixC = ac.Targets.NavigationRoute.Any(r => r.Name == "FIX_C");
                output.WriteLine($"*** AT block fired at tick {tick}: tgtAlt={ac.Targets.TargetAltitude} routeHasFixC={routeStillHasFixC} ***");
                break;
            }
        }

        Assert.True(atBlockFired, "AT FIX_B block should have fired via lookahead while DCT was still in progress");
        Assert.True(routeStillHasFixC, "Navigation route should still contain FIX_C (DCT not abandoned)");
        Assert.Equal(1400, ac.Targets.TargetAltitude);
    }
}
