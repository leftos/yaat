using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft is stopped on the ground (taxiway, runway, or ramp) awaiting instructions.
/// Catch-all idle state that prevents the aircraft from becoming phase-less after
/// taxi completion, runway crossing completion, air taxi arrival, or any other
/// ground operation that finishes without a specific successor phase.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class HoldingInPositionPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingInPositionPhase");

    public HoldingInPositionPhase() { }

    /// <summary>
    /// The hold a <c>CLRWY</c> ends in when its stop was cut back with the nose at another runway's holding position marking
    /// and the tail still inside the cleared runway's (<see cref="ClearRunwayPhase"/>): the pilot reports holding short of
    /// <paramref name="heldShortOfRunway"/>, not clear of <paramref name="tailInsideRunway"/>; only not clear of it when the nose
    /// was already past the other runway's marking.
    /// </summary>
    /// <param name="heldShortOfRunway">
    /// The other runway whose holding position marking the nose stopped at; null when the nose is already past it.
    /// </param>
    /// <param name="tailInsideRunway">The cleared runway whose holding position marking the tail is still inside.</param>
    /// <param name="notClearReportPositions">The student positions the not-clear report is routed to in solo training.</param>
    public HoldingInPositionPhase(string? heldShortOfRunway, string tailInsideRunway, IReadOnlyCollection<string> notClearReportPositions)
    {
        HeldShortOfRunway = heldShortOfRunway;
        TailInsideRunway = tailInsideRunway;
        NotClearReportPositions = notClearReportPositions;
    }

    /// <summary>
    /// The other runway a <c>CLRWY</c>'s cut-back stop holds the nose short of, with the tail inside
    /// <see cref="TailInsideRunway"/>'s holding position marking, or whose bar set where a crossing that ends at rest stops, when its
    /// nose came to rest short of that bar (<see cref="AfterFollowBreakOff(string, string, IReadOnlyCollection{string})"/>); null for
    /// any other hold in position, including a <c>CLRWY</c> hold or a crossing's whose nose was past the other runway's marking
    /// (<see cref="TailInsideRunway"/> set alone).
    /// </summary>
    public string? HeldShortOfRunway { get; private init; }

    /// <summary>
    /// The runway a <c>CLRWY</c> was clearing, or a crossing that ends at rest crossed
    /// (<see cref="AfterFollowBreakOff(string, string, IReadOnlyCollection{string})"/>), whose holding position marking the tail is
    /// still inside; null otherwise.
    /// </summary>
    public string? TailInsideRunway { get; private init; }

    /// <summary>
    /// The student positions the not-clear report a hold with <see cref="TailInsideRunway"/> set makes is routed to in solo training:
    /// the tower's for a <c>CLRWY</c>'s hold, the ground's and tower's for a crossing's or follow's that ends at rest, as the crossing's
    /// own not-clear call. Empty for any other hold in position. Snapshotted.
    /// </summary>
    public IReadOnlyCollection<string> NotClearReportPositions { get; private init; } = [];

    /// <summary>
    /// True for the hold a <c>HOLD</c> that broke off a <c>FOLLOWG</c> follow ends in (<see cref="AfterFollowBreakOff()"/>): with
    /// nothing after it no route or follow is left to resume, so a <c>RES</c> is refused
    /// (<see cref="GroundCommandHandler.RefusesResumeTaxi"/>). False for every other hold in position, a follow whose lead is
    /// gone included. Snapshotted.
    /// </summary>
    public bool EndsBrokenOffFollow { get; private init; }

    /// <summary>The hold a <c>HOLD</c> that breaks off a <c>FOLLOWG</c> follow ends in (<see cref="EndsBrokenOffFollow"/>).</summary>
    public static HoldingInPositionPhase AfterFollowBreakOff() => new() { EndsBrokenOffFollow = true };

    /// <summary>
    /// The break-off hold (<see cref="EndsBrokenOffFollow"/>) a crossing that ends at rest
    /// (<see cref="CrossingRunwayPhase.EndAtRest"/>) hands over to, recording where it came to rest. With its tail still inside the
    /// crossed runway's holding position marking it is that runway's occupant and reports not clear of it, as after a <c>CLRWY</c> cut
    /// back (AIM 2-3-5.a.1.(a), 4-3-21.b).
    /// </summary>
    /// <param name="heldShortOfRunway">The other runway whose bar the nose stopped at; null when no other runway's bar stopped it.</param>
    /// <param name="tailInsideRunway">The crossed runway whose holding position marking the tail is still inside; null when clear of it.</param>
    /// <param name="notClearReportPositions">The student positions the not-clear report is routed to in solo training.</param>
    public static HoldingInPositionPhase AfterFollowBreakOff(
        string? heldShortOfRunway,
        string? tailInsideRunway,
        IReadOnlyCollection<string> notClearReportPositions
    ) =>
        new()
        {
            EndsBrokenOffFollow = true,
            HeldShortOfRunway = heldShortOfRunway,
            TailInsideRunway = tailInsideRunway,
            NotClearReportPositions = tailInsideRunway is null ? [] : notClearReportPositions,
        };

    public override string Name => "Holding In Position";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        Log.LogDebug(
            "[Hold] {Callsign}: holding in position at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees
        );

        if (TailInsideRunway is { } cleared)
        {
            ReportNotClear(ctx, HeldShortOfRunway, cleared, NotClearReportPositions);
        }
    }

    /// <summary>
    /// The pilot reports holding short of <paramref name="other"/>, not clear of <paramref name="cleared"/>; holding position, not
    /// clear of <paramref name="cleared"/> (<see cref="PilotResponder.BuildHoldingPositionNotClear(AircraftState, string)"/>) when
    /// <paramref name="other"/> is null (the nose is past its marking). Routed to <paramref name="positions"/> in solo training.
    /// </summary>
    private static void ReportNotClear(PhaseContext ctx, string? other, string cleared, IReadOnlyCollection<string> positions)
    {
        string taxiway = ctx.Aircraft.Ground.CurrentTaxiway ?? "taxiway";
        Log.LogDebug(
            "[Hold] {Callsign}: nose {Where} on {Twy}, tail inside {Runway}'s line — reporting not clear",
            ctx.Aircraft.Callsign,
            other is null ? "past another runway's line" : $"at runway {other}'s line",
            taxiway,
            cleared
        );
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            other is null
                ? PilotResponder.BuildHoldingPositionNotClear(ctx.Aircraft, cleared)
                : PilotResponder.BuildHoldingShortNotClearOfRunwayText(ctx.Aircraft, other, taxiway, cleared),
            positions
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        if (TailInsideRunway is null)
        {
            Pilot.TaxiInRequest.TryAnnounce(ctx, ElapsedSeconds, ctx.Aircraft.Phases?.AssignedRunway?.Designator, ctx.Aircraft.Ground.CurrentTaxiway);
        }

        if (
            (PresetTaxiStopLocation(ctx.Aircraft.Ground) is { } location)
            && (ElapsedSeconds >= InitialCallupCall.AfterTaxiArrivalDelaySeconds(ctx.Aircraft.Callsign))
        )
        {
            InitialCallupCall.TryMake(ctx, location);
        }

        return false;
    }

    /// <summary>
    /// Where the after-taxi-arrival call is made from when the aircraft has come to rest at the stop its spawn's preset taxi
    /// ends at (<see cref="AircraftGroundOps.PresetTaxiStop"/>) and the call is still owed: "at spot 9" when its route ended
    /// at that spot, "on taxiway K" when a route with no destination ended on that taxiway. Null anywhere else, so a
    /// controller's TAXI that took the aircraft elsewhere never triggers the call.
    /// </summary>
    private static ReadyToTaxiLocation? PresetTaxiStopLocation(AircraftGroundOps ground)
    {
        if ((ground.InitialCallup != InitialCallupPlan.AfterTaxiArrival) || ground.InitialCallupDecisionProcessed)
        {
            return null;
        }

        return (ground.PresetTaxiStop is { } stop) && (ground.AssignedTaxiRoute is { } route) ? MatchPresetTaxiStop(ground, stop, route) : null;
    }

    private static ReadyToTaxiLocation? MatchPresetTaxiStop(AircraftGroundOps ground, PresetTaxiStop stop, TaxiRoute route)
    {
        if (stop.Kind == PresetTaxiStopKind.Spot)
        {
            return string.Equals(route.DestinationSpot, stop.Name, StringComparison.OrdinalIgnoreCase) ? ReadyToTaxiLocation.Spot(stop.Name) : null;
        }

        bool endedOnTheTaxiway =
            (stop.Kind == PresetTaxiStopKind.RouteEnd)
            && (route.DestinationSpot is null)
            && (route.DestinationParking is null)
            && string.Equals(ground.CurrentTaxiway, stop.Name, StringComparison.OrdinalIgnoreCase);
        return endedOnTheTaxiway ? ReadyToTaxiLocation.OnTaxiway(stop.Name) : null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Pushback or CanonicalCommandType.ForcedPushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.LineUpAndWait => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is holding position on the taxiway; issue RES, a new TAXI/PUSH/ATXI/LAND/LUAW, or DEL"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new HoldingInPositionPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            HeldShortOfRunway = HeldShortOfRunway,
            TailInsideRunway = TailInsideRunway,
            EndsBrokenOffFollow = EndsBrokenOffFollow ? true : null,
            NotClearReportPositions = TailInsideRunway is null ? null : [.. NotClearReportPositions],
        };

    public static HoldingInPositionPhase FromSnapshot(HoldingInPositionPhaseDto dto)
    {
        var phase = new HoldingInPositionPhase
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            HeldShortOfRunway = dto.HeldShortOfRunway,
            TailInsideRunway = dto.TailInsideRunway,
            EndsBrokenOffFollow = dto.EndsBrokenOffFollow ?? false,
            NotClearReportPositions = dto.NotClearReportPositions ?? [],
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
