using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// <c>CROSS {runway}</c> to an arrival with no taxi route: on the landing rollout or during the runway exit it pre-clears the
/// crossing of the parallel runway the exit leads across (<see cref="PhaseList.PreClearedCrossing"/>, which the exit moves onto
/// its pull-up route's bar); stopped at that runway's bar after the exit, or at the runway bar a completed route ends at, it builds
/// the crossing along the taxiway the aircraft stands on to past the far bar and starts it, as the pull-up crossing does.
/// </summary>
public static partial class GroundCommandHandler
{
    /// <summary>A crossing to build from <paramref name="NearBar"/>, a runway's bar, along <paramref name="Taxiway"/> across that runway.</summary>
    internal sealed record CrossingFromBar(AirportGroundLayout Layout, GroundNode NearBar, string Taxiway);

    /// <summary>
    /// The route from <paramref name="crossing"/>'s near bar along its taxiway across the bar's runway to the far bar, the near bar
    /// cleared: the crossing half of the exit's pull-up route (<see cref="AirportGroundLayout.WalkAcrossToFarHoldShort"/>), with
    /// its hold-shorts placed as the pull-up places them. Null when the taxiway does not carry on across the runway from the bar.
    /// </summary>
    internal static TaxiRoute? BuildCrossingFromBar(CrossingFromBar crossing, AircraftState aircraft, ActiveRunways activeRunways)
    {
        (AirportGroundLayout layout, GroundNode nearBar, string taxiway) = crossing;
        if (
            (nearBar.RunwayId is not { } runwayId)
            || (WalkAcrossFrom(crossing, runwayId) is not { } path)
            || (RunwayExitPhase.BuildRouteSegments(path, taxiway) is not { } segments)
        )
        {
            return null;
        }

        var holdShorts = new List<HoldShortPoint>();
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts, StartPose.Of(aircraft));
        var route = new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
        HoldShortAnnotator.ComputeHoldShortPositions(
            layout,
            route,
            AircraftLength.ResolveFt(aircraft.AircraftType),
            aircraft.Position,
            RunwayCrossingEnd.TailOverNamer(aircraft.Position, layout, activeRunways, aircraft.Phases?.AssignedRunway)
        );
        ClearCrossingAt(route, nearBar.Id);
        Log.LogDebug(
            "{Callsign}: crossing {Rwy} from bar #{Bar} along {Twy}: [{Path}]",
            aircraft.Callsign,
            runwayId,
            nearBar.Id,
            taxiway,
            string.Join("→", path.Select(n => n.Id))
        );
        return route;
    }

    /// <summary>Clears every hold-short of <paramref name="route"/> at <paramref name="barNodeId"/>.</summary>
    internal static void ClearCrossingAt(TaxiRoute route, int barNodeId)
    {
        foreach (HoldShortPoint hs in route.HoldShortPoints.Where(hs => hs.NodeId == barNodeId))
        {
            hs.IsCleared = true;
        }
    }

    /// <summary>
    /// The walk from the near bar across <paramref name="runwayId"/> to its far bar, along the taxiway in whichever direction
    /// crosses it.
    /// </summary>
    private static List<GroundNode>? WalkAcrossFrom(CrossingFromBar crossing, RunwayIdentifier runwayId) =>
        crossing
            .NearBar.Edges.Where(edge => edge.MatchesTaxiway(crossing.Taxiway))
            .Select(edge =>
                crossing.Layout.WalkAcrossToFarHoldShort(crossing.NearBar, edge.OtherNodeId(crossing.NearBar.Id), crossing.Taxiway, runwayId)
            )
            .FirstOrDefault(path => path is not null);

    /// <summary>
    /// Whether a single-runway <c>CROSS</c> is tried without a route first: none assigned, or a completed one, or the aircraft on its
    /// landing rollout, in its runway exit or holding after it, where an assigned route can only be an earlier taxi's (it is not
    /// cleared at takeoff).
    /// </summary>
    private static bool MayCrossWithoutRoute(AircraftState aircraft, TaxiRoute? route) =>
        (route is null) || route.IsComplete || (aircraft.Phases?.CurrentPhase is LandingPhase or RunwayExitPhase or HoldingAfterExitPhase);

    /// <summary>
    /// A single-runway <c>CROSS</c> tried without a route (<see cref="MayCrossWithoutRoute"/>, <see cref="TryCrossWithoutRoute"/>),
    /// else refused for want of one when there is none; null when the route's own pre-clearance applies.
    /// </summary>
    private static CommandResult? TryCrossRoutelessOrRefuse(
        AircraftState aircraft,
        CrossRunwayCommand cross,
        AirportGroundLayout? layout,
        ActiveRunways activeRunways
    )
    {
        TaxiRoute? route = aircraft.ActiveOrStoredTaxiRoute;
        if (MayCrossWithoutRoute(aircraft, route) && (TryCrossWithoutRoute(aircraft, cross, layout, activeRunways) is { } routeless))
        {
            return routeless;
        }

        return (route is null) ? new CommandResult(false, "No taxi route assigned") : null;
    }

    /// <summary>
    /// A single-runway <c>CROSS</c> with no route to pre-clear on: on the rollout or in the runway exit (no route stored behind the
    /// exit, <see cref="PhaseList.TaxiRoute"/>), or stopped after the exit or at the end of a completed route. Null when none of
    /// these applies, so the caller's own refusal stands.
    /// </summary>
    private static CommandResult? TryCrossWithoutRoute(
        AircraftState aircraft,
        CrossRunwayCommand cross,
        AirportGroundLayout? layout,
        ActiveRunways activeRunways
    )
    {
        if ((ResolvedLayout(aircraft, layout) is not { } groundLayout) || !IsRunwayName(cross.RunwayIds[0]))
        {
            return null;
        }

        bool noRoute = aircraft.Phases?.TaxiRoute is null;
        return aircraft.Phases?.CurrentPhase switch
        {
            RunwayExitPhase exit when noRoute => TryPreClearDuringExit(aircraft, exit, cross, groundLayout),
            LandingPhase when noRoute => PreClearOnRollout(aircraft, cross),
            _ => TryCrossFromStandingBar(aircraft, cross, groundLayout, activeRunways),
        };
    }

    /// <summary>
    /// A bare <c>CROSS</c> with no route, stopped after the exit with the nose at another runway's line (a crossover too short for
    /// the aircraft, <see cref="HoldingAfterExitPhase.HeldShortOfRunway"/>): the crossing of that runway. Null otherwise.
    /// </summary>
    private static CommandResult? TryBareCrossWithoutRoute(
        AircraftState aircraft,
        CrossRunwayCommand cross,
        AirportGroundLayout? layout,
        ActiveRunways activeRunways
    )
    {
        if (
            (aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase { HeldShortOfRunway: { } held })
            || (ResolvedLayout(aircraft, layout) is not { } groundLayout)
        )
        {
            return null;
        }

        return TryCrossFromStandingBar(aircraft, cross with { RunwayIds = [held] }, groundLayout, activeRunways);
    }

    /// <summary>
    /// <c>CROSS {runway}</c> in the runway exit. Once the exit is chosen, it is stored (<see cref="PhaseList.PreClearedCrossing"/>)
    /// when the exit leads across that runway, and read back with where it crosses ("cross runway 19R at H"); refused, nothing
    /// stored, when it leads to no crossing of it. Before the exit is chosen it is taken as on the rollout (null when that does not
    /// apply).
    /// </summary>
    private static CommandResult? TryPreClearDuringExit(
        AircraftState aircraft,
        RunwayExitPhase exit,
        CrossRunwayCommand cross,
        AirportGroundLayout layout
    )
    {
        string target = cross.RunwayIds[0];
        string display = RunwayIdentifier.ToDisplayDesignator(target);
        if (exit.HoldTaxiway is not { } taxiway)
        {
            return (aircraft.Phases?.ReportedNoExitAhead == true)
                ? new CommandResult(false, $"CROSS {display}: no exit ahead leads to runway {display}")
                : PreClearOnRollout(aircraft, cross);
        }

        RunwayIdentifier? ahead = exit.ParallelRunwayAhead(layout);
        if ((ahead is null) || !ahead.Value.Contains(target))
        {
            Log.LogDebug("{Callsign}: CROSS {Rwy} refused in the exit: {Twy} leads to {Ahead}", aircraft.Callsign, display, taxiway, ahead);
            return new CommandResult(false, $"CROSS {display}: exit {taxiway} does not lead to runway {display}");
        }

        aircraft.Phases!.PreClearedCrossing = target;
        Log.LogDebug("{Callsign}: CROSS {Rwy} stored in the exit onto {Twy}", aircraft.Callsign, display, taxiway);
        return CommandDispatcher.Ok($"Cross {target}") with { PilotReadback = CrossAtTaxiwayReadback(cross, taxiway) };
    }

    /// <summary>
    /// <c>CROSS {runway}</c> on the rollout before an exit is chosen: stored (<see cref="PhaseList.PreClearedCrossing"/>) for the
    /// exit to apply, with the exit pick biased toward that runway's side as an EL/ER sets it, unless an exit instruction already
    /// stands. Read back as the crossing alone; no taxiway is known yet. Null when the runway is no parallel of the landing runway
    /// (the landing runway itself, a LAHSO runway), so the caller's refusal stands.
    /// </summary>
    private static CommandResult? PreClearOnRollout(AircraftState aircraft, CrossRunwayCommand cross)
    {
        string target = cross.RunwayIds[0];
        PhaseList phases = aircraft.Phases!;
        if (SideToward(phases.AssignedRunway?.Designator, target) is not { } side)
        {
            return null;
        }

        if (phases.RequestedExit is null)
        {
            phases.RequestedExit = new ExitPreference { Side = side };
            phases.CrossExitPreference = phases.RequestedExit;
        }

        phases.PreClearedCrossing = target;
        Log.LogDebug("{Callsign}: CROSS {Rwy} stored on the rollout, exit side {Side}", aircraft.Callsign, target, phases.RequestedExit?.Side);
        HandlerReadback? readback =
            (PhraseologyVerbalizer.VerbalizeTerminal(cross) is { } terminal) && (PhraseologyVerbalizer.Verbalize(cross) is { } spoken)
                ? new HandlerReadback(CanonicalCommandType.CrossRunway, new PilotSpeechText(terminal, spoken))
                : null;
        return CommandDispatcher.Ok($"Cross {target}") with { PilotReadback = readback };
    }

    /// <summary>
    /// The side of <paramref name="landing"/> (an end, e.g. <c>19L</c>) that <paramref name="target"/>, a parallel runway, lies on:
    /// by the end of it with the same number, L left of C left of R. Null when it is not a parallel of the landing runway.
    /// </summary>
    private static ExitSide? SideToward(string? landing, string target)
    {
        if ((landing is null) || (RunwayEndParts(landing) is not { } own))
        {
            return null;
        }

        var targetId = RunwayIdentifier.Parse(target);
        string[] ends = [targetId.End1, targetId.End2];
        foreach (string end in ends)
        {
            if ((RunwayEndParts(end) is { } other) && (other.Number == own.Number) && (other.Order != own.Order))
            {
                return (other.Order > own.Order) ? ExitSide.Right : ExitSide.Left;
            }
        }

        return null;
    }

    /// <summary>A runway end's number and its suffix's order across the field (L 0, C 1, R 2, none 1); null when it has no number.</summary>
    private static (int Number, int Order)? RunwayEndParts(string end)
    {
        string digits = new([.. end.TakeWhile(char.IsDigit)]);
        if (!int.TryParse(digits, out int number))
        {
            return null;
        }

        int order = end[digits.Length..].ToUpperInvariant() switch
        {
            "L" => 0,
            "R" => 2,
            _ => 1,
        };
        return (number, order);
    }

    /// <summary>
    /// <c>CROSS {runway}</c> stopped after the runway exit with no route, or at rest at the end of a completed route: at the
    /// runway's bar on the taxiway it stands on, the crossing along that taxiway to past the far bar starts, read back with where it
    /// crosses. After the exit with no such bar, refused: "taxiway J does not cross runway 19R" when the taxiway has no bar of the
    /// runway, else "not holding short". Null otherwise, so the route's own handling applies.
    /// </summary>
    private static CommandResult? TryCrossFromStandingBar(
        AircraftState aircraft,
        CrossRunwayCommand cross,
        AirportGroundLayout layout,
        ActiveRunways activeRunways
    )
    {
        string target = cross.RunwayIds[0];
        bool afterExit = aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase;
        if ((!afterExit && !StandsAtRouteEnd(aircraft)) || (aircraft.Ground.CurrentTaxiway is not { } taxiway))
        {
            return null;
        }

        string display = RunwayIdentifier.ToDisplayDesignator(target);
        if (StandingBar(aircraft, layout, target, taxiway) is not { } bar)
        {
            if (!afterExit)
            {
                return null;
            }

            return TaxiwayHasBarOf(layout, taxiway, target) ? NotHoldingShortOf(target) : TaxiwayDoesNotCross(display, taxiway);
        }

        if (BuildCrossingFromBar(new CrossingFromBar(layout, bar, taxiway), aircraft, activeRunways) is not { } route)
        {
            return TaxiwayDoesNotCross(display, taxiway);
        }

        StartCrossingFromStand(aircraft, route, bar, layout, activeRunways);
        return CommandDispatcher.Ok($"Cross {target}") with { PilotReadback = CrossAtTaxiwayReadback(cross, taxiway) };
    }

    /// <summary>At rest holding in position where a completed route (or none) left it, with no route stored to follow.</summary>
    private static bool StandsAtRouteEnd(AircraftState aircraft) =>
        (aircraft.Phases is { CurrentPhase: HoldingInPositionPhase, TaxiRoute: null })
        && (aircraft.Ground.AssignedTaxiRoute is null or { IsComplete: true });

    /// <summary>
    /// The bar of <paramref name="target"/> on <paramref name="taxiway"/> the aircraft stands at: within its length, ahead of it or
    /// under it. Null when there is none.
    /// </summary>
    private static GroundNode? StandingBar(AircraftState aircraft, AirportGroundLayout layout, string target, string taxiway)
    {
        double reachFt = AircraftLength.ResolveFt(aircraft.AircraftType);
        return layout
            .Nodes.Values.Where(node => IsBarOn(node, target, taxiway))
            .Select(node => (Node: node, Ft: GeoMath.DistanceNm(aircraft.Position, node.Position) * GeoMath.FeetPerNm))
            .Where(c => (c.Ft <= reachFt) && ((c.Ft <= StopAheadToleranceFt) || IsAhead(aircraft, c.Node.Position, c.Ft)))
            .OrderBy(c => c.Ft)
            .Select(c => c.Node)
            .FirstOrDefault();
    }

    private static bool TaxiwayHasBarOf(AirportGroundLayout layout, string taxiway, string target) =>
        layout.Nodes.Values.Any(node => IsBarOn(node, target, taxiway));

    private static bool IsBarOn(GroundNode node, string target, string taxiway) =>
        (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId is { } runway)
        && runway.Contains(target)
        && node.Edges.Any(edge => edge.MatchesTaxiway(taxiway));

    /// <summary>
    /// The phases that cross from <paramref name="bar"/> along <paramref name="route"/> (built by <see cref="BuildCrossingFromBar"/>)
    /// as a release from a held bar at a completed route crosses: a <see cref="CrossingRunwayPhase"/> to its far bar, which holds
    /// clear past it, then holding in position for a TAXI. The caller marks the route complete, as that release does.
    /// </summary>
    internal static List<Phase> CrossingPhasesFrom(TaxiRoute route, GroundNode bar) =>
        [new CrossingRunwayPhase(bar.Id, route.Segments[^1].ToNodeId, bar.RunwayId?.ToString()), new HoldingInPositionPhase()];

    /// <summary>
    /// Starts <paramref name="route"/>, a crossing from <paramref name="bar"/>, where the aircraft stands: assigned, its taxiway
    /// current, the taxi-in call armed when it stopped short of clearing the landing runway (nose at the parallel's line, tail
    /// inside; it owed none, and will hold past a parallel it had to cross first), and the crossing
    /// (<see cref="CrossingPhasesFrom"/>) in place of the hold. The runway assignment stays.
    /// </summary>
    private static void StartCrossingFromStand(
        AircraftState aircraft,
        TaxiRoute route,
        GroundNode bar,
        AirportGroundLayout layout,
        ActiveRunways activeRunways
    )
    {
        PhaseList phases = aircraft.Phases!;
        if (phases.CurrentPhase is HoldingAfterExitPhase { HeldShortOfRunway: not null })
        {
            aircraft.Ground.AwaitingTaxiInCall = true;
        }

        route.CurrentSegmentIndex = route.Segments.Count;
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Ground.CurrentTaxiway = route.Segments[0].TaxiwayName;
        phases.ReplaceUpcoming(CrossingPhasesFrom(route, bar));
        phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(aircraft, activeRunways, layout));
    }

    /// <summary>
    /// Whether <paramref name="hs"/> is the runway bar ahead that a destination-less route ends at: a CROSS of it extends the route
    /// across, since the route was issued only up to it.
    /// </summary>
    private static bool IsRouteEndBarAhead(TaxiRoute route, HoldShortPoint hs) =>
        hs.EndsNoDestinationRoute && (hs.Reason == HoldShortReason.RunwayCrossing) && (route.SegmentIndexAhead(hs.NodeId) != int.MaxValue);

    /// <summary>
    /// <c>CROSS</c> of the runway bar a destination-less route ends at, before the aircraft reaches it: the route is extended across
    /// to the far bar (<see cref="TryExtendRouteAcrossDestinationRunway"/>), the bar cleared and no longer the route's end.
    /// </summary>
    private static CommandResult TryExtendRouteAcrossEndBar(
        AircraftState aircraft,
        HoldShortPoint endBar,
        string target,
        AirportGroundLayout? layout,
        ActiveRunways activeRunways
    )
    {
        CommandResult result = TryExtendRouteAcrossDestinationRunway(aircraft, endBar, target, layout, activeRunways);
        if (result.Success)
        {
            endBar.EndsNoDestinationRoute = false;
        }

        return result;
    }

    private static CommandResult NotHoldingShortOf(string target)
    {
        string display = RunwayIdentifier.ToDisplayDesignator(target);
        return new CommandResult(false, $"CROSS {display}: not holding short of runway {display}");
    }

    private static CommandResult TaxiwayDoesNotCross(string display, string taxiway) =>
        new(false, $"CROSS {display}: taxiway {taxiway} does not cross runway {display}");
}
