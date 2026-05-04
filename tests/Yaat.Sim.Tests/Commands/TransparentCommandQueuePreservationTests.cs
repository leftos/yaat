using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Phase-transparent commands (RFIS/RTIS and the broader squawk/ident/say set)
/// must not wipe queued command blocks regardless of phase state. Before this
/// fix, the line-58 transparent fast path was gated on `Phases.CurrentPhase
/// is not null`, so transparent commands fell through to normal dispatch when
/// the aircraft had no active phase, and `ClearConflictingBlocks` then hit the
/// `None`-dimension fast path that clears the entire queue.
///
/// Concrete repro: N435C in S2-OAK-5 bug bundle. Compound `DCT OAK; ERD 28R`
/// queues both blocks (DCT applied, ERD pending). RTIS at t=998 wiped the
/// queue including the still-pending ERD. The user later reissued ERD at
/// t=1400 because the original was silently lost.
/// </summary>
public class TransparentCommandQueuePreservationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public TransparentCommandQueuePreservationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeAirborneVfr() =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.62, -122.19),
            TrueHeading = new TrueHeading(340),
            Altitude = 1500,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KOAK",
            },
        };

    private static AircraftState MakeTrafficTarget() =>
        new()
        {
            Callsign = "N85439",
            AircraftType = "C172",
            Position = new LatLon(37.65, -122.20),
            TrueHeading = new TrueHeading(0),
            Altitude = 1500,
            IndicatedAirspeed = 100,
        };

    private static DispatchContext CtxWithLookup(AircraftState target) =>
        TestDispatch.Context(
            Random.Shared,
            validateDctFixes: false,
            findAircraft: cs => cs.Equals(target.Callsign, StringComparison.OrdinalIgnoreCase) ? target : null
        );

    private static void DispatchOk(AircraftState ac, string text, DispatchContext ctx)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(result.Success, result.Message);
    }

    private void DumpQueue(string label, AircraftState ac)
    {
        _output.WriteLine($"=== {label} (queue {ac.Queue.Blocks.Count} blocks) ===");
        for (int i = 0; i < ac.Queue.Blocks.Count; i++)
        {
            var b = ac.Queue.Blocks[i];
            _output.WriteLine($"  [{i}] applied={b.IsApplied} desc='{b.NaturalDescription}'");
        }
    }

    [Fact]
    public void Rtis_WithNullPhases_PreservesQueuedErd()
    {
        var ac = MakeAirborneVfr();
        var ctx = CtxWithLookup(MakeTrafficTarget());

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        DumpQueue("After DCT OAK; ERD 28R", ac);
        Assert.Null(ac.Phases?.CurrentPhase); // ERD waits for DCT to complete; no active phase yet
        Assert.Equal(2, ac.Queue.Blocks.Count);
        Assert.Contains("Enter right downwind", ac.Queue.Blocks[1].NaturalDescription);

        DispatchOk(ac, "RTIS N85439", ctx);
        DumpQueue("After RTIS N85439", ac);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }

    [Fact]
    public void Rfis_WithNullPhases_PreservesQueuedErd()
    {
        var ac = MakeAirborneVfr();
        var ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        Assert.Equal(2, ac.Queue.Blocks.Count);

        DispatchOk(ac, "RFIS", ctx);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
    }

    [Fact]
    public void Squawk_WithNullPhases_PreservesQueuedErd()
    {
        var ac = MakeAirborneVfr();
        var ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false);

        DispatchOk(ac, "DCT OAK; ERD 28R", ctx);
        Assert.Equal(2, ac.Queue.Blocks.Count);

        DispatchOk(ac, "SQ 1234", ctx);

        Assert.Contains(ac.Queue.Blocks, b => b.NaturalDescription.Contains("Enter right downwind"));
        Assert.Equal(1234u, ac.Transponder.Code);
    }
}
