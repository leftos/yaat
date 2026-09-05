using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// Every <see cref="ParsedCommand"/> subtype must have a route on replay: either <see cref="RecordedCommandClassifier"/>
/// gives it a kind of its own, or it falls to the default <see cref="RecordedCommandKind.Compound"/> arm and
/// <see cref="CommandDispatcher"/> has an arm for it. A type with neither is recorded live and then silently dropped
/// by every replay, rewind and bug-bundle export — that is how <c>SQALL</c>, <c>TAXIALL</c>, <c>ADD</c> and the TDLS
/// verbs were lost until the 2026-09-05 audit found them.
///
/// <para>
/// The unrouted set is banked in <see cref="KnownUnrouted"/> and asserted <em>exactly</em>, the same way the tick
/// oracle banks its accepted divergences: a new unrouted type fails (add the arm, or bank it with a reason), and a
/// type that gains a route fails too (remove it from the list, so the list only ever shrinks deliberately). Step 3d of
/// the tick-path unification empties it.
/// </para>
/// </summary>
public class ActionRoutingCompletenessTests
{
    /// <summary>
    /// Types that classify to <see cref="RecordedCommandKind.Compound"/> and reach no dispatcher arm. Every entry
    /// names why it is unrouted today; every entry is a replay-fidelity gap except the three transport verbs and
    /// the bookmark, which are deliberately never recorded.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnrouted = new(StringComparer.Ordinal)
    {
        ["AddAircraftCommand"] = "global (empty callsign); live spawns through ScenarioLifecycleService and draws World.Rng, no replay arm",
        ["AsdexEnableAllAlertsCommand"] = "global; live applies an ASDE-X mutation inline, no replay arm",
        ["BookmarkCommand"] = "never recorded by design — bookmarks are timeline metadata carried across rewinds verbatim",
        ["CfrDepartureCommand"] = "never recorded — its window is wall-clock UTC and the recorder excludes it",
        ["PauseCommand"] = "transport; never recorded",
        ["UnpauseCommand"] = "transport; never recorded",
        ["SimRateCommand"] = "transport; never recorded",
        ["TaxiAllCommand"] = "global; the dispatcher arm is a refusal ('must be dispatched at the engine level'), the live body is RoomEngine's",
        ["TdlsOpsConfigCommand"] = "global; server TDLS handler, no replay arm",
        ["TdlsQueueCommand"] = "server TDLS handler; replay falls to the dispatcher, which has no arm",
        ["TdlsSendCommand"] = "server TDLS handler; replay falls to the dispatcher, which has no arm",
        ["TdlsWilcoCommand"] = "server TDLS handler; replay falls to the dispatcher, which has no arm",
        ["TdlsDumpCommand"] = "server TDLS handler; replay falls to the dispatcher, which has no arm",
    };

    /// <summary>
    /// Types whose only dispatcher arm is inside the phase-gated tower switch (<c>TryApplyTowerCommand</c>) and that
    /// belong to neither <see cref="CommandDescriber.IsTowerCommand"/> nor <see cref="CommandDescriber.IsGroundCommand"/>,
    /// so a phase-less probe cannot see the arm. They are routed on replay — <c>DispatchCompound</c> reaches the
    /// tower switch under the aircraft's phase — and are listed here so the probe's blind spot is named rather than
    /// papered over. Asserted exactly: an entry that gains a phase-less arm or a family predicate must leave.
    /// </summary>
    private static readonly Dictionary<string, string> PhaseGatedArms = new(StringComparer.Ordinal)
    {
        ["AssignRunwayCommand"] = "TryApplyTowerCommand only",
        ["GoCommand"] = "TryApplyTowerCommand only (docs/command-handlers.md: GO is in neither IsGroundCommand nor IsTowerCommand)",
        ["TaxiAutoCommand"] = "TryApplyTowerCommand only",
    };

    public ActionRoutingCompletenessTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void EveryParsedCommand_HasAReplayRoute_OrIsBanked()
    {
        var unrouted = new List<string>();
        var undummied = new List<string>();

        foreach (var type in ParsedCommandDummies.ConcreteTypes())
        {
            var dummy = ParsedCommandDummies.Create(type);
            if (dummy is null)
            {
                undummied.Add(type.Name);
                continue;
            }

            if (RecordedCommandClassifier.ClassifyParsed(dummy).Kind != RecordedCommandKind.Compound)
            {
                continue;
            }

            if (!DispatcherHasArm(dummy))
            {
                unrouted.Add(type.Name);
            }
        }

        Assert.True(undummied.Count == 0, "No dummy could be built for: " + string.Join(", ", undummied));

        var newlyUnrouted = unrouted.Where(name => !KnownUnrouted.ContainsKey(name) && !PhaseGatedArms.ContainsKey(name)).ToList();
        var stale = KnownUnrouted.Keys.Concat(PhaseGatedArms.Keys).Where(name => !unrouted.Contains(name)).ToList();
        var problems = new List<string>();
        if (newlyUnrouted.Count > 0)
        {
            problems.Add(
                "Recorded live and dropped on every replay — add a RecordedCommandKind + arm, or bank with a reason: "
                    + string.Join(", ", newlyUnrouted)
            );
        }

        if (stale.Count > 0)
        {
            problems.Add(
                "Now visible to the probe — remove from KnownUnrouted / PhaseGatedArms so the lists only shrink deliberately: "
                    + string.Join(", ", stale)
            );
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Whether <see cref="CommandDispatcher"/> owns an arm for the command. Tower and ground verbs are owned by
    /// construction — the dispatcher's own family predicates route them into the phase-gated arms, which a probe
    /// without a phase cannot reach — except <c>TAXIALL</c>, whose arm is a refusal naming the engine as the owner.
    /// Everything else is probed through <see cref="CommandDispatcher.Dispatch"/> on an airborne, phase-less
    /// aircraft: the no-arm fallback returns exactly "Unable to {natural description}", and an arm that runs and
    /// throws on the placeholder arguments is still an arm.
    /// </summary>
    private static bool DispatcherHasArm(ParsedCommand dummy)
    {
        if (dummy is TaxiAllCommand)
        {
            return false;
        }

        if (CommandDescriber.IsTowerCommand(dummy) || CommandDescriber.IsGroundCommand(dummy))
        {
            return true;
        }

        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.80, -122.30),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
        };

        string noArmMessage;
        try
        {
            noArmMessage = $"Unable to {CommandDescriber.DescribeNatural(dummy)}";
        }
        catch (Exception)
        {
            noArmMessage = "";
        }

        try
        {
            var result = CommandDispatcher.Dispatch(dummy, aircraft, TestDispatch.Context(new Random(0)));
            return noArmMessage.Length == 0 || result.Message != noArmMessage;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
