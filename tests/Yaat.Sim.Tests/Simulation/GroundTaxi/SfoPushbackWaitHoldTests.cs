using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A scenario's scripted timed command waits for an active pushback. SKW5564's presets at SFO stand F8 are
/// <c>PUSH T7A</c>, <c>WAIT 30 SN</c>, <c>WAIT 30 TAXI T7A $7A</c>; the deferred TAXI used to fire 30 s in,
/// while the tug was still pushing, clear the pushback through the phase gate and taxi the aircraft away
/// tail-first from the middle of the alley (S1-SFO-2 | Ground Control 28/01).
///
/// <para>The hold is tick-based, so it replays exactly: the deferral stays on the aircraft's list until the
/// current phase is no longer a <see cref="PushbackPhase"/>, and commands that do not end a push (a squawk,
/// a strip op) still fire on their own timer. An instructor-typed TAXI is untouched and still takes the tug
/// off mid-push.</para>
/// </summary>
public sealed class SfoPushbackWaitHoldTests(ITestOutputHelper output)
{
    private const string Stand = "F8";
    private const string AircraftType = "CRJ2";
    private const string Callsign = "SKW5564";
    private const string MoveCommand = "PUSH T7A";
    private const string TaxiCommand = "TAXI T7A $7A";

    /// <summary>A hold-short of a taxiway the TAXI's own route runs along.</summary>
    private const string HoldShortCommand = "HS T7A";

    /// <summary>The terminal line the hold emits, the instructor's only sight of it.</summary>
    private const string HoldWording = "held until pushback completes";

    /// <summary>When the chains' second scripted command falls due, 15 s after the TAXI's own WAIT 30.</summary>
    private const int SecondScriptedDueSecond = 45;

    /// <summary>The scripted chain: the push, then a squawk and a taxi, each 30 s after the push starts.</summary>
    private static readonly string[] PresetCommands = [MoveCommand, "WAIT 30 SN", $"WAIT 30 {TaxiCommand}"];

    /// <summary>
    /// One second past the scripted <c>WAIT 30</c>. The TAXI's timer has expired here, so on the old
    /// always-fire behaviour the tug is already gone; the pushback must still be running.
    /// </summary>
    private const int HoldCheckSecond = 31;

    /// <summary>
    /// Tick budget for the push. The tug spins up at its towbar rate (0.3 kt/s, ~17 s to its 5 kt) and holds
    /// 5 kt, so the F8 alley push runs well past a minute — 400 s is a failure timeout, not a cost.
    /// </summary>
    private const int PushBudgetSeconds = 400;

