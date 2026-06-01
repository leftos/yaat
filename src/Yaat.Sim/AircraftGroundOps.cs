using System.Text.Json.Serialization;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Ground-operations state: layout reference, taxi route, parking/taxiway position,
/// hold flags, conflict-detection overrides, pushback heading, and the spawn-readback
/// gate.
/// </summary>
public class AircraftGroundOps
{
    /// <summary>
    /// Cached ground layout for the airport this aircraft is currently at (or about to land at).
    /// Set at lifecycle events: spawn, CLAND, flight plan amend. Used by phases and commands.
    /// Excluded from JSON serialization — layouts are stored separately in recording archives.
    /// </summary>
    [JsonIgnore]
    public AirportGroundLayout? Layout { get; set; }

    /// <summary>
    /// Airport ID reference for the ground layout. When <see cref="Layout"/> is set,
    /// returns its AirportId. When deserialized from JSON (no layout attached), returns the
    /// stored reference so the layout can be reattached by the caller.
    /// </summary>
    public string? LayoutAirportId
    {
        get => Layout?.AirportId ?? _layoutAirportId;
        set => _layoutAirportId = value;
    }

    private string? _layoutAirportId;

    public TaxiRoute? AssignedTaxiRoute { get; set; }
    public string? ParkingSpot { get; set; }
    public string? CurrentTaxiway { get; set; }
    public NavTickDiag? LastNavDiag { get; set; }

    /// <summary>
    /// Active hold directive (HOLDPOSITION or GIVEWAY) or null when the aircraft is
    /// free to move under its phase's normal control. Set by ground command handlers
    /// (TryHoldPosition / TryGiveWay) and cleared by RES, TAXI, auto-resume geometry
    /// (FlightPhysics.UpdateGiveWayResume), and deferred-dispatch firing.
    /// Ground phases honour this via <see cref="IsImmobile"/>; each phase decides how
    /// to bring the aircraft to a stop (decel, snap-to-zero, etc).
    /// </summary>
    public HoldDirective? Hold { get; set; }

    /// <summary>
    /// True when the aircraft is under any active hold directive. All ground phases
    /// must short-circuit their motion logic on this predicate. A convention test
    /// enforces that every <c>IGroundPhase</c> references this property.
    /// </summary>
    public bool IsImmobile => Hold is not null;

    public bool AutoDeleteExempt { get; set; }

    /// <summary>
    /// Per-aircraft auto-delete request raised by a queued <c>ONHS DEL</c> block
    /// (or any queued <see cref="Commands.DeleteCommand"/>) whose trigger has fired.
    /// The hosting sweep — yaat-server's <c>TickProcessor.ProcessAutoDelete</c> in
    /// production, the test's manual sweep in standalone Yaat.Sim tests — observes
    /// this flag, removes the aircraft, and fires the appropriate broadcast chain.
    /// Cleared by <see cref="Commands.CancelAutoDeleteCommand"/> (NODEL).
    /// </summary>
    public bool PendingAutoDelete { get; set; }

    /// <summary>
    /// When true, the active TaxiingPhase raises its straight-line speed cap by
    /// <see cref="CategoryPerformance.TaxiExpediteMultiplier"/>. Cleared on the
    /// next HOLD/RES/HS command — pilots resume normal taxi after any of those.
    /// </summary>
    public bool IsExpeditingTaxi { get; set; }

    /// <summary>
    /// Remaining seconds of BREAK conflict override. While positive, the aircraft
    /// ignores ground conflict speed limits imposed by GroundConflictDetector.
    /// </summary>
    public double ConflictBreakRemainingSeconds { get; set; }

    /// <summary>
    /// Max ground speed (kts) imposed by GroundConflictDetector.
    /// Null = no limit. Reset each tick before conflict detection runs.
    /// </summary>
    public double? SpeedLimit { get; set; }

    /// <summary>
    /// When set, FlightPhysics uses this heading for ground position updates
    /// instead of the aircraft's TrueHeading. Used by pushback (aircraft nose stays
    /// forward while tug pushes it backward along this direction).
    /// </summary>
    public TrueHeading? PushbackTrueHeading { get; set; }

    /// <summary>
    /// Set by AtParkingPhase after the spawn check-in line has been emitted once.
    /// Prevents the ready-to-taxi readback from spamming the terminal every tick the
    /// aircraft sits at the gate. Snapshot-serialized so replays produce identical pilot output.
    /// </summary>
    public bool HasAnnouncedReady { get; set; }

