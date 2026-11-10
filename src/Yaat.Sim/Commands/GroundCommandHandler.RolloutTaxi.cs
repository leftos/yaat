using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// A TAXI or TAXIAUTO sent while the arrival is on its landing rollout: an instructed exit at the route's first turn-off from the
/// landing runway plus a taxi route stored on <see cref="PhaseList.TaxiRoute"/>, started when the runway exit completes
/// (<see cref="TryStartStoredTaxiRoute"/>). The landing's phases keep running until then.
/// </summary>
public static partial class GroundCommandHandler
{
    /// <summary>How many nodes the join walk follows the exit taxiway away from the runway before it gives up.</summary>
    private const int MaxJoinWalkNodes = 64;

    /// <summary>The sharpest turn, degrees, from the exit taxiway onto the stored route that still counts as joining it ahead.</summary>
    private const double MaxJoinTurnDeg = 100.0;

    /// <summary>The refusal of a rollout TAXI whose route never leaves the landing runway.</summary>
    private const string AlongTheLandingRunwayRefusal = "Cannot taxi along the landing runway during the rollout; issue TAXI after the runway exit";

    /// <summary>
    /// A TAXI to store until the aircraft can take it: the resolve inputs, the working command, and the route resolved from the
    /// aircraft on the runway. A rollout TAXI is one, and so is a TAXI to the LAHSO hold (<see cref="TryStoreTaxiAtLahsoHold"/>).
    /// </summary>
    private sealed record RolloutTaxiRequest(TaxiResolveInputs Inputs, TaxiCommand Taxi, TaxiRoute RouteFromAircraft, TaxiResolution Resolved);

    /// <summary>The route a rollout TAXI stores, and what its readback needs.</summary>
    private sealed record StoredTaxi(TaxiRoute Route, TaxiCommand Taxi, string? OccupiedTaxiway, bool EndsShort, TaxiCommand? EffectiveCommand);

    /// <summary>Where a runway exit ends, for joining a stored taxi route from it.</summary>
    /// <param name="HoldShort">The exit's hold-short node, where its route ends.</param>
    /// <param name="CameFrom">The node before it on the exit, toward the runway.</param>
    /// <param name="Taxiway">The taxiway the aircraft holds on.</param>
    /// <param name="RunwayId">The runway the aircraft is leaving.</param>
    internal sealed record StoredRouteJoin(GroundNode HoldShort, GroundNode CameFrom, string Taxiway, string? RunwayId);

    /// <summary>
    /// A TAXI on the rollout whose route resolved from the runway. Its first taxiway — the first one the clearance names, else the
    /// route's first turn-off — is the implied exit when it touches the landing runway (<see cref="TryTaxiViaImpliedExit"/>). One
    /// that does not was resolved from the exits the rollout can make (<see cref="TryTaxiFromMakeableExit"/>) wherever it can list
    /// them, so here the route is stored unjudged behind the rollout's own exit, the join checked when the exit completes.
    /// </summary>
    private static CommandResult TryTaxiOnRollout(LandingPhase landing, RolloutTaxiRequest request)
    {
        AircraftState aircraft = request.Inputs.Aircraft;
        string? runwayId = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Phases?.ClearedRunwayId;
        if ((runwayId is null) || (OffRunwayStart(request.RouteFromAircraft) is not { } offRunway))
        {
            return new CommandResult(false, AlongTheLandingRunwayRefusal);
        }

        TaxiRoute trimmed = TrimRoute(request.RouteFromAircraft, offRunway);
        string firstTaxiway = FirstNamedTaxiway(request.Inputs.AsCleared) ?? TurnOff(trimmed);
        if (FirstTaxiwayTouchesRunway(request.Inputs.Layout, runwayId, firstTaxiway, trimmed))
        {
            return TryTaxiViaImpliedExit(landing, request, trimmed, firstTaxiway);
        }

        TaxiResolution resolved = request.Resolved;
        var stored = new StoredTaxi(trimmed, request.Taxi, resolved.OccupiedTaxiway, resolved.EndsShort, resolved.EffectiveCommand);
        ReplacePreviousRolloutTaxi(landing, aircraft);
        return AcceptStoredTaxi(request.Inputs, stored, null, null);
    }

    /// <summary>The route's own turn-off from the runway: its first taxiway-driven segment's taxiway.</summary>
    private static string TurnOff(TaxiRoute trimmed) => FirstDrivenTaxiway(trimmed) ?? trimmed.Segments[0].TaxiwayName;

