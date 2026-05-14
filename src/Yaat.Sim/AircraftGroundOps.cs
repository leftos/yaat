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
            ConflictBreakRemainingSeconds = ConflictBreakRemainingSeconds,
            SpeedLimit = SpeedLimit,
            PushbackTrueHeadingDeg = PushbackTrueHeading?.Degrees,
            HasAnnouncedReady = HasAnnouncedReady,
            InitialCallupDecisionProcessed = InitialCallupDecisionProcessed,
            IsScriptedDeparture = IsScriptedDeparture,
            IsExpeditingTaxi = IsExpeditingTaxi,
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
            ConflictBreakRemainingSeconds = dto.ConflictBreakRemainingSeconds,
            SpeedLimit = dto.SpeedLimit,
            PushbackTrueHeading = dto.PushbackTrueHeadingDeg.HasValue ? new TrueHeading(dto.PushbackTrueHeadingDeg.Value) : null,
            HasAnnouncedReady = dto.HasAnnouncedReady,
            InitialCallupDecisionProcessed = dto.InitialCallupDecisionProcessed,
            IsScriptedDeparture = dto.IsScriptedDeparture,
            IsExpeditingTaxi = dto.IsExpeditingTaxi,
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
