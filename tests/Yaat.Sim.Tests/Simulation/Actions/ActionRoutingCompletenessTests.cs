using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// Every <see cref="ParsedCommand"/> subtype must classify to exactly one <see cref="RecordedCommandKind"/> with an
/// <see cref="ActionScope"/>, and the classifier's notion of "the dispatcher's" (<see cref="RecordedCommandKind.Compound"/>,
/// via <see cref="RecordedCommandClassifier.IsAviationCommand"/>) must agree with <see cref="CommandDispatcher"/> in both
/// directions. Before the classifier was exhaustive, every unclaimed type fell into an aircraft-scoped default and was
/// recorded live then silently dropped by every replay, rewind and bug-bundle export — that is how <c>SQALL</c>,
/// <c>TAXIALL</c>, <c>ADD</c> and the TDLS verbs were lost until the 2026-09-05 audit found them.
///
/// <para>
/// Whether each kind is then <em>applied</em> identically on every run kind is the tick oracle's job (the
/// <c>actions</c> fixture in yaat-server), not this test's: a kind can be classified today and still no-op on a
/// replay path until its arm lands.
/// </para>
/// </summary>
public class ActionRoutingCompletenessTests
{
    /// <summary>
    /// Aviation types whose only dispatcher arm is inside the phase-gated tower switch (<c>TryApplyTowerCommand</c>)
    /// and that belong to neither <see cref="CommandDescriber.IsTowerCommand"/> nor <see cref="CommandDescriber.IsGroundCommand"/>,
    /// so the phase-less probe cannot see the arm. Listed so the probe's blind spot is named rather than papered
    /// over. Asserted exactly: an entry that gains a phase-less arm or a family predicate must leave.
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
    public void EveryParsedCommand_ClassifiesToOneKindWithAScope()
    {
        var unclassified = new List<string>();
        var undummied = new List<string>();

        foreach (var type in ParsedCommandDummies.ConcreteTypes())
        {
            var dummy = ParsedCommandDummies.Create(type);
            if (dummy is null)
            {
                undummied.Add(type.Name);
                continue;
            }

            try
            {
                var classification = RecordedCommandClassifier.ClassifyParsed(dummy);
                Assert.Equal(RecordedCommandClassifier.ScopeOf(classification.Kind), classification.Scope);
            }
            catch (UnroutedCommandException)
            {
                unclassified.Add(type.Name);
            }
        }

        Assert.True(undummied.Count == 0, "No dummy could be built for: " + string.Join(", ", undummied));
        Assert.True(
            unclassified.Count == 0,
            "ParsedCommand subtypes with no RecordedCommandKind — decide what each is addressed to and add an arm to ClassifyParsed: "
                + string.Join(", ", unclassified)
        );
    }

    [Fact]
    public void EveryKind_HasAScope()
    {
        foreach (var kind in Enum.GetValues<RecordedCommandKind>())
        {
            // ScopeOf throws for a kind without a row.
            _ = RecordedCommandClassifier.ScopeOf(kind);
        }
    }

    /// <summary>
    /// Every kind routes: <see cref="ArmTable.For"/> throws for a kind without a row, each row's scope is the
    /// classifier's, and the never-recorded set is exactly the transport verbs, bookmarks and the SHOW query.
    /// </summary>
    [Fact]
    public void EveryKind_HasAnArm()
    {
        var neverRecorded = new List<RecordedCommandKind>();
        foreach (var kind in Enum.GetValues<RecordedCommandKind>())
        {
            var arm = ArmTable.For(kind);
            Assert.Equal(kind, arm.Kind);
            Assert.Equal(RecordedCommandClassifier.ScopeOf(kind), arm.Scope);
            if (arm.Recording == RecordingPolicy.Never)
            {
                neverRecorded.Add(kind);
            }
        }

        Assert.Equal([RecordedCommandKind.ShowQueued, RecordedCommandKind.Bookmark, RecordedCommandKind.Transport], neverRecorded.Order());
    }

    /// <summary>
    /// A type the classifier calls the dispatcher's must reach a dispatcher arm — otherwise replay hands the
    /// dispatcher a verb it has no arm for and the command is dropped with a log line. The reverse is not asserted:
    /// a type with a kind of its own may legitimately also have a dispatcher arm (<c>DEL</c> raises the auto-delete
    /// flag there; the flight-plan verbs are answered with a refusal), because the dispatcher is the aviation
    /// arm's body, not the router.
    /// </summary>
    [Fact]
    public void EveryAviationType_ReachesADispatcherArm()
    {
        var aviationWithoutArm = new List<string>();
        var stale = new List<string>();

        foreach (var type in ParsedCommandDummies.ConcreteTypes())
        {
            var dummy = ParsedCommandDummies.Create(type);
            if (dummy is null)
            {
                continue;
            }

            bool aviation = RecordedCommandClassifier.IsAviationCommand(dummy);
            bool phaseGated = PhaseGatedArms.ContainsKey(type.Name);
            bool hasArm = DispatcherHasArm(dummy);

            if (aviation && !hasArm && !phaseGated)
            {
                aviationWithoutArm.Add(type.Name);
            }

            if (phaseGated && (hasArm || !aviation))
            {
                stale.Add(type.Name);
            }
        }

        var problems = new List<string>();
        if (aviationWithoutArm.Count > 0)
        {
            problems.Add("Listed in IsAviationCommand but CommandDispatcher has no arm: " + string.Join(", ", aviationWithoutArm));
        }

        if (stale.Count > 0)
        {
            problems.Add(
                "PhaseGatedArms entry is stale — the probe sees the arm now, or the type left the aviation list: " + string.Join(", ", stale)
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