    [Fact]
    public void ScriptedTaxi_WaitsForThePushbackToFinish()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand);

        // Every deferred dispatch the RPO sees, with the phase it fired against — the moment the payload
        // reached the phase gate, captured before the payload could change anything.
        var deferred = new List<(int Second, string Message, bool DuringPushback)>();
        int second = 0;
        ground.Engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Message.StartsWith("[Deferred]", StringComparison.Ordinal))
            {
                deferred.Add((second, entry.Message, ac.Phases?.CurrentPhase is PushbackPhase));
            }
        };

        ground.Engine.DispatchPresetCommands(
            new LoadedAircraft { State = ac, PresetCommands = [.. PresetCommands.Select(command => new PresetCommand { Command = command })] }
        );

        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(2, ac.DeferredDispatches.Count);

        int pushbackEndSecond = -1;
        int taxiSecond = -1;
        for (second = 1; second <= PushBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();

            if (ac.Phases?.CurrentPhase is PushbackPhase)
            {
                continue;
            }

            if (pushbackEndSecond < 0)
            {
                pushbackEndSecond = second;
            }

            if (ac.Phases?.CurrentPhase is TaxiingPhase)
            {
                taxiSecond = second;
                break;
            }
        }

        output.WriteLine($"pushback ended at t={pushbackEndSecond}s, taxiing at t={taxiSecond}s");
        foreach ((int at, string message, bool duringPushback) in deferred)
        {
            output.WriteLine($"  t={at}s duringPushback={duringPushback} {message}");
        }

        Assert.True(
            pushbackEndSecond > HoldCheckSecond,
            $"the pushback ended at t={pushbackEndSecond}s — the scripted TAXI, whose 30 s timer expired at "
                + $"t={HoldCheckSecond - 1}s, took the tug off mid-push instead of waiting for it"
        );
        Assert.True(
            taxiSecond > 0,
            $"the scripted TAXI never applied — the aircraft was {ac.Phases?.CurrentPhase?.Name ?? "(no phase)"} at t={PushBudgetSeconds}s"
        );
        Assert.DoesNotContain(deferred, e => e.DuringPushback && e.Message.Contains("Taxi", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            deferred,
            e => e.DuringPushback && (e.Second <= HoldCheckSecond) && e.Message.Contains("Squawk normal", StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// The instructor is not the scenario: a TAXI typed mid-push still drops the tug immediately, whatever the
    /// push is doing and whatever the scenario's own scripted commands are waiting for.
    /// </summary>
    [Fact]
    public void InstructorTaxi_MidPush_StillTakesTheTugOffImmediately()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "N511SP", AircraftType, Stand);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });

        // A TAXI only routes once the tow has brought the aircraft far enough off the stand to reach the
        // taxiway, so it is sent each second until it takes; the cue is the tug being under way.
        CommandResult? taxi = null;
        int taxiSecond = -1;
        for (int at = 1; (at <= PushBudgetSeconds) && (ac.Phases?.CurrentPhase is PushbackPhase); at++)
        {
            ground.Engine.TickOneSecond();
            taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
            if (taxi.Success)
            {
                taxiSecond = at;
                break;
            }
        }

        output.WriteLine($"'{TaxiCommand}' mid-push at t={taxiSecond}s → \"{taxi?.Message}\"");
        Assert.True(taxi is { Success: true }, $"'{TaxiCommand}' during the pushback was refused: {taxi?.Message}");
        Assert.DoesNotContain(ac.Phases!.Phases, phase => phase is PushbackPhase);
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A scripted command behind a held one waits with it when the pushback would reject it. <c>HS</c> is the
    /// case: the pushback's gate answers "only HOLD/RES are accepted until pushback completes", so a scripted
    /// <c>WAIT 45 HS …</c> that falls due mid-push used to be dispatched, refused and lost — the aircraft then
    /// taxied out with no hold-short. Held, it fires FIFO behind the TAXI, against the taxiing phase that
    /// accepts it.
    /// </summary>
    [Fact]
    public void ScriptedHoldShort_BehindAHeldTaxi_WaitsAndApplies()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand);

        var deferred = new List<(int Second, string Message, bool DuringPushback)>();
        var holds = new List<(int Second, string Message)>();
        int second = 0;
        ground.Engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Message.Contains(HoldWording, StringComparison.Ordinal))
            {
                holds.Add((second, entry.Message));
            }
            else if (entry.Message.StartsWith("[Deferred]", StringComparison.Ordinal))
            {
                deferred.Add((second, entry.Message, ac.Phases?.CurrentPhase is PushbackPhase));
            }
        };

        // The TAXI is due at t=30 and the hold-short at t=45 — both while the tow is still running.
        ground.Engine.DispatchPresetCommands(
            new LoadedAircraft
            {
                State = ac,
                PresetCommands =
                [
                    new PresetCommand { Command = MoveCommand },
                    new PresetCommand { Command = $"WAIT 30 {TaxiCommand}" },
                    new PresetCommand { Command = $"WAIT 45 {HoldShortCommand}" },
                ],
            }
        );

        Assert.Equal(2, ac.DeferredDispatches.Count);

        int taxiSecond = -1;
        for (second = 1; second <= PushBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is TaxiingPhase)
            {
                taxiSecond = second;
                break;
            }
        }

        output.WriteLine($"taxiing at t={taxiSecond}s, route holds: {DescribeHolds(ac)}");
        foreach ((int at, string message) in holds)
        {
            output.WriteLine($"  t={at}s HOLD-LINE {message}");
        }

        foreach ((int at, string message, bool duringPushback) in deferred)
        {
            output.WriteLine($"  t={at}s duringPushback={duringPushback} {message}");
        }

        Assert.True(
            taxiSecond > 0,
            $"the scripted TAXI never applied — the aircraft was {ac.Phases?.CurrentPhase?.Name ?? "(no phase)"} at t={PushBudgetSeconds}s"
        );
        Assert.Empty(ac.DeferredDispatches);

        // Both scripted commands were held (once each) and both then fired, TAXI first.
        Assert.Equal(2, holds.Count);
        Assert.Contains(holds, h => h.Message.Contains("Taxi", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(holds, h => h.Message.Contains("Hold short", StringComparison.OrdinalIgnoreCase));

        int taxiDispatch = deferred.FindIndex(e => e.Message.Contains("Taxi", StringComparison.OrdinalIgnoreCase));
        int hsDispatch = deferred.FindIndex(e => e.Message.Contains("Hold short", StringComparison.OrdinalIgnoreCase));
        Assert.True(taxiDispatch >= 0, "the scripted TAXI never dispatched");
        Assert.True(hsDispatch > taxiDispatch, $"the scripted hold-short dispatched before the TAXI (taxi idx {taxiDispatch}, hs idx {hsDispatch})");
        Assert.DoesNotContain(deferred, e => e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));

        // The hold-short reached the route the TAXI had just installed.
        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.Contains(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
    }

    /// <summary>
    /// A scripted command the pushback never gates still fires on its own timer while a command ahead of it
    /// waits: a squawk is applied to the aircraft, not to the phase, so holding the TAXI must not hold it.
    /// </summary>
    [Fact]
    public void ScriptedSquawk_BehindAHeldTaxi_StillFiresOnTime()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand);

        var deferred = new List<(int Second, string Message, bool DuringPushback)>();
        int second = 0;
        ground.Engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Message.StartsWith("[Deferred]", StringComparison.Ordinal))
            {
                deferred.Add((second, entry.Message, ac.Phases?.CurrentPhase is PushbackPhase));
            }
        };

        ground.Engine.DispatchPresetCommands(
            new LoadedAircraft
            {
                State = ac,
                PresetCommands =
                [
                    new PresetCommand { Command = MoveCommand },
                    new PresetCommand { Command = $"WAIT 30 {TaxiCommand}" },
                    new PresetCommand { Command = "WAIT 45 SN" },
                ],
            }
        );

        for (second = 1; second <= SecondScriptedDueSecond + 1; second++)
        {
            ground.Engine.TickOneSecond();
        }

        output.WriteLine($"t={second}s phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"} deferred={ac.DeferredDispatches.Count}");
        foreach ((int at, string message, bool duringPushback) in deferred)
        {
            output.WriteLine($"  t={at}s duringPushback={duringPushback} {message}");
        }

        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Contains(
            deferred,
            e =>
                e.DuringPushback
                && (e.Second <= SecondScriptedDueSecond + 1)
                && e.Message.Contains("Squawk normal", StringComparison.OrdinalIgnoreCase)
        );

        // The TAXI is still held: the squawk firing did not release the queue behind it.
        Assert.Single(ac.DeferredDispatches);
        Assert.True(ac.DeferredDispatches[0].IsScenarioScripted);

        int taxiSecond = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is TaxiingPhase, PushBudgetSeconds, null);
        Assert.True(taxiSecond > 0, $"the held TAXI never applied after the pushback — {ac.Phases?.CurrentPhase?.Name ?? "(no phase)"}");
    }

    /// <summary>
    /// The deferral list is not in time order. Time-offset presets are queued and then fired by a reverse walk
    /// of the queue, so a preset listed second lands in the list first: here the refused <c>HS</c>, whose timer
    /// expires at t=40, sits ahead of the <c>TAXI</c> that is due at t=30. The hold has to find it wherever it
    /// sits, and the release has to follow the timers — the TAXI first — or the hold-short is dispatched into
    /// the pushback with no route to bind to and is lost.
    /// </summary>
    [Fact]
    public void OutOfOrderPresets_RefusedCommandAheadOfTheHeldOne_IsHeldAndAppliedAfterIt()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, Stand);

        var deferred = new List<(int Second, string Message, bool DuringPushback)>();
        var holds = new List<(int Second, string Message)>();
        int second = 0;
        ground.Engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Message.Contains(HoldWording, StringComparison.Ordinal))
            {
                holds.Add((second, entry.Message));
            }
            else if (entry.Message.StartsWith("[Deferred]", StringComparison.Ordinal))
            {
                deferred.Add((second, entry.Message, ac.Phases?.CurrentPhase is PushbackPhase));
            }
        };

        // Both presets carry a time offset, so both ride the preset queue; the reverse walk of that queue fires
        // the hold-short (listed second) first, and its longer WAIT puts it at the head of the deferral list.
        ground.Engine.DispatchPresetCommands(
            new LoadedAircraft
            {
                State = ac,
                PresetCommands =
                [
                    new PresetCommand { Command = MoveCommand },
                    new PresetCommand { Command = $"WAIT 25 {TaxiCommand}", TimeOffset = 5 },
                    new PresetCommand { Command = $"WAIT 35 {HoldShortCommand}", TimeOffset = 5 },
                ],
            }
        );

        int taxiSecond = -1;
        bool sawTheOutOfOrderPair = false;
        for (second = 1; second <= PushBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();

            if (!sawTheOutOfOrderPair && (ac.DeferredDispatches.Count == 2))
            {
                sawTheOutOfOrderPair = true;
                Assert.IsType<HoldShortCommand>(ac.DeferredDispatches[0].Payload.Blocks[0].Commands[0]);
                Assert.True(
                    ac.DeferredDispatches[0].RemainingSeconds > ac.DeferredDispatches[1].RemainingSeconds,
                    "this case needs the refused hold-short ahead of the taxiing command with the longer timer, not behind it"
                );
            }

            if (ac.Phases?.CurrentPhase is TaxiingPhase)
            {
                taxiSecond = second;
                break;
            }
        }

        Assert.True(sawTheOutOfOrderPair, "the two time-offset presets never produced their deferrals");

        output.WriteLine($"taxiing at t={taxiSecond}s, route holds: {DescribeHolds(ac)}");
        foreach ((int at, string message) in holds)
        {
            output.WriteLine($"  t={at}s HOLD-LINE {message}");
        }

        foreach ((int at, string message, bool duringPushback) in deferred)
        {
            output.WriteLine($"  t={at}s duringPushback={duringPushback} {message}");
        }

        Assert.True(
            taxiSecond > 0,
            $"the scripted TAXI never applied — the aircraft was {ac.Phases?.CurrentPhase?.Name ?? "(no phase)"} at t={PushBudgetSeconds}s"
        );
        Assert.Empty(ac.DeferredDispatches);
        Assert.Equal(2, holds.Count);
        Assert.Contains(holds, h => h.Message.Contains("Taxi", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(holds, h => h.Message.Contains("Hold short", StringComparison.OrdinalIgnoreCase));

        int taxiDispatch = deferred.FindIndex(e => e.Message.Contains("Taxi", StringComparison.OrdinalIgnoreCase));
        int hsDispatch = deferred.FindIndex(e => e.Message.Contains("Hold short", StringComparison.OrdinalIgnoreCase));
        Assert.True(taxiDispatch >= 0, "the scripted TAXI never dispatched");
        Assert.True(
            hsDispatch > taxiDispatch,
            $"the release did not follow the timers: the hold-short (t=45) dispatched before the TAXI (t=30) — taxi idx {taxiDispatch}, hs idx {hsDispatch}"
        );
        Assert.DoesNotContain(deferred, e => e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));

        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.Contains(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
    }

    private static string DescribeHolds(AircraftState ac) =>
        ac.Ground.AssignedTaxiRoute is { } route
            ? string.Join("; ", route.HoldShortPoints.Select(h => $"{h.TargetName}:{h.Reason}:{h.IsCleared}"))
            : "(no route)";
}