    /// <summary>
    /// Set after AtParkingPhase evaluates whether this aircraft should make the automatic
    /// solo-training ready-to-taxi call-up. Suppressed aircraft set only this flag so later
    /// milestones can still tell they have not contacted ATC.
    /// </summary>
    public bool InitialCallupDecisionProcessed { get; set; }

    /// <summary>
    /// True when the scenario author preset a TAXI command on this parking aircraft.
    /// The autonomous solo-training ready-to-taxi call-up is suppressed for these
    /// aircraft — the scripted ground sequence covers what the pilot would otherwise
    /// volunteer. Such aircraft also do not count toward
    /// <c>HasSoloParkingInitialCallupSource</c> (the slider availability gate).
    /// </summary>
    public bool IsScriptedDeparture { get; set; }

    /// <summary>
    /// Seconds the current GIVEWAY hold has been active. Accumulated each tick by
    /// <see cref="FlightPhysics.UpdateGiveWayResume"/> while <see cref="Hold"/> is a GIVEWAY
    /// directive, reset to zero whenever no GIVEWAY hold is active. Drives the safety-timeout
    /// auto-release so an aircraft never waits forever on a target that will never pass.
    /// </summary>
    public double HoldElapsedSeconds { get; set; }

    /// <summary>
    /// Seconds this aircraft has been stopped on the ground (ground speed at or below
    /// <see cref="GiveWayConstants.StationarySpeedThresholdKts"/>). Reset when it moves or
    /// goes airborne. A held aircraft reads its yield target's value to decide whether the
    /// target-stationary stalemate-bypass fallback may fire.
    /// </summary>
    public double StationarySeconds { get; set; }

    public AircraftGroundOpsDto ToSnapshot() =>
        new()
        {
            LayoutAirportId = LayoutAirportId,
            AssignedTaxiRoute = AssignedTaxiRoute?.ToSnapshot(),
            ParkingSpot = ParkingSpot,
            CurrentTaxiway = CurrentTaxiway,
            IsHeld = Hold is not null,
            GiveWayTarget = Hold?.YieldTarget,
            AutoDeleteExempt = AutoDeleteExempt,
            PendingAutoDelete = PendingAutoDelete,
            ConflictBreakRemainingSeconds = ConflictBreakRemainingSeconds,
            SpeedLimit = SpeedLimit,
            PushbackTrueHeadingDeg = PushbackTrueHeading?.Degrees,
            HasAnnouncedReady = HasAnnouncedReady,
            InitialCallupDecisionProcessed = InitialCallupDecisionProcessed,
            IsScriptedDeparture = IsScriptedDeparture,
            IsExpeditingTaxi = IsExpeditingTaxi,
            HoldElapsedSeconds = HoldElapsedSeconds,
            StationarySeconds = StationarySeconds,
        };

    public static AircraftGroundOps FromSnapshot(AircraftGroundOpsDto dto, AirportGroundLayout? layout) =>
        new()
        {
            Layout = layout,
            LayoutAirportId = dto.LayoutAirportId,
            AssignedTaxiRoute = dto.AssignedTaxiRoute is not null ? TaxiRoute.FromSnapshot(dto.AssignedTaxiRoute, layout) : null,
            ParkingSpot = dto.ParkingSpot,
            CurrentTaxiway = dto.CurrentTaxiway,
            Hold = HoldFromSnapshot(dto.IsHeld, dto.GiveWayTarget),
            AutoDeleteExempt = dto.AutoDeleteExempt,
            PendingAutoDelete = dto.PendingAutoDelete,
            ConflictBreakRemainingSeconds = dto.ConflictBreakRemainingSeconds,
            SpeedLimit = dto.SpeedLimit,
            PushbackTrueHeading = dto.PushbackTrueHeadingDeg.HasValue ? new TrueHeading(dto.PushbackTrueHeadingDeg.Value) : null,
            HasAnnouncedReady = dto.HasAnnouncedReady,
            InitialCallupDecisionProcessed = dto.InitialCallupDecisionProcessed,
            IsScriptedDeparture = dto.IsScriptedDeparture,
            IsExpeditingTaxi = dto.IsExpeditingTaxi,
            HoldElapsedSeconds = dto.HoldElapsedSeconds,
            StationarySeconds = dto.StationarySeconds,
        };

    private static HoldDirective? HoldFromSnapshot(bool isHeld, string? giveWayTarget)
    {
        if (!isHeld)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(giveWayTarget) ? HoldDirective.HoldPosition : HoldDirective.GiveWay(giveWayTarget);
    }
}
