using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #335: the interactive typed path routes commands through the client-side scheme parser,
/// whose canonical string must not break a <c>&lt;condition&gt; WAIT n &lt;cmd&gt;</c> compound
/// into a top-level unconditioned payload block. The payload has to fire n seconds after the
/// condition, not whenever the queue's current in-progress block happens to complete.
/// </summary>
public class ConditionWaitClientCanonicalTests(ITestOutputHelper output)
{
    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "TSTX",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 5100,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };

    private (int? crossedAt, int? descendedAt) Run(bool useClientCanonical)
    {
        const string typed = "LV 050 WAIT 5 DM 110";
        var ac = MakeAircraft();
        var ctx = TestDispatch.Context(new SerializableRandom(42));

        // A prior, still-in-progress instruction: descend to 4,000 ft. The aircraft is crossing 5,000 ft
        // downward, so LV 050 fires almost immediately, but DM 040 stays the current (incomplete) block.
        CommandDispatcher.DispatchCompound(CommandParser.ParseCompound("DM 040").Value!, ac, ctx);

        var toDispatch = typed;
        if (useClientCanonical)
        {
            var canonical = CommandSchemeParser.ParseCompound(typed, CommandScheme.Default());
            Assert.NotNull(canonical);
            toDispatch = canonical!.CanonicalString;
            output.WriteLine($"client canonical: '{typed}' -> '{toDispatch}'");
        }

        var parsed = CommandParser.ParseCompound(toDispatch);
        Assert.True(parsed.IsSuccess, $"parse failed: {toDispatch}");
        CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);

        int? crossedAt = null,
            descendedAt = null;
        for (int t = 1; t <= 60; t++)
        {
            FlightPhysics.Update(ac, 1.0, null, null);
            if ((crossedAt is null) && (ac.Altitude <= 5000))
            {
                crossedAt = t;
            }

            if ((descendedAt is null) && (ac.Targets.AssignedAltitude == 11000))
            {
                descendedAt = t;
            }
        }

        output.WriteLine($"{(useClientCanonical ? "canonical" : "direct")}: crossed={crossedAt} descended={descendedAt}");
        return (crossedAt, descendedAt);
    }

    [Fact]
    public void ClientCanonical_ConditionWaitPayload_FiresAfterWait_NotBehindUnrelatedInProgressCommand()
    {
        var direct = Run(useClientCanonical: false);
        Assert.NotNull(direct.crossedAt);
        Assert.True(direct.descendedAt is not null, "control: direct form never descended");
        Assert.True(
            direct.descendedAt!.Value - direct.crossedAt!.Value <= 12,
            $"control: direct form should descend ~5 s after crossing; was {direct.descendedAt - direct.crossedAt}s"
        );

        var canon = Run(useClientCanonical: true);
        Assert.NotNull(canon.crossedAt);
        Assert.True(canon.descendedAt is not null, "canonical form never descended (payload orphaned behind the in-progress DM 040)");
        Assert.True(
            canon.descendedAt!.Value - canon.crossedAt!.Value <= 12,
            $"canonical form descended {canon.descendedAt - canon.crossedAt}s after crossing 5,000 ft — the WAIT payload "
                + "was gated behind the unrelated in-progress DM 040 instead of the ~5 s wait"
        );
    }
}
