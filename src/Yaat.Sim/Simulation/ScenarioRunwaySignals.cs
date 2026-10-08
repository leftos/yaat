using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

/// <summary>A runway one loaded aircraft is sent to, and whether it departs from it, arrives on it, or both.</summary>
public sealed record AircraftRunwaySignal(RunwayInfo Runway, ActiveRunwayUse Use);

/// <summary>
/// The runways a loaded scenario sends its aircraft and arrival generators to, read from the loader's output without
/// dispatching anything: <see cref="ImpliedActiveRunways.For"/> and <see cref="ScenarioRunwayUse.CountAssigned"/> both read
/// them here, so the prompt's pre-fill and its counts never disagree.
/// </summary>
public static class ScenarioRunwaySignals
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioRunwaySignals");

    /// <summary>
    /// The runways <paramref name="loaded"/> is sent to, in order: its expected approach's runway (an arrival; not for an
    /// aircraft that starts on the ground, which is a departure), then each runway its preset commands name, in preset
    /// order. Presets are parsed with <see cref="CommandParser.ParseCompound"/>, never dispatched; the read carries its own
    /// copy of what an earlier preset changes, as live play would (a runway a command assigns is where later commands
    /// resolve first; <c>APT</c> moves the destination and drops the old one's arrival state; <c>EAPP</c> sets the
    /// approach a later bare clearance takes) and writes nothing.
    /// Conditional and timed presets count.
    /// <para>
    /// A departure use is a taxi to a runway (<c>TAXI</c>, <c>RWY x TAXI</c>, <c>TAXIAUTO</c>, <c>TAXIALL</c>) or a takeoff
    /// clearance or line-up (<c>CTO</c> and its variants, <c>LUAW</c>), which takes the spawn runway or the last runway an
    /// earlier taxi or <c>RWY</c> named; a closed-traffic takeoff (<c>CTOMLT</c>, <c>CTOMRT</c>) comes back, so it is both.
    /// An arrival use is an approach clearance whose procedure resolves the way the command resolves it (a bare <c>CAPP</c>
    /// included), a visual approach, <c>CLAND</c> with a runway, or a pattern entry with a runway; a touch and go, stop and
    /// go, low approach or option is both, on its own runway or the aircraft's last one. <c>RWY x</c> alone is whichever its
    /// command makes it. Pattern commands resolve their runway by the pattern rule (assigned runway, destination, spawn
    /// airport), runway assignments and taxis by the ground rule. A hold short, crossing, expect-approach instruction or exit
    /// names no use. An unparseable preset or an unknown runway is skipped.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AircraftRunwaySignal> AircraftRunways(LoadedAircraft loaded)
    {
        var reader = new AircraftRunwayReader(loaded.State);
        reader.ReadExpectedApproach();
        foreach (PresetCommand preset in loaded.PresetCommands)
        {
            if (Parse(loaded.State, preset.Command) is not { } compound)
            {
                continue;
            }

            foreach (ParsedCommand command in compound.Blocks.SelectMany(block => block.Commands))
            {
                reader.Read(command, preset.Command);
            }
        }

        return reader.Signals;
    }

    /// <summary>
    /// <see cref="AircraftRunways"/> of <paramref name="loaded"/>, read once per aircraft of <paramref name="result"/>:
    /// <see cref="ImpliedActiveRunways.For"/> and <see cref="ScenarioRunwayUse.CountAssigned"/> both read a load, and share
    /// this read (and its log lines) rather than parse every preset twice. The read is of the load as the loader returned
    /// it, before any preset dispatches; it is kept for as long as <paramref name="result"/> lives.
    /// </summary>
    internal static IReadOnlyList<AircraftRunwaySignal> AircraftRunwaysInLoad(ScenarioLoadResult result, LoadedAircraft loaded) =>
        LoadReads
            .GetValue(result, static _ => new ConcurrentDictionary<LoadedAircraft, IReadOnlyList<AircraftRunwaySignal>>())
            .GetOrAdd(loaded, AircraftRunways);

    private static readonly ConditionalWeakTable<
        ScenarioLoadResult,
        ConcurrentDictionary<LoadedAircraft, IReadOnlyList<AircraftRunwaySignal>>
    > LoadReads = [];

    /// <summary>
    /// The distinct runways the arrival generators of <paramref name="result"/> feed, resolved at the primary airport only and
    /// ordinal-sorted; empty with no primary airport. A generator whose runway does not resolve there is skipped.
    /// </summary>
    public static IReadOnlyList<string> GeneratorArrivalRunways(ScenarioLoadResult result)
    {
        if (result.PrimaryAirportId is not { Length: > 0 } primary)
        {
            return [];
        }

        var runways = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ScenarioGeneratorConfig generator in result.Generators)
        {
            if ((!string.IsNullOrWhiteSpace(generator.Runway)) && (NavigationDatabase.Instance.GetRunway(primary, generator.Runway) is { } runway))
            {
                runways.Add(runway.Designator);
            }
        }

        return [.. runways];
    }

    private static CompoundCommand? Parse(AircraftState state, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ParseResult<CompoundCommand> parsed;
        try
        {
            parsed = CommandParser.ParseCompound(text, state.FlightPlan.Route);
        }
        catch (InvalidOperationException ex)
        {
            // A fix-bearing preset reaches the navigation database, which a reader without one cannot answer.
            Log.LogWarning(ex, "{Callsign}: preset '{Preset}' could not be parsed; no runway read from it", state.Callsign, text);
            return null;
        }

        if (parsed.Value is null)
        {
            Log.LogDebug("{Callsign}: preset '{Preset}' does not parse ({Reason}); no runway read from it", state.Callsign, text, parsed.Reason);
        }

        return parsed.Value;
    }

    /// <summary>What one parsed command says about a runway, and where the runway comes from.</summary>
    private abstract record RunwayMention;

    /// <summary>
    /// A taxi to a runway or a runway assignment, resolved by the ground rule (<see cref="CommandDispatcher.FindRunway"/>);
    /// it becomes the runway a later takeoff clearance departs from. <paramref name="Use"/> null is <c>RWY</c>'s own rule.
    /// </summary>
    private sealed record RunwayAssignment(string RunwayId, ActiveRunwayUse? Use) : RunwayMention;

    /// <summary>A pattern entry, landing clearance or touch and go naming its runway, resolved by the pattern rule.</summary>
    private sealed record PatternRunway(string RunwayId, ActiveRunwayUse Use) : RunwayMention;

    /// <summary>An approach clearance; a null <paramref name="ApproachId"/> is the command's own auto-resolve.</summary>
    private sealed record ApproachClearance(string? ApproachId, string? AirportCode) : RunwayMention;

    private sealed record VisualApproachClearance(string RunwayId, string? AirportCode) : RunwayMention;

    /// <summary>A takeoff clearance or line-up: the spawn runway or the last one a taxi or <c>RWY</c> named.</summary>
    private sealed record TakeoffRunway(ActiveRunwayUse Use) : RunwayMention;

    /// <summary>A pattern command naming no runway: the last runway any earlier signal named, else the spawn runway.</summary>
    private sealed record CurrentRunway(ActiveRunwayUse Use) : RunwayMention;

    private static RunwayMention? Mention(ParsedCommand command) =>
        command switch
        {
            TaxiCommand { DestinationRunway: { } runway } => new RunwayAssignment(runway, ActiveRunwayUse.Departure),
            TaxiAutoCommand { DestinationRunway: { } runway } => new RunwayAssignment(runway, ActiveRunwayUse.Departure),
            TaxiAllCommand { DestinationRunway: { } runway } => new RunwayAssignment(runway, ActiveRunwayUse.Departure),
            AssignRunwayCommand assign => new RunwayAssignment(assign.RunwayId, null),
            ClearedForTakeoffCommand { Departure: ClosedTrafficDeparture } => new TakeoffRunway(ActiveRunwayUse.Both),
            ClearedForTakeoffCommand or ClearedTakeoffPresentCommand or LineUpAndWaitCommand => new TakeoffRunway(ActiveRunwayUse.Departure),
            ClearedApproachCommand capp => new ApproachClearance(capp.ApproachId, capp.AirportCode),
            ClearedApproachStraightInCommand cappsi => new ApproachClearance(cappsi.ApproachId, cappsi.AirportCode),
            JoinApproachCommand japp => new ApproachClearance(japp.ApproachId, japp.AirportCode),
            JoinApproachStraightInCommand jappsi => new ApproachClearance(jappsi.ApproachId, jappsi.AirportCode),
            PositionTurnAltitudeClearanceCommand ptac => new ApproachClearance(ptac.ApproachId, null),
            ClearedVisualApproachCommand cva => new VisualApproachClearance(cva.RunwayId, cva.AirportCode),
            ClearedToLandCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            TouchAndGoCommand tg => DepartsAgain(tg.RunwayId ?? tg.PatternRunwayId),
            StopAndGoCommand sg => DepartsAgain(sg.PatternRunwayId),
            LowApproachCommand la => DepartsAgain(la.PatternRunwayId),
            ClearedForOptionCommand option => DepartsAgain(option.PatternRunwayId),
            EnterLeftDownwindCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterRightDownwindCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterLeftCrosswindCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterRightCrosswindCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterLeftBaseCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterRightBaseCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            EnterFinalCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            MakeLeftTrafficCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            MakeRightTrafficCommand { RunwayId: { } runway } => new PatternRunway(runway, ActiveRunwayUse.Arrival),
            _ => null,
        };

    private static RunwayMention DepartsAgain(string? runway) =>
        runway is null ? new CurrentRunway(ActiveRunwayUse.Both) : new PatternRunway(runway, ActiveRunwayUse.Both);

    /// <summary>
    /// One aircraft's read, in order: the runways named so far, the two an inheriting command can take, and the read's own
    /// copy of the arrival state an earlier preset changes (destination, assigned runway, expected approach, destination
    /// runway), seeded from the loaded state.
    /// </summary>
    private sealed class AircraftRunwayReader(AircraftState state)
    {
        private RunwayInfo? _departureRunway = state.Phases?.AssignedRunway;
        private RunwayInfo? _currentRunway = state.Phases?.AssignedRunway;
        private RunwayInfo? _assignedRunway = state.Phases?.AssignedRunway;
        private string? _destination = state.FlightPlan.Destination;
        private string? _expectedApproach = state.Approach.Expected;
        private string? _destinationRunway = state.Procedure.DestinationRunway;

        public List<AircraftRunwaySignal> Signals { get; } = [];

        /// <summary>
        /// The expected approach the loader gave the aircraft (its own, else the scenario's for the primary). An aircraft that
        /// starts on the ground is a departure: the loader gives a destination-less one the primary approach anyway.
        /// </summary>
        public void ReadExpectedApproach()
        {
            if ((state.IsOnGround) || (_expectedApproach is not { } expected))
            {
                return;
            }

            if (ApproachRunway(expected, null) is { } runway)
            {
                Add(new AircraftRunwaySignal(runway, ActiveRunwayUse.Arrival), mention: null);
            }
            else
            {
                Log.LogDebug("{Callsign}: expected approach {Approach} names no runway", state.Callsign, expected);
            }
        }

        public void Read(ParsedCommand command, string preset)
        {
            switch (command)
            {
                case ChangeDestinationCommand change:
                    ChangeDestination(change.Airport, preset);
                    return;
                case ExpectApproachCommand expect:
                    ExpectApproach(expect, preset);
                    return;
            }

            if (Mention(command) is not { } mention)
            {
                return;
            }

            if (Resolve(mention) is { } signal)
            {
                Add(signal, mention);
            }
            else
            {
                Log.LogDebug("{Callsign}: preset '{Preset}' names no runway the navigation data resolves; skipped", state.Callsign, preset);
            }
        }

        /// <summary>
        /// <c>EAPP</c>: as the command does, the approach resolves (against the read's own state) and, when it does, becomes
        /// the expected approach and its runway the destination runway a later bare clearance takes; one that does not resolve
        /// changes nothing.
        /// </summary>
        private void ExpectApproach(ExpectApproachCommand expect, string preset)
        {
            ApproachCommandHandler.ResolvedApproach resolved = ApproachCommandHandler.ResolveApproach(
                expect.ApproachId,
                expect.AirportCode,
                state,
                ApproachContext()
            );
            if (resolved is { Success: true, Procedure: { } procedure, Runway: { } runway })
            {
                _expectedApproach = procedure.ApproachId;
                _destinationRunway = runway.Designator;
            }
            else
            {
                Log.LogDebug("{Callsign}: preset '{Preset}' expects no approach that resolves; expected approach unchanged", state.Callsign, preset);
            }
        }

        /// <summary>
        /// <c>APT</c>: later commands resolve against the new destination. The runway context goes with every change (it stands
        /// in for the arrival's phase chain, which live play tears down); the expected approach and destination runway go only
        /// when an existing destination changes to another airport, as <c>FlightPlanCommandHandler.TryChangeDestination</c>
        /// drops them.
        /// </summary>
        private void ChangeDestination(string airport, string preset)
        {
            if (!NavigationDatabase.Instance.TryResolveAirport(airport, out string canonical))
            {
                Log.LogDebug("{Callsign}: preset '{Preset}' names an unknown airport; destination unchanged", state.Callsign, preset);
                return;
            }

            bool leavesAnotherDestination =
                (!string.IsNullOrEmpty(_destination)) && (!NavigationDatabase.Instance.AirportIdsMatchResolved(_destination, canonical));
            _destination = canonical;
            _assignedRunway = null;
            _currentRunway = null;
            if (leavesAnotherDestination)
            {
                _expectedApproach = null;
                _destinationRunway = null;
            }
        }

        private AircraftRunwaySignal? Resolve(RunwayMention mention) =>
            mention switch
            {
                RunwayAssignment assignment => Signal(CommandDispatcher.FindRunway(state, assignment.RunwayId), GroundUse(assignment, state)),
                PatternRunway pattern => Signal(PatternRunwayAt(pattern.RunwayId), pattern.Use),
                ApproachClearance approach => Signal(ApproachRunway(approach.ApproachId, approach.AirportCode), ActiveRunwayUse.Arrival),
                VisualApproachClearance visual => Signal(VisualApproachRunway(visual.RunwayId, visual.AirportCode), ActiveRunwayUse.Arrival),
                TakeoffRunway takeoff => Signal(_departureRunway, takeoff.Use),
                CurrentRunway current => Signal(_currentRunway, current.Use),
                _ => throw new ArgumentOutOfRangeException(nameof(mention), mention, "unknown runway mention"),
            };

        private static AircraftRunwaySignal? Signal(RunwayInfo? runway, ActiveRunwayUse use) =>
            runway is null ? null : new AircraftRunwaySignal(runway, use);

        /// <summary>A taxi's use is its own; a bare <c>RWY</c> is an arrival or a departure by the command's own rule.</summary>
        private static ActiveRunwayUse GroundUse(RunwayAssignment assignment, AircraftState state)
        {
            if (assignment.Use is { } use)
            {
                return use;
            }

            return GroundCommandHandler.IsArrivalRunwayAssignment(state) ? ActiveRunwayUse.Arrival : ActiveRunwayUse.Departure;
        }

        private RunwayInfo? PatternRunwayAt(string runwayId)
        {
            string airport = PatternCommandHandler.ResolveAirportContext(_assignedRunway, _destination, state.AirportId);
            return string.IsNullOrEmpty(airport) ? null : NavigationDatabase.Instance.GetRunway(airport, runwayId);
        }

        /// <summary>The read's own arrival state, in the shape an approach command resolves against.</summary>
        private ApproachCommandHandler.ApproachResolutionContext ApproachContext() =>
            new(CommandDispatcher.ResolveAirport(_destination, _assignedRunway), _expectedApproach, _destinationRunway);

        private RunwayInfo? ApproachRunway(string? approachId, string? airportCode)
        {
            ApproachCommandHandler.ResolvedApproach resolved = ApproachCommandHandler.ResolveApproach(
                approachId,
                airportCode,
                state,
                ApproachContext()
            );
            return resolved.Success ? resolved.Runway : null;
        }

        private RunwayInfo? VisualApproachRunway(string runwayId, string? airportCode)
        {
            string fallbackAirport = CommandDispatcher.ResolveAirport(_destination, _assignedRunway);
            bool known = ApproachCommandHandler.TryResolveApproachAirport(airportCode, fallbackAirport, out string airport);
            return (known && (airport.Length > 0)) ? NavigationDatabase.Instance.GetRunway(airport, runwayId) : null;
        }

        /// <summary>
        /// Files <paramref name="signal"/> and tracks what live play would: every runway named becomes the one a bare pattern
        /// command takes; a taxi or <c>RWY</c> runway becomes the one a takeoff clearance departs from; and every runway a
        /// command assigns (all but a takeoff clearance's and a bare pattern command's, which reuse one, and the expected
        /// approach's, <paramref name="mention"/> null, which assigns none) becomes the assigned runway the airport rules read
        /// first.
        /// </summary>
        private void Add(AircraftRunwaySignal signal, RunwayMention? mention)
        {
            Signals.Add(signal);
            _currentRunway = signal.Runway;
            if (mention is RunwayAssignment)
            {
                _departureRunway = signal.Runway;
            }

            if (mention is not (null or TakeoffRunway or CurrentRunway))
            {
                _assignedRunway = signal.Runway;
            }
        }
    }
}