    /// <summary>
    /// Whether <paramref name="firstTaxiway"/> touches the landing runway: the route's own turn-off (by construction, even a taxiway
    /// the exit list leaves out — one authored as no turn-off, a crossing taxiway) or one of the runway's exits.
    /// </summary>
    private static bool FirstTaxiwayTouchesRunway(AirportGroundLayout layout, string runwayId, string firstTaxiway, TaxiRoute trimmed) =>
        string.Equals(firstTaxiway, TurnOff(trimmed), StringComparison.OrdinalIgnoreCase)
        || layout.ExitListTaxiways(runwayId).Contains(firstTaxiway, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first taxiway touches the landing runway: it is the implied exit, judged as an <c>EXIT</c> naming it on the side the
    /// route turns off to, at the firm braking limit, or the expedite limit for a <c>TAXI … EXP</c>
    /// (<see cref="LandingPhase.EvaluateImpliedExit"/>). Refused with the crew's
    /// "unable" when it cannot be made or is not ahead; accepted with the route resolved from the exit's hold-short node and the
    /// exit instructed as <see cref="TryExitCommand"/> instructs one.
    /// </summary>
    private static CommandResult TryTaxiViaImpliedExit(LandingPhase landing, RolloutTaxiRequest request, TaxiRoute trimmed, string taxiway)
    {
        AircraftState aircraft = request.Inputs.Aircraft;
        // A TAXI EXP is "exit and taxi without delay": its implied exit is judged as EXIT … EXP is, at the max-effort limit.
        ImpliedExitVerdict implied = landing.EvaluateImpliedExit(aircraft, taxiway, RouteNodeIds(trimmed), request.Taxi.Expedite);
        if (!implied.Verdict.Allowed)
        {
            return new CommandResult(false, implied.Verdict.UnableReason!) { PilotUnable = implied.Verdict.PilotUnable };
        }

        TaxiResolution resolved = request.Resolved;
        var stored = new StoredTaxi(trimmed, request.Taxi, resolved.OccupiedTaxiway, resolved.EndsShort, resolved.EffectiveCommand);
        if (implied.Exit is { } exit)
        {
            TaxiResolution fromExit = ResolveTaxiRouteFromStart(request.Inputs, request.Taxi, exit.HoldShortNode);
            if (fromExit.Route is null)
            {
                return fromExit.Refusal ?? throw new InvalidOperationException("a taxi resolution carries neither a route nor a refusal");
            }

            stored = StoredFrom(request.Inputs, fromExit);
        }

        ReplacePreviousRolloutTaxi(landing, aircraft);

        // As a joining TAXI does (TryTaxiJoiningFromMakeableExit): the rollout's own exit pick prefers the route's side from now on,
        // so a bare EXIT repeating the implied taxiway, which drops the implied side, still turns off toward the route.
        if (implied.Preference.Side is { } side)
        {
            landing.InferSideTowardTaxiRoute(side);
        }

        bool expediteExit = request.Taxi.Expedite || KeepsStandingExpedite(aircraft, implied.Preference);
        return AcceptImpliedExit(request.Inputs, stored, (Exit: implied.Preference, Expedite: expediteExit), implied.Preference);
    }

    /// <summary>
    /// Instructs <c>implied.Exit</c> as <see cref="TryExitCommand"/> instructs one, then stores the route behind it.
    /// <c>implied.Expedite</c> is the exit expedite it is flown under: the one it was judged or listed at, so an exit only the
    /// expedite braking limit makes is braked for at that limit.
    /// <paramref name="announced"/> is the exit instruction the readback leads with — the controller's own, when it named this exit —
    /// and null for an exit the crew picked for itself (<see cref="AcceptStoredTaxi"/>).
    /// </summary>
    private static CommandResult AcceptImpliedExit(
        TaxiResolveInputs inputs,
        StoredTaxi stored,
        (ExitPreference Exit, bool Expedite) implied,
        ExitPreference? announced
    )
    {
        AircraftState aircraft = inputs.Aircraft;
        PhaseList phases = aircraft.Phases!;
        ExitPreference exit = implied.Exit;
        phases.GivenUpExitTaxiways.Remove(exit.Taxiway!);
        aircraft.Ground.IsExpeditingExit = implied.Expedite;
        phases.RequestedExit = exit;
        phases.TaxiImpliedExit = exit;
        return AcceptStoredTaxi(inputs, stored, exit, announced);
    }

    /// <summary>
    /// A rollout TAXI accepted over an earlier one: the side the earlier TAXI's route gave the rollout's exit pick gives way to the
    /// side before it (<see cref="LandingPhase.DropSideTowardTaxiRoute"/>), and the exit that TAXI implied is forgotten, so the new
    /// TAXI's own pick is made as it would be with no TAXI at all. A controller's own <c>EXIT</c>/<c>EL</c>/<c>ER</c> (another
    /// instance) stays. A refused TAXI never reaches here, so a refusal leaves the earlier plan as it was.
    /// </summary>
    private static void ReplacePreviousRolloutTaxi(LandingPhase landing, AircraftState aircraft)
    {
        landing.DropSideTowardTaxiRoute();
        if ((aircraft.Phases is { TaxiImpliedExit: not null } phases) && ReferenceEquals(phases.RequestedExit, phases.TaxiImpliedExit))
        {
            phases.RequestedExit = null;
            phases.TaxiImpliedExit = null;
        }
    }

    /// <summary>
    /// The controller's own exit instruction (<see cref="PhaseList.RequestedExit"/>) when it names the taxiway a joining TAXI picked:
    /// the exit the readback leads with. Null for an exit the crew picked itself — no instruction stands, or the one that does is the
    /// exit an earlier rollout TAXI implied (<see cref="PhaseList.TaxiImpliedExit"/>), or it names another taxiway.
    /// </summary>
    private static ExitPreference? ControllerNamedTheExit(AircraftState aircraft, ExitPreference picked)
    {
        if (
            (aircraft.Phases?.RequestedExit is not { } standing)
            || ReferenceEquals(standing, aircraft.Phases.TaxiImpliedExit)
            || !string.Equals(standing.Taxiway, picked.Taxiway, StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        return standing;
    }

    /// <summary>
    /// A rollout TAXI resolved from the exits the rollout can make right now (<see cref="LandingPhase.MakeableExitsAhead"/>), nearest
    /// first, each from its hold-short node, the first exit taken as the implied exit: for a TAXIAUTO the first whose route leaves the
    /// exit ahead (<see cref="RouteFromExit"/>), the pathfinder's own turn-off from the runway never used; for a TAXI that joins from an
    /// exit (<see cref="JoinsFromAnExit"/>), the first whose taxiway reaches the clearance's first taxiway with no other taxiway between
    /// (<see cref="TryTaxiJoiningFromMakeableExit"/>). Null where the rollout cannot list its exits (a forced rollout, no hold-short
    /// data), for a TAXI whose first taxiway touches the runway, and for one naming no taxiway the layout has: the caller's own path
    /// answers then.
    /// </summary>
    private static CommandResult? TryTaxiFromMakeableExit(
        LandingPhase landing,
        TaxiResolveInputs inputs,
        TaxiCommand working,
        TaxiResolution fromAircraft
    )
    {
        AircraftState aircraft = inputs.Aircraft;
        // The exits are listed at the braking the picked exit is flown under: the expedite limit for a TAXI EXP, and for a TAXI
        // without EXP while a controller's EXIT … EXP stands (as AcceptStoredTaxi keeps it), so the list and the braking agree.
        // The exit an earlier rollout TAXI EXP implied (PhaseList.TaxiImpliedExit) is no standing EXIT … EXP: a TAXI without EXP
        // after it restates the clearance at the firm limit, as the implied-exit and stored paths do.
        bool expedite =
            working.Expedite
            || (
                (aircraft.Phases?.RequestedExit is { } standing)
                && !ReferenceEquals(standing, aircraft.Phases.TaxiImpliedExit)
                && KeepsStandingExpedite(aircraft, standing)
            );
        if ((aircraft.Phases?.AssignedRunway is not { } runway) || (landing.MakeableExitsAhead(aircraft, expedite) is not { } exits))
        {
            return null;
        }

        if (!inputs.Options.IsTaxiAuto)
        {
            return JoinsFromAnExit(inputs, fromAircraft, runway.Designator)
                ? TryTaxiJoiningFromMakeableExit(landing, inputs, working, (Exits: exits, Runway: runway, Expedite: expedite))
                : null;
        }

        CommandResult? pairRefusal = null;
        foreach (ResolvedExitInfo exit in exits)
        {
            TaxiResolution? fromExit = ResolveFromExit(inputs, working, exit);
            pairRefusal ??= PairRefusalOf(fromExit);
            if (JoinsFromExit(fromExit, exit, runway.Designator))
            {
                Log.LogDebug("[TryTaxi] {Callsign}: TAXIAUTO via {Exit}, the first makeable exit with a route", aircraft.Callsign, exit.TaxiwayName);
                var chosen = new ExitPreference { Taxiway = exit.TaxiwayName, Side = exit.Side };
                ReplacePreviousRolloutTaxi(landing, aircraft);
                if (exit.Side is { } side)
                {
                    landing.InferSideTowardTaxiRoute(side);
                }

                return AcceptImpliedExit(inputs, StoredFrom(inputs, fromExit), (Exit: chosen, Expedite: expedite), chosen);
            }
        }

        return pairRefusal is not null ? PassedThrough(inputs, pairRefusal) : NoMakeableExitRefusal(landing, inputs, fromAircraft);
    }

    /// <summary>
    /// The refusal of a route from an exit whose two cleared taxiways are refused as a pair (<see cref="PathfindingFailure.RefusesPair"/>),
    /// or null: it stands in for "unable to exit", since no exit can make the clearance resolvable.
    /// </summary>
    private static CommandResult? PairRefusalOf(TaxiResolution? fromExit) =>
        fromExit is { Route: null, Failure.RefusesPair: true } ? fromExit.Refusal : null;

    /// <summary>
    /// <paramref name="pairRefusal"/>, a pair refusal reached through an exit's route (<see cref="ResolveFromExit"/>, which
    /// resolves at Debug), logged once at the inputs' failure level as the refusal of the clearance.
    /// </summary>
    private static CommandResult PassedThrough(TaxiResolveInputs inputs, CommandResult pairRefusal)
    {
        Log.Log(
            inputs.FailureLogLevel,
            "[TryTaxi] {Callsign}: TAXI {Taxi} refused from every exit — {Reason}",
            inputs.Aircraft.Callsign,
            string.Join(" ", inputs.AsCleared.Path),
            pairRefusal.Message
        );
        return pairRefusal;
    }

    /// <summary>
    /// Whether a TAXI joins its route from an exit rather than turning off onto its first taxiway: no route resolved from the runway,
    /// or the clearance's first taxiway does not touch the landing runway (<see cref="FirstTaxiwayTouchesRunway"/>).
    /// </summary>
    private static bool JoinsFromAnExit(TaxiResolveInputs inputs, TaxiResolution fromAircraft, string runwayId)
    {
        if (fromAircraft.Route is not { } route)
        {
            return true;
        }

        return (OffRunwayStart(route) is { } offRunway)
            && (FirstNamedTaxiway(inputs.AsCleared) is { } first)
            && !FirstTaxiwayTouchesRunway(inputs.Layout, runwayId, first, TrimRoute(route, offRunway));
    }

    /// <summary>
    /// A TAXI that joins its route from an exit (<see cref="JoinsFromAnExit"/>): the first makeable exit — those on a standing
    /// controller EL/ER side first, then those on the side of the clearance's first taxiway (<see cref="JoinSideRank"/>) — whose
    /// route from its hold-short node reaches that taxiway along the exit taxiway alone (<see cref="JoinStoredRoute"/>'s
    /// "connects straight") is the implied exit, the rollout's side inferred toward it; refused "unable {first taxiway}." when none
    /// does. Null when the clearance names no taxiway the layout has.
    /// </summary>
    private static CommandResult? TryTaxiJoiningFromMakeableExit(
        LandingPhase landing,
        TaxiResolveInputs inputs,
        TaxiCommand working,
        (IReadOnlyList<ResolvedExitInfo> Exits, RunwayInfo Runway, bool Expedite) rollout
    )
    {
        AircraftState aircraft = inputs.Aircraft;
        if (
            (FirstNamedTaxiway(inputs.AsCleared) is not { } first)
            || (SideOfTaxiway(inputs.Layout, first, aircraft.Position, rollout.Runway.TrueHeading) is not { } towardRoute)
        )
        {
            return null;
        }

        ExitSide? controllerSide = ControllerExitSide(aircraft);
        CommandResult? pairRefusal = null;
        foreach (ResolvedExitInfo exit in rollout.Exits.OrderBy(e => JoinSideRank(e.Side, controllerSide, towardRoute)))
        {
            TaxiResolution? fromExit = ResolveFromExit(inputs, working, exit);
            pairRefusal ??= PairRefusalOf(fromExit);
            if (
                JoinsFromExit(fromExit, exit, rollout.Runway.Designator)
                && (fromExit.Route is { } route)
                && (route.Segments.FindIndex(s => string.Equals(s.TaxiwayName, first, StringComparison.OrdinalIgnoreCase)) is var onFirst and >= 0)
                && (JoinStoredRoute(Spliced(route, [], onFirst), JoinAt(exit, rollout.Runway.Designator)) is not null)
            )
            {
                Log.LogDebug("[TryTaxi] {Callsign}: TAXI joins {First} straight from the exit {Exit}", aircraft.Callsign, first, exit.TaxiwayName);
                var picked = new ExitPreference { Taxiway = exit.TaxiwayName, Side = exit.Side };
                ReplacePreviousRolloutTaxi(landing, aircraft);
                if (exit.Side is { } side)
                {
                    landing.InferSideTowardTaxiRoute(side);
                }

                ExitPreference? announced = ControllerNamedTheExit(aircraft, picked);
                return AcceptImpliedExit(inputs, StoredFrom(inputs, fromExit), (Exit: picked, rollout.Expedite), announced);
            }
        }

        if (pairRefusal is not null)
        {
            return PassedThrough(inputs, pairRefusal);
        }

        PilotSpeechText unable = PilotResponder.BuildUnableToExit(aircraft, first);
        return new CommandResult(false, unable.Terminal) { PilotUnable = unable };
    }

    /// <summary>
    /// The side of an exit instruction the controller gave (<c>EL</c>/<c>ER</c>, <c>ER J</c>) and that still stands; null when none
    /// stands or the standing one is the exit an earlier rollout TAXI implied (<see cref="PhaseList.TaxiImpliedExit"/>).
    /// </summary>
    private static ExitSide? ControllerExitSide(AircraftState aircraft) =>
        (aircraft.Phases?.RequestedExit is { } standing) && !ReferenceEquals(standing, aircraft.Phases.TaxiImpliedExit) ? standing.Side : null;

    /// <summary>
    /// The order a joining TAXI tries the makeable exits in, nearest first within each rank: the controller's standing side, then
    /// the side toward the route, then the rest.
    /// </summary>
    internal static int JoinSideRank(ExitSide? side, ExitSide? controllerSide, ExitSide towardRoute)
    {
        if ((controllerSide is not null) && (side == controllerSide))
        {
            return 0;
        }

        return side == towardRoute ? 1 : 2;
    }

    /// <summary>The clearance resolved from <paramref name="exit"/>'s hold-short node, route or refusal; null for an exit with no path.</summary>
    private static TaxiResolution? ResolveFromExit(TaxiResolveInputs inputs, TaxiCommand working, ResolvedExitInfo exit) =>
        exit.Path.Count < 2 ? null : ResolveTaxiRouteFromStart(inputs with { FailureLogLevel = LogLevel.Debug }, working, exit.HoldShortNode);

    /// <summary>
    /// Whether <paramref name="fromExit"/> carries a route that joins from <paramref name="exit"/> (<see cref="JoinStoredRoute"/>).
    /// </summary>
    private static bool JoinsFromExit([NotNullWhen(true)] TaxiResolution? fromExit, ResolvedExitInfo exit, string runwayId) =>
        (fromExit?.Route is { } route) && (JoinStoredRoute(route, JoinAt(exit, runwayId)) is not null);

    /// <summary>Where <paramref name="exit"/> ends, for joining a route from it.</summary>
    private static StoredRouteJoin JoinAt(ResolvedExitInfo exit, string runwayId) =>
        new(exit.HoldShortNode, exit.Path[^2], exit.TaxiwayName, runwayId);

    /// <summary>The route resolved from an exit's hold-short node as the route to store, its hold-short positions computed.</summary>
    private static StoredTaxi StoredFrom(TaxiResolveInputs inputs, TaxiResolution fromExit)
    {
        TaxiRoute route = fromExit.Route!;
        HoldShortAnnotator.ComputeHoldShortPositions(
            inputs.Layout,
            route,
            inputs.AircraftLengthFt,
            null,
            RunwayCrossingEnd.TailOverNamer(null, inputs.Layout, inputs.Options.ActiveRunways, null)
        );
        return new StoredTaxi(route, fromExit.Command, fromExit.OccupiedTaxiway, fromExit.EndsShort, fromExit.EffectiveCommand);
    }

    /// <summary>
    /// A TAXIAUTO no makeable exit yields a route for: refused as the route from the aircraft's own turn-off would be judged
    /// (<see cref="LandingPhase.EvaluateImpliedExit"/>: "unable {twy}." or "no {twy} ahead"), "unable {twy}." when that turn-off
    /// would be allowed, and with the route's own refusal when none resolved from the aircraft either.
    /// </summary>
    private static CommandResult NoMakeableExitRefusal(LandingPhase landing, TaxiResolveInputs inputs, TaxiResolution fromAircraft)
    {
        if (fromAircraft.Route is not { } route)
        {
            return fromAircraft.Refusal ?? throw new InvalidOperationException("a taxi resolution carries neither a route nor a refusal");
        }

        if (OffRunwayStart(route) is not { } offRunway)
        {
            return new CommandResult(false, AlongTheLandingRunwayRefusal);
        }

        TaxiRoute trimmed = TrimRoute(route, offRunway);
        string turnOff = TurnOff(trimmed);
        ExitInstructionVerdict verdict = landing
            .EvaluateImpliedExit(inputs.Aircraft, turnOff, RouteNodeIds(trimmed), inputs.AsCleared.Expedite)
            .Verdict;
        if (!verdict.Allowed)
        {
            return new CommandResult(false, verdict.UnableReason!) { PilotUnable = verdict.PilotUnable };
        }

        PilotSpeechText unable = PilotResponder.BuildUnableToExit(inputs.Aircraft, turnOff);
        return new CommandResult(false, unable.Terminal) { PilotUnable = unable };
    }

    /// <summary>
    /// The side of the runway, looking along <paramref name="heading"/> from <paramref name="from"/>, of <paramref name="taxiway"/>'s
    /// node nearest it, nodes on a runway centerline (on neither side) left out; null when the layout has no other node of an edge
    /// of that taxiway.
    /// </summary>
    private static ExitSide? SideOfTaxiway(AirportGroundLayout layout, string taxiway, LatLon from, TrueHeading heading)
    {
        GroundNode? nearest = layout
            .Edges.Where(e => e.MatchesTaxiway(taxiway))
            .SelectMany(e => e.Nodes)
            .Where(n => !IsOnRunwayCenterline(n))
            .MinBy(n => GeoMath.DistanceNm(n.Position, from));
        if (nearest is null)
        {
            return null;
        }

        return GeoMath.SignedCrossTrackDistanceNm(nearest.Position, from, heading) < 0 ? ExitSide.Left : ExitSide.Right;
    }

    /// <summary>
    /// Stores the accepted route, with the destination runway the clearance names, and reads it back. The landing runway's own bar
    /// is cleared (leaving the runway just landed on is not a crossing), the clearance's <c>CROSS</c> keywords and the scenario's
    /// auto-cross apply now — auto-cross never to the landing runway's re-crossing (<see cref="ApplyAutoCrossExceptLandingRunway"/>)
    /// — and <c>NoDelete</c> is taken now; the rest of a taxi start waits for the route to start.
    /// <paramref name="impliedExit"/> is the exit the TAXI implies, whose taxiway the readback's exit clause names already: the via
    /// list leaves it out and its "not in the route issued" note is suppressed. <paramref name="announcedExit"/> is the exit
    /// instruction the readback leads with — the controller's own when it named one — and null when the crew picked the exit itself.
    /// </summary>
    private static CommandResult AcceptStoredTaxi(
        TaxiResolveInputs inputs,
        StoredTaxi stored,
        ExitPreference? impliedExit,
        ExitPreference? announcedExit
    )
    {
        AircraftState aircraft = inputs.Aircraft;
        TaxiRoute route = stored.Route;
        string landedRunway = aircraft.Phases?.ClearedRunwayId ?? aircraft.Phases?.AssignedRunway?.Designator ?? "";
        string? crossLabel = ClearLandingRunwayBar(inputs.Layout, route, landedRunway);
        ApplyAutoCrossExceptLandingRunway(
            route,
            inputs.Options.AutoCrossRunwayMode,
            inputs.Options.ActiveRunways.For(inputs.Layout.AirportId),
            landedRunway
        );
        ApplyCrossKeywords(route, stored.Taxi);
        PrefixClearOfRunwayAdvisory(route);

        // "Taxi without delay" on the rollout is exit and taxi without delay: the exit is flown at the expedite braking, and the
        // route expedite stands for the stored route, which TaxiingPhase reads once the route starts from the exit. A TAXI
        // without EXP restates the clearance and ends an earlier route expedite.
        if (!stored.Taxi.Expedite)
        {
            EndRouteExpedite(aircraft);
        }

        aircraft.Ground.IsExpeditingTaxi = stored.Taxi.Expedite;
        if (impliedExit is null)
        {
            // Stored behind the rollout's own exit: a TAXI without EXP keeps a standing exit expedite only for the exit the
            // controller instructed under it, as a bare EXIT repeating that exit does (KeepsStandingExpedite).
            aircraft.Ground.IsExpeditingExit =
                stored.Taxi.Expedite || ((aircraft.Phases?.RequestedExit is { } standing) && KeepsStandingExpedite(aircraft, standing));
        }

        if (stored.Taxi.Expedite)
        {
            aircraft.Ground.IsExpeditingExit = true;
            // As the standalone EXP does: the rollout drops its cached exit and re-resolves it at the expedite braking limit.
            foreach (LandingPhase landing in aircraft.Phases?.Phases.OfType<LandingPhase>() ?? [])
            {
                landing.ResetExitCandidate();
            }
        }

        if (stored.Taxi.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.NoDeleteRequested = true;
        }

        // Any route still assigned on the landing rollout is an earlier taxi's (a departure's, never cleared on takeoff): it would
        // hide the stored route from every reader of ActiveOrStoredTaxiRoute.
        aircraft.Ground.AssignedTaxiRoute = null;
        // As TryTaxiCore sets it: the taxiways the controller named, so an exit taxiway the clearance left implied is not among them.
        SetClearanceTaxiways(route, inputs.AsCleared, inputs.Options.IsTaxiAuto);

        StoreTaxiRoute(aircraft.Phases!, route, stored.Taxi.DestinationRunway);
        Log.LogInformation(
            "[TryTaxi] {Callsign}: taxi stored behind the rollout exit {Exit}: {Summary}",
            aircraft.Callsign,
            impliedExit?.Taxiway ?? "(rollout's pick)",
            route.ToSummary()
        );

        PilotSpeechText? speech = announcedExit is { Taxiway: not null } ? ImpliedExitReadback(announcedExit, inputs.AsCleared) : null;
        ExitPreference? exitClause = speech is null ? null : announcedExit;
        var readbackInputs = new TaxiReadbackInputs(
            stored.OccupiedTaxiway,
            stored.EndsShort,
            !inputs.Options.IsTaxiAuto,
            impliedExit?.Taxiway,
            exitClause?.Taxiway
        );
        string taxiMessage = WithResponseNotes(
            BuildTaxiReadback(route, inputs.Layout, stored.Taxi, readbackInputs),
            ResponseWarnings(route, inputs.Layout, inputs.Traffic),
            crossLabel,
            null
        );
        if ((exitClause is null) || (speech is null))
        {
            return CommandDispatcher.Ok(taxiMessage) with { EffectiveCommand = stored.EffectiveCommand };
        }

        CanonicalCommandType type = inputs.Options.IsTaxiAuto ? CanonicalCommandType.TaxiAuto : CanonicalCommandType.Taxi;
        string message = $"{ExitInstructionText(exitClause, expedite: false)}, {char.ToLowerInvariant(taxiMessage[0])}{taxiMessage[1..]}";
        Log.LogDebug("[TryTaxi] {Callsign}: implied exit {Taxiway}", aircraft.Callsign, exitClause.Taxiway);
        return CommandDispatcher.Ok(message) with { EffectiveCommand = stored.EffectiveCommand, PilotReadback = new HandlerReadback(type, speech) };
    }

    /// <summary>The one write of a stored taxi route: <see cref="PhaseList.TaxiRoute"/> and the destination runway its TAXI named.</summary>
    private static void StoreTaxiRoute(PhaseList phases, TaxiRoute route, string? destinationRunway)
    {
        phases.TaxiRoute = route;
        phases.StoredTaxiDestinationRunway = destinationRunway;
    }

    /// <summary>
    /// A TAXI to an aircraft held on the runway after a LAHSO stop (<see cref="RunwayHoldingPhase"/>) whose route enters the
    /// runway held short of (<see cref="RouteEntersRunway(AirportGroundLayout, LatLon, TaxiRoute, string)"/>) and carries no
    /// <c>CROSS</c> of it: stored on <see cref="PhaseList.TaxiRoute"/> for the <c>CROSS</c> that releases the hold
    /// (<see cref="StartTaxiStoredAtLahsoHold"/>), the hold kept, and read back with it (<see cref="LahsoHoldTaxiReadback"/>).
    /// The latest instruction wins: a stored TAXI or a stored exit is replaced. Null when the aircraft is not so held, or the
    /// route never enters that runway, or the TAXI clears the crossing itself: the caller installs it, which ends the hold.
    /// </summary>
    private static CommandResult? TryStoreTaxiAtLahsoHold(RolloutTaxiRequest request, HoldShortPoint? leftRunwayBar)
    {
        TaxiResolveInputs inputs = request.Inputs;
        AircraftState aircraft = inputs.Aircraft;
        if (aircraft.Phases is not { CurrentPhase: RunwayHoldingPhase hold } phases)
        {
            return null;
        }

        string crossing = hold.CrossingRunwayId;
        if ((request.Taxi.CrossRunways ?? []).Any(r => HoldShortAnnotator.TargetMatches(crossing, r)))
        {
            Log.LogDebug("[TryTaxi] {Callsign}: the TAXI clears runway {Runway}; runs at once from the LAHSO hold", aircraft.Callsign, crossing);
            return null;
        }

        // Cleared to cross already (a CROSS earlier in the block, or before the hold's next tick ends it): nothing to wait for.
        if (hold.Requirements.Any(static r => r.IsSatisfied))
        {
            Log.LogDebug("[TryTaxi] {Callsign}: already cleared to cross runway {Runway}; runs at once", aircraft.Callsign, crossing);
            return null;
        }

        if (!RouteEntersRunway(inputs.Layout, aircraft.Position, request.RouteFromAircraft, crossing))
        {
            return null;
        }

        // The latest instruction wins: a stored TAXI or a stored exit, with that exit's expedite, is replaced.
        phases.ClearStoredTaxiRoute();
        phases.RequestedExit = null;
        aircraft.Ground.IsExpeditingExit = false;
        return AcceptTaxiAtLahsoHold(request, phases, RunwayIdentifier.ToDisplayDesignator(crossing), leftRunwayBar);
    }

    /// <summary>
    /// Stores the route of a TAXI given at the LAHSO hold (<see cref="TryStoreTaxiAtLahsoHold"/>), with the start a TAXI makes that
    /// does not wait for the route — the expedite restated, <c>NoDelete</c> taken, the cleared taxiways set — and reads it back
    /// with the hold the aircraft stays in.
    /// </summary>
    private static CommandResult AcceptTaxiAtLahsoHold(RolloutTaxiRequest request, PhaseList phases, string display, HoldShortPoint? leftRunwayBar)
    {
        TaxiResolveInputs inputs = request.Inputs;
        AircraftState aircraft = inputs.Aircraft;
        TaxiRoute route = request.RouteFromAircraft;
        if (!request.Taxi.Expedite)
        {
            EndRouteExpedite(aircraft);
        }

        aircraft.Ground.IsExpeditingTaxi = request.Taxi.Expedite;
        if (request.Taxi.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.NoDeleteRequested = true;
        }

        aircraft.Ground.AssignedTaxiRoute = null;
        SetClearanceTaxiways(route, inputs.AsCleared, inputs.Options.IsTaxiAuto);
        StoreTaxiRoute(phases, route, request.Taxi.DestinationRunway);
        Log.LogDebug(
            "[TryTaxi] {Callsign}: taxi stored at the LAHSO hold until cleared to cross runway {Runway}: {Summary}",
            aircraft.Callsign,
            display,
            route.ToSummary()
        );

        TaxiResolution resolved = request.Resolved;
        var readbackInputs = new TaxiReadbackInputs(resolved.OccupiedTaxiway, resolved.EndsShort, !inputs.Options.IsTaxiAuto, null, null);
        string taxiText = BuildTaxiReadback(route, inputs.Layout, request.Taxi, readbackInputs);
        string message = WithResponseNotes(
            $"{taxiText} stored; holding short of runway {display} until cleared to cross",
            ResponseWarnings(route, inputs.Layout, inputs.Traffic),
            null,
            null
        );
        CanonicalCommandType type = inputs.Options.IsTaxiAuto ? CanonicalCommandType.TaxiAuto : CanonicalCommandType.Taxi;
        var readbackSource = new TaxiRouteReadbackSource(
            route,
            inputs.Layout,
            inputs.AsCleared,
            resolved.EndsShort,
            aircraft,
            inputs.Options.ActiveRunways
        )
        {
            LeftRunwayBar = leftRunwayBar,
        };
        return CommandDispatcher.Ok(message) with
        {
            EffectiveCommand = resolved.EffectiveCommand,
            PilotReadback = new HandlerReadback(type, LahsoHoldTaxiReadback(readbackSource, taxiText, display)),
            TaxiRouteReadback = inputs.Options.IsTaxiAuto ? null : readbackSource,
        };
    }

    /// <summary>
    /// The pilot's readback of a TAXI stored at the LAHSO hold: the route read back as any taxi route is
    /// (<see cref="RouteReadback"/>), then the hold the aircraft stays in (", holding short of runway 1L", spoken "…, holding short
    /// of runway one left") unless the route readback already says it: it does when the bar its walk stops at
    /// (<see cref="PhraseologyVerbalizer.RouteReadbackStop"/>) names the held runway (<see cref="HoldShortAnnotator.TargetMatches"/>),
    /// whatever the bar's reason. The clearance in the rule's own words, else the controller-facing <paramref name="taxiText"/>,
    /// stands in when the route reads back nothing, and the hold follows it.
    /// </summary>
    private static PilotSpeechText LahsoHoldTaxiReadback(TaxiRouteReadbackSource source, string taxiText, string display)
    {
        string spelled = PhraseologyVerbalizer.SpellRunway(display);
        // A TAXIAUTO names no taxiways (its route has no clearance taxiways set), so no route readback reads it: its command does.
        PhraseologyVerbalizer.TaxiRouteReadbackInputs? inputs = source.Route.ClearanceTaxiways is null
            ? null
            : RouteReadbackInputs(source, new RouteReadbackCrossings([], [], []));
        PilotSpeechText route = inputs is null ? CommandReadback(source.AsCleared, taxiText) : RenderRouteReadback(source, inputs).Speech;
        if (
            (inputs is not null)
            && (PhraseologyVerbalizer.RouteReadbackStop(inputs) is { } stop)
            && HoldShortAnnotator.TargetMatches(stop.TargetName, display)
        )
        {
            return route;
        }

        return new PilotSpeechText($"{route.Terminal}, holding short of runway {display}", $"{route.Tts}, holding short of runway {spelled}");
    }

    /// <summary>The clearance as the rule words it, else <paramref name="taxiText"/> lower-cased at its start.</summary>
    private static PilotSpeechText CommandReadback(TaxiCommand asCleared, string taxiText)
    {
        string terminal = PhraseologyVerbalizer.VerbalizeTerminal(asCleared) ?? LowerFirst(taxiText);
        return new PilotSpeechText(terminal, PhraseologyVerbalizer.Verbalize(asCleared) ?? terminal);
    }

    /// <summary>
    /// Whether the stored route (<see cref="PhaseList.TaxiRoute"/>) begins at the aircraft on the runway it stands on, as a TAXI given
    /// at the LAHSO hold builds it (the runway-centreline start): its first segment starts within
    /// <see cref="AirportGroundLayout.AtNodeToleranceFt"/> of the aircraft, on its assigned runway's pavement. A route a TAXI stored
    /// on the rollout starts at an exit's hold-short node instead, and is joined from the runway exit after the release.
    /// </summary>
    private static bool StoredRouteStartsAtAircraft(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if ((layout is null) && (aircraft.Phases?.TaxiRoute is not null))
        {
            Log.LogWarning(
                "[TryTaxi] {Callsign}: no ground layout, so the taxi route stored at the LAHSO hold is not started there",
                aircraft.Callsign
            );
        }

        if (
            (layout is null)
            || (aircraft.Phases is not { TaxiRoute.Segments: [{ } first, ..] } phases)
            || (phases.AssignedRunway?.Designator is not { Length: > 0 } standingRunway)
        )
        {
            return false;
        }

        LatLon start = first.Edge.FromNode.Position;
        double fromAircraftFt = GeoMath.DistanceNm(aircraft.Position, start) * GeoMath.FeetPerNm;
        return (fromAircraftFt <= AirportGroundLayout.AtNodeToleranceFt) && TaxiPathfinder.IsOnRunwayPavement(layout, start, standingRunway);
    }

    /// <summary>
    /// Starts the route a TAXI stored at the LAHSO hold (<see cref="TryStoreTaxiAtLahsoHold"/>) as the <c>CROSS</c> releases the
    /// hold: from where the aircraft stands, as a TAXI from the runway starts, by the stored-route start
    /// (<see cref="InstallStoredTaxiRoute"/>), the stored route cleared. No runway exit runs in between. The caller has checked
    /// that the route starts there (<see cref="StoredRouteStartsAtAircraft"/>).
    /// </summary>
    private static void StartTaxiStoredAtLahsoHold(AircraftState aircraft, AirportGroundLayout layout, ActiveRunways activeRunways)
    {
        if (aircraft.Phases is not { TaxiRoute: { } route } phases)
        {
            return;
        }

        string? destinationRunway = phases.StoredTaxiDestinationRunway;
        phases.ClearStoredTaxiRoute();
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, activeRunways, layout);
        phases.Clear(ctx);
        InstallStoredTaxiRoute(ctx, layout, route, destinationRunway);
        Log.LogDebug("[TryTaxi] {Callsign}: stored taxi route started from the LAHSO hold: {Summary}", aircraft.Callsign, route.ToSummary());
    }

    /// <summary>
    /// The scenario's auto-cross on a route stored on the rollout. Each uncleared crossing of <paramref name="landedRunway"/> — the
    /// landing runway's re-crossing after the exit, which needs an explicit <c>CROSS</c> (7110.65 3-10-9.b.2) — is marked
    /// (<see cref="HoldShortPoint.RecrossesLandingRunway"/>), so no auto-cross clears it, now or after a mid-session toggle; then
    /// auto-cross applies as on any route (<see cref="TaxiRouteAutoCross.Apply"/>).
    /// </summary>
    private static void ApplyAutoCrossExceptLandingRunway(
        TaxiRoute route,
        AutoCrossRunwayMode mode,
        IReadOnlyList<ActiveRunway> activeAtAirport,
        string landedRunway
    )
    {
        if (landedRunway.Length > 0)
        {
            var landed = RunwayIdentifier.Parse(landedRunway);
            foreach (HoldShortPoint bar in route.HoldShortPoints)
            {
                if (
                    (bar.Reason == HoldShortReason.RunwayCrossing)
                    && !bar.IsCleared
                    && (bar.TargetName is { Length: > 0 } target)
                    && RunwayIdentifier.Parse(target).Overlaps(landed)
                )
                {
                    bar.RecrossesLandingRunway = true;
                }
            }
        }

        TaxiRouteAutoCross.Apply(route, mode, activeAtAirport);
    }

    /// <summary>
    /// The pilot's readback of a rollout TAXI with an implied exit: the exit clause, side and taxiway only ("exit right at J", spoken
    /// "exit right at juliett"), then the taxi clause, which leaves the leading exit taxiway out by the same rule the controller-facing
    /// message drops its leading one by (<see cref="IsImpliedExitTaxiway"/>): "exit right at J, taxi to runway 30 via C B W W1".
    /// </summary>
    internal static PilotSpeechText ImpliedExitReadback(ExitPreference exit, TaxiCommand asCleared)
    {
        string taxiway = exit.Taxiway!;
        var spokenExit = new ExitPreference { Side = exit.Side, Taxiway = PhraseologyVerbalizer.SpellTaxiway(taxiway) };
        string exitTerminal = LowerFirst(ExitInstructionText(exit, expedite: false));
        string exitTts = LowerFirst(ExitInstructionText(spokenExit, expedite: false));
        asCleared = WithoutTaxiway(asCleared, taxiway);
        if (PhraseologyVerbalizer.Verbalize(asCleared) is not { Length: > 0 } taxiTts)
        {
            return new PilotSpeechText(exitTerminal, exitTts);
        }

        string taxiTerminal = PhraseologyVerbalizer.VerbalizeTerminal(asCleared) ?? taxiTts;
        return new PilotSpeechText($"{exitTerminal}, {taxiTerminal}", $"{exitTts}, {taxiTts}");
    }

    /// <summary><paramref name="text"/> with its first character lower-cased, to run on after a leading clause.</summary>
    private static string LowerFirst(string text) => $"{char.ToLowerInvariant(text[0])}{text[1..]}";

    /// <summary>
    /// The clearance with its leading token naming <paramref name="taxiway"/> (the exit taxiway) left out, with its turn hint; a
    /// later token naming the same taxiway stays, and the controller echo drops the route's leading leg by the same rule
    /// (<see cref="IsImpliedExitTaxiway"/>). The clearance as it is when no token leads with it.
    /// </summary>
    internal static TaxiCommand WithoutTaxiway(TaxiCommand taxi, string taxiway)
    {
        if (LeadingExitTaxiway(taxi.Path, taxiway) is null)
        {
            return taxi;
        }

        List<int> kept = [.. Enumerable.Range(1, taxi.Path.Count - 1)];
        List<TurnDirection?>? hints = taxi.PathTurnHints is { } all ? [.. kept.Select(i => (i < all.Count) ? all[i] : null)] : null;
        return taxi with { Path = [.. kept.Select(i => taxi.Path[i])], PathTurnHints = hints };
    }

    /// <summary>
    /// Starts the route stored behind a runway exit (<see cref="PhaseList.TaxiRoute"/>) as the exit completes: joined from where the
    /// exit ends (<see cref="JoinStoredRoute"/>), then the taxi start <see cref="TryTaxiCore"/> makes — the landing's clearance
    /// dropped, the departure runway assigned (the one the clearance named, else the one the route ends at), the commanded taxi
    /// speed reset — with a <see cref="TaxiingPhase"/> next. When the exit does not reach the route the route is dropped and the
    /// crew says "unable {first taxiway}."; false then, and the exit holds clear as usual.
    /// </summary>
    internal static bool TryStartStoredTaxiRoute(PhaseContext ctx, StoredRouteJoin? join)
    {
        AircraftState aircraft = ctx.Aircraft;
        if ((aircraft.Phases is not { TaxiRoute: { } stored } phases) || (ctx.GroundLayout is not { } layout))
        {
            return false;
        }

        string? destinationRunway = phases.StoredTaxiDestinationRunway;
        phases.ClearStoredTaxiRoute();
        if ((join is null) || (JoinStoredRoute(stored, join) is not { } route))
        {
            string taxiway = FirstOffRunwayTaxiway(layout, stored, join?.RunwayId);
            Log.LogInformation(
                "[TryTaxi] {Callsign}: the exit does not reach the stored route; dropped, unable {Taxiway}",
                aircraft.Callsign,
                taxiway
            );
            PilotResponder.RouteSoloOrRpoTransmission(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                PilotResponder.BuildUnableToExit(aircraft, taxiway),
                PilotResponder.SoloPositionsTower
            );

            // The route the expedite was given for is dropped, and the expedite with it.
            EndRouteExpedite(aircraft);
            return false;
        }

        HoldShortAnnotator.ComputeHoldShortPositions(
            layout,
            route,
            AircraftLength.ResolveFt(aircraft.AircraftType),
            aircraft.Position,
            RunwayCrossingEnd.TailOverNamer(aircraft.Position, layout, ctx.ActiveRunways, aircraft.Phases?.AssignedRunway)
        );

        // The exit is the landing list's last phase: the phase runner ends it there, and nothing after it starts. The taxi runs on
        // a fresh list, as a TAXI issued once the exit is complete gets (TryTaxiCore), so none of the landing's state survives.
        phases.ReplaceUpcoming([]);
        InstallStoredTaxiRoute(ctx, layout, route, destinationRunway);
        Log.LogInformation("[TryTaxi] {Callsign}: stored taxi route started from the exit: {Summary}", aircraft.Callsign, route.ToSummary());
        return true;
    }

    /// <summary>
    /// The taxi start of a stored route (<see cref="PhaseList.TaxiRoute"/>), shared by the runway exit's start
    /// (<see cref="TryStartStoredTaxiRoute"/>) and the LAHSO hold's (<see cref="StartTaxiStoredAtLahsoHold"/>): the route assigned,
    /// the departure runway assigned (<paramref name="destinationRunway"/>, the one the clearance named, else the one the route ends
    /// at), a GIVEWAY given for the route kept, the commanded taxi speed reset, the current taxiway set to the route's first, and a
    /// fresh phase list with a <see cref="TaxiingPhase"/> started. The caller has already ended the phases before it.
    /// </summary>
    private static void InstallStoredTaxiRoute(PhaseContext ctx, AirportGroundLayout layout, TaxiRoute route, string? destinationRunway)
    {
        AircraftState aircraft = ctx.Aircraft;
        DepartureRunwayAssignment prior = CaptureDepartureRunwayAssignment(aircraft);
        RunwayInfo? detectedRunway = destinationRunway is not null
            ? CommandDispatcher.ResolveRunway(aircraft, destinationRunway)
            : DetectRunwayFromRoute(route, layout, aircraft);
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Ground.AwaitingTaxiInCall = false;
        if (aircraft.Ground.Hold?.Kind != HoldKind.GiveWay)
        {
            // A GIVEWAY given for the stored route is kept: it is the controller's instruction for this taxi.
            aircraft.Ground.Hold = null;
        }

        aircraft.Ground.CommandedTaxiSpeedKts = null;
        aircraft.Ground.ParkingSpot = null;
        aircraft.Ground.CurrentTaxiway = route.Segments[0].TaxiwayName;
        PhaseList taxiPhases = InstallFreshTaxiPhaseList(aircraft, detectedRunway, prior, DepartureRunwayClearance.Taxi);
        taxiPhases.Add(new TaxiingPhase());
        taxiPhases.Start(ctx);
    }

    /// <summary>
    /// The stored route as joined from the end of a runway exit, or null when the exit does not reach it: the exit taxiway is
    /// followed away from the runway from its hold-short node until the route leaves from a node on it (or one an edge off it
    /// reaches) heading on, not back; another taxiway met first, a dead end or a runway ends the walk with no join: a node on a
    /// runway centerline, or a runway hold-short node other than the exit's own, is never joined at or walked past, so no runway
    /// bar is run unchecked. The stored route itself when it starts at the hold-short node.
    /// </summary>
    internal static TaxiRoute? JoinStoredRoute(TaxiRoute stored, StoredRouteJoin join)
    {
        List<TaxiRouteSegment> prefix = [];
        GroundNode node = join.HoldShort;
        GroundNode previous = join.CameFrom;
        for (int step = 0; step < MaxJoinWalkNodes; step++)
        {
            if (IsOtherRunwayBar(node, join) || IsOnRunwayCenterline(node))
            {
                return null;
            }

            if (JoinIndex(stored, previous, node) is { } index)
            {
                return Spliced(stored, prefix, index);
            }

            JoinStep next = NextJoinStep(stored, join, node, previous);
            if (next.Leg is { } leg)
            {
                return Spliced(stored, [.. prefix, leg], next.LegIndex);
            }

            if (next.Onward is not { } onward)
            {
                return null;
            }

            GroundNode following = onward.OtherNode(node);
            prefix.Add(new TaxiRouteSegment { TaxiwayName = join.Taxiway, Edge = onward.Directed(node, following) });
            previous = node;
            node = following;
        }

        return null;
    }

    /// <summary>One node of the join walk: the exit taxiway's edge to follow on, or a leg straight onto the stored route at segment
    /// <see cref="LegIndex"/>; neither ends the walk with no join.</summary>
    private readonly record struct JoinStep(IGroundEdge? Onward, TaxiRouteSegment? Leg, int LegIndex);

    /// <summary>
    /// The join walk's step from <paramref name="node"/>, reached from <paramref name="previous"/>: a leg onto the stored route
    /// along an edge off the exit taxiway, else the exit taxiway's edge on when no other taxiway leaves the node. No leg ending at
    /// another runway's hold-short node. The walk never reaches a node on a runway centerline (<see cref="JoinStoredRoute"/>).
    /// </summary>
    private static JoinStep NextJoinStep(TaxiRoute stored, StoredRouteJoin join, GroundNode node, GroundNode previous)
    {
        IGroundEdge? onward = null;
        bool otherTaxiway = false;
        foreach (IGroundEdge edge in node.Edges)
        {
            GroundNode next = edge.OtherNode(node);
            if (next.Id == previous.Id)
            {
                continue;
            }

            if (IsPlainEdgeOf(edge, join.Taxiway))
            {
                onward ??= edge;
                continue;
            }

            if ((!IsOtherRunwayBar(next, join)) && (JoinIndex(stored, node, next) is { } viaIndex))
            {
                var leg = new TaxiRouteSegment { TaxiwayName = stored.Segments[viaIndex].TaxiwayName, Edge = edge.Directed(node, next) };
                return new JoinStep(null, leg, viaIndex);
            }

            otherTaxiway = true;
        }

        return otherTaxiway ? default : new JoinStep(onward, null, 0);
    }

    /// <summary>A runway hold-short node other than the one the exit ends at: a bar the join walk must not run.</summary>
    private static bool IsOtherRunwayBar(GroundNode node, StoredRouteJoin join) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && (node.Id != join.HoldShort.Id);

    /// <summary>A node an edge of a runway centerline meets: on the runway, on neither side of it.</summary>
    private static bool IsOnRunwayCenterline(GroundNode node) => node.Edges.Any(e => e.IsRunwayCenterline);

    /// <summary>The index of the stored route's segment leaving <paramref name="at"/> ahead of an arrival from <paramref name="from"/>.</summary>
    private static int? JoinIndex(TaxiRoute stored, GroundNode from, GroundNode at)
    {
        var arrival = new TrueHeading(GeoMath.BearingTo(from.Position, at.Position));
        for (int i = 0; i < stored.Segments.Count; i++)
        {
            DirectionalEdge edge = stored.Segments[i].Edge;
            if (edge.FromNodeId != at.Id)
            {
                continue;
            }

            var departure = new TrueHeading(GeoMath.BearingTo(at.Position, edge.ToNode.Position));
            if (arrival.AbsAngleTo(departure) <= MaxJoinTurnDeg)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary><paramref name="prefix"/> then the stored route from segment <paramref name="index"/>, keeping its bars on what is kept.</summary>
    private static TaxiRoute Spliced(TaxiRoute stored, List<TaxiRouteSegment> prefix, int index)
    {
        if ((prefix.Count == 0) && (index == 0))
        {
            return stored;
        }

        List<TaxiRouteSegment> kept = [.. prefix, .. stored.Segments.Skip(index)];
        HashSet<int> barNodeIds = [.. stored.Segments.Skip(index).Select(s => s.ToNodeId), stored.Segments[index].FromNodeId];
        return RouteOf(stored, kept, barNodeIds, (FirstKept: index, PrefixCount: prefix.Count));
    }

    /// <summary>The route from its first segment off the runway (<paramref name="offRunway"/>) on, its bars there kept.</summary>
    private static TaxiRoute TrimRoute(TaxiRoute route, int offRunway)
    {
        List<TaxiRouteSegment> kept = [.. route.Segments.Skip(offRunway)];
        return RouteOf(route, kept, [.. kept.Select(s => s.ToNodeId), kept[0].FromNodeId], (FirstKept: offRunway, PrefixCount: 0));
    }

    /// <summary>
    /// <paramref name="source"/> re-cut to <paramref name="segments"/>: its bars on <paramref name="barNodeIds"/> and everything it
    /// carries beyond its segments kept. <paramref name="cut"/> is the source's first segment kept and how many new segments lead in
    /// before it, so a spot line-up's pull (<see cref="TaxiRoute.SpotLineUpPullFromSegment"/>) stays on its segment, and is dropped
    /// when that segment is cut away, as <see cref="TaxiRoute.TruncateAt"/> drops it.
    /// </summary>
    private static TaxiRoute RouteOf(TaxiRoute source, List<TaxiRouteSegment> segments, HashSet<int> barNodeIds, (int FirstKept, int PrefixCount) cut)
    {
        int? pull = (source.SpotLineUpPullFromSegment is { } from) && (from >= cut.FirstKept) ? from - cut.FirstKept + cut.PrefixCount : null;
        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = [.. source.HoldShortPoints.Where(h => barNodeIds.Contains(h.NodeId))],
            Warnings = source.Warnings,
            ImpliedLanes = source.ImpliedLanes,
            ClearanceTaxiways = source.ClearanceTaxiways,
            MandatoryConnectorCount = source.MandatoryConnectorCount,
            DestinationParking = source.DestinationParking,
            DestinationSpot = source.DestinationSpot,
            SpotLineUpPullFromSegment = pull,
            TurnAboutShape = source.TurnAboutShape,
            TurnAboutTargetNodeId = source.TurnAboutTargetNodeId,
        };
    }

    /// <summary>The index of the route's first segment off the runway: not a runway centerline, not a free-space leg. Null when none.</summary>
    private static int? OffRunwayStart(TaxiRoute route)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            IGroundEdge edge = route.Segments[i].Edge.Edge;
            if (!edge.IsRunwayCenterline && !VirtualNode.IsVirtualEdge(edge))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The spacing (ft) at which <see cref="PolylineEntersRunway"/> samples each leg.</summary>
    private const double RouteSampleStepFt = 25.0;

    /// <summary>
    /// Whether <paramref name="route"/>, the leg from <paramref name="aircraftPosition"/> to its first node included, has a point
    /// on <paramref name="runwayId"/>'s pavement (<see cref="PolylineEntersRunway"/>). Each segment is judged along its chord,
    /// from node to node: a fillet arc stays within a few feet of it.
    /// </summary>
    private static bool RouteEntersRunway(AirportGroundLayout layout, LatLon aircraftPosition, TaxiRoute route, string runwayId)
    {
        List<LatLon> polyline = [aircraftPosition];
        if (route.Segments.Count > 0)
        {
            polyline.Add(route.Segments[0].Edge.FromNode.Position);
            polyline.AddRange(route.Segments.Select(s => s.Edge.ToNode.Position));
        }

        return PolylineEntersRunway(layout, polyline, runwayId);
    }

    /// <summary>
    /// Whether <paramref name="polyline"/>, sampled every <see cref="RouteSampleStepFt"/> along each leg and at every vertex, has
    /// a point on <paramref name="runwayId"/>'s pavement: the runway rectangle of
    /// <see cref="RunwayCrossingDetector.IsOnRunway(LatLon, in RunwayRectangle)"/>, so a point in the holding area only does not count.
    /// False, with a warning, when the layout has no such runway.
    /// </summary>
    internal static bool PolylineEntersRunway(AirportGroundLayout layout, IReadOnlyList<LatLon> polyline, string runwayId)
    {
        if (layout.FindRunway(runwayId) is not { } runway)
        {
            Log.LogWarning("[TryTaxi] runway {Runway} is not in the {Airport} layout; a route is judged not to enter it", runwayId, layout.AirportId);
            return false;
        }

        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        for (int i = 0; i < polyline.Count; i++)
        {
            if (
                RunwayCrossingDetector.IsOnRunway(polyline[i], rect)
                || ((i + 1 < polyline.Count) && LegEntersRunway(polyline[i], polyline[i + 1], rect))
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a point sampled every <see cref="RouteSampleStepFt"/> strictly between the leg's ends lies on <paramref name="rect"/>.
    /// </summary>
    private static bool LegEntersRunway(LatLon from, LatLon to, in RunwayRectangle rect)
    {
        double lengthFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
        var heading = new TrueHeading(GeoMath.BearingTo(from, to));
        for (double alongFt = RouteSampleStepFt; alongFt < lengthFt; alongFt += RouteSampleStepFt)
        {
            if (RunwayCrossingDetector.IsOnRunway(GeoMath.ProjectPoint(from, heading, alongFt / GeoMath.FeetPerNm), rect))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every node the route passes.</summary>
    private static HashSet<int> RouteNodeIds(TaxiRoute route) =>
        [.. route.Segments.Select(s => s.FromNodeId), .. route.Segments.Select(s => s.ToNodeId)];

    /// <summary>The first taxiway the clearance names: a path token that is not a node, a spot or a runway.</summary>
    private static string? FirstNamedTaxiway(TaxiCommand clearance) => clearance.Path.FirstOrDefault(t => (t.Length > 0) && char.IsLetter(t[0]));

    /// <summary>The taxiway of the route's first taxiway-driven segment.</summary>
    private static string? FirstDrivenTaxiway(TaxiRoute route) => route.Segments.FirstOrDefault(s => IsTaxiwayDriven(s.Edge.Edge))?.TaxiwayName;

    /// <summary>The route's first taxiway that is not one of <paramref name="runwayId"/>'s exits: the taxiway it was cleared onto.</summary>
    private static string FirstOffRunwayTaxiway(AirportGroundLayout layout, TaxiRoute route, string? runwayId)
    {
        IReadOnlyList<string> exits = runwayId is null ? [] : layout.ExitListTaxiways(runwayId);
        return route
                .Segments.FirstOrDefault(s => IsTaxiwayDriven(s.Edge.Edge) && !exits.Contains(s.TaxiwayName, StringComparer.OrdinalIgnoreCase))
                ?.TaxiwayName
            ?? FirstDrivenTaxiway(route)
            ?? route.Segments[0].TaxiwayName;
    }

    /// <summary>A straight or same-taxiway edge of <paramref name="taxiway"/>: not a junction arc, a runway centerline or a free-space leg.</summary>
    private static bool IsPlainEdgeOf(IGroundEdge edge, string taxiway) => IsTaxiwayDriven(edge) && edge.MatchesTaxiway(taxiway);

    /// <summary>
    /// Clears the landing runway's bar where the route leaves the runway: a crossing of <paramref name="landedRunway"/> at the route's
    /// start or before its first runway hold-short node. Returns the bar's runway for the readback, or null when none was cleared.
    /// </summary>
    private static string? ClearLandingRunwayBar(AirportGroundLayout layout, TaxiRoute route, string landedRunway)
    {
        if ((landedRunway.Length == 0) || (route.Segments.Count == 0))
        {
            return null;
        }

        HashSet<int> leg = [route.Segments[0].FromNodeId];
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            if (layout.Nodes.GetValueOrDefault(segment.FromNodeId)?.Type == GroundNodeType.RunwayHoldShort)
            {
                break;
            }

            leg.Add(segment.ToNodeId);
        }

        string? cleared = null;
        var landed = RunwayIdentifier.Parse(landedRunway);
        foreach (HoldShortPoint bar in route.HoldShortPoints)
        {
            if (
                (bar.Reason == HoldShortReason.RunwayCrossing)
                && leg.Contains(bar.NodeId)
                && (bar.TargetName is { Length: > 0 } target)
                && RunwayIdentifier.Parse(target).Overlaps(landed)
            )
            {
                bar.IsCleared = true;
                cleared ??= target;
            }
        }

        return cleared;
    }
}
