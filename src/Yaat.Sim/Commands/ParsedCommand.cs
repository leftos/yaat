using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Commands;

public readonly struct ParseResult<T>
    where T : class
{
    public T? Value { get; }
    public string? Reason { get; }
    public bool IsSuccess => Value is not null;

    private ParseResult(T? value, string? reason)
    {
        Value = value;
        Reason = reason;
    }

    public static ParseResult<T> Ok(T value) => new(value, null);

    public static ParseResult<T> Fail(string reason) => new(null, reason);
}

public abstract record ParsedCommand;

public record FlyHeadingCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record TurnLeftCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record TurnRightCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record LeftTurnCommand(int Degrees) : ParsedCommand;

public record RightTurnCommand(int Degrees) : ParsedCommand;

public record FlyPresentHeadingCommand : ParsedCommand;

public enum AltitudeAssignmentModifier
{
    None,
    AtOrAbove,
    AtOrBelow,
}

public record ClimbMaintainCommand(int Altitude, AltitudeAssignmentModifier Modifier) : ParsedCommand
{
    public ClimbMaintainCommand(int altitude)
        : this(altitude, AltitudeAssignmentModifier.None) { }
}

public record DescendMaintainCommand(int Altitude) : ParsedCommand;

public enum SpeedModifier
{
    None,
    Floor,
    Ceiling,
}

/// <summary>
/// Maintain-speed assignment. <paramref name="Force"/> true (SPEEDF) overrides the
/// §5-7-1.b.4 "no speed inside 5nm final" rejection and persists past the auto-cancel
/// gate; unlike SPDN it converges via physics rather than teleporting IAS.
/// </summary>
public record SpeedCommand(int Speed, SpeedModifier Modifier = SpeedModifier.None, bool Force = false) : ParsedCommand;

public record ResumeNormalSpeedCommand : ParsedCommand;

public record ReduceToFinalApproachSpeedCommand : ParsedCommand;

public record DeleteSpeedRestrictionsCommand : ParsedCommand;

public record ExpediteCommand(int? UntilAltitude = null) : ParsedCommand;

public record NormalRateCommand : ParsedCommand;

public record MachCommand(double MachNumber) : ParsedCommand;

public record ForceHeadingCommand(MagneticHeading MagneticHeading) : ParsedCommand;

public record ForceAltitudeCommand(int Altitude) : ParsedCommand;

public record ForceSpeedCommand(int Speed) : ParsedCommand;

public record WarpCommand(string PositionLabel, double Latitude, double Longitude, MagneticHeading? MagneticHeading, int? Altitude, int? Speed)
    : ParsedCommand;

public record WarpGroundCommand(string Taxiway1, string Taxiway2, int? NodeId = null, string? ParkingName = null) : ParsedCommand;

public record SquawkCommand(uint Code) : ParsedCommand;

public record SquawkResetCommand : ParsedCommand;

public record SquawkVfrCommand : ParsedCommand;

public record SquawkNormalCommand : ParsedCommand;

public record SquawkStandbyCommand : ParsedCommand;

public record IdentCommand : ParsedCommand;

public record RandomSquawkCommand : ParsedCommand;

public record SquawkAllCommand : ParsedCommand;

public record SquawkNormalAllCommand : ParsedCommand;

public record SquawkStandbyAllCommand : ParsedCommand;

public record DirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record ForceDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record AppendDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record AppendForceDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record TurnLeftDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record TurnRightDirectToCommand(List<ResolvedFix> Fixes, List<string> SkippedFixes) : ParsedCommand;

public record ExpectApproachCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record ResolvedFix(string Name, double Lat, double Lon);

public record SayCommand(string Text) : ParsedCommand;

public record SaySpeedCommand : ParsedCommand;

public record SayMachCommand : ParsedCommand;

public record SayExpectedApproachCommand : ParsedCommand;

public record SayAltitudeCommand : ParsedCommand;

public record SayHeadingCommand : ParsedCommand;

public record SayPositionCommand : ParsedCommand;

public record UnsupportedCommand(string RawText) : ParsedCommand;

// Departure instruction hierarchy for CTO commands
public abstract record DepartureInstruction
{
    public abstract DepartureInstructionDto ToSnapshot();

    public static DepartureInstruction FromSnapshot(DepartureInstructionDto dto) =>
        dto switch
        {
            RunwayHeadingDepartureDto => new RunwayHeadingDeparture(),
            RelativeTurnDepartureDto rt => new RelativeTurnDeparture(rt.Degrees, (TurnDirection)rt.Direction),
            FlyHeadingDepartureDto fh => new FlyHeadingDeparture(
                new MagneticHeading(fh.MagneticHeadingDeg),
                fh.Direction.HasValue ? (TurnDirection)fh.Direction.Value : null
            ),
            OnCourseDepartureDto => new OnCourseDeparture(),
            DirectFixDepartureDto df => new DirectFixDeparture(
                df.FixName,
                df.Lat,
                df.Lon,
                df.Direction.HasValue ? (TurnDirection)df.Direction.Value : null
            ),
            PresentPositionHoverDepartureDto h => new PresentPositionHoverDeparture(h.HoverAltitudeAglFt),
            PatternExitDepartureDto pe => new PatternExitDeparture((PatternEntryLeg)pe.ExitLeg, (PatternDirection)pe.Direction),
            ClosedTrafficDepartureDto ct => new ClosedTrafficDeparture((PatternDirection)ct.Direction, ct.RunwayId, ct.PatternAltitude),
            DefaultDepartureDto => new DefaultDeparture(),
            _ => new DefaultDeparture(),
        };
}

/// <summary>VFR: fly runway heading. IFR: navigate to first route fix.</summary>
public record DefaultDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new DefaultDepartureDto();
}

/// <summary>Fly runway heading (explicit instruction).</summary>
public record RunwayHeadingDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new RunwayHeadingDepartureDto();
}

/// <summary>Turn a relative number of degrees after takeoff (e.g., crosswind = 90°).</summary>
public record RelativeTurnDeparture(int Degrees, TurnDirection Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new RelativeTurnDepartureDto { Degrees = Degrees, Direction = (int)Direction };
}

/// <summary>Fly a specific heading after takeoff, with optional turn direction.</summary>
public record FlyHeadingDeparture(MagneticHeading MagneticHeading, TurnDirection? Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() =>
        new FlyHeadingDepartureDto { MagneticHeadingDeg = MagneticHeading.Degrees, Direction = Direction.HasValue ? (int)Direction.Value : null };
}

/// <summary>On course: fly direct to destination airport.</summary>
public record OnCourseDeparture : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new OnCourseDepartureDto();
}

/// <summary>
/// Present-position hover (helicopter): vertical liftoff, then hold position at
/// <see cref="HoverAltitudeAglFt"/> ft AGL with zero forward speed, awaiting the next
/// instruction. No lateral departure. Produced by bare CTOPP and CTOPP +AGL.
/// </summary>
public record PresentPositionHoverDeparture(int HoverAltitudeAglFt = PresentPositionHoverDeparture.DefaultAltitudeAglFt) : DepartureInstruction
{
    /// <summary>
    /// Default hover-hold altitude (ft AGL) for a bare CTOPP. 25 ft keeps the helicopter in
    /// an in-ground-effect hover — the ceiling a pilot may hold at without requesting a higher
    /// altitude from ATC (AIM 4-3-17.b.2). Use the +AGL form for anything higher.
    /// </summary>
    public const int DefaultAltitudeAglFt = 25;

    public override DepartureInstructionDto ToSnapshot() => new PresentPositionHoverDepartureDto { HoverAltitudeAglFt = HoverAltitudeAglFt };
}

/// <summary>Direct to a named fix after takeoff, with optional turn direction preference.</summary>
public record DirectFixDeparture(string FixName, double Lat, double Lon, TurnDirection? Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() =>
        new DirectFixDepartureDto
        {
            FixName = FixName,
            Lat = Lat,
            Lon = Lon,
            Direction = Direction.HasValue ? (int)Direction.Value : null,
        };
}

/// <summary>Closed traffic: re-enter the pattern after takeoff.</summary>
/// <param name="Direction">Left or right traffic pattern.</param>
/// <param name="RunwayId">Optional runway for the pattern (cross-runway ops). When null, uses the takeoff runway.</param>
/// <param name="PatternAltitude">Optional pattern altitude override (feet MSL).</param>
public record ClosedTrafficDeparture(PatternDirection Direction, string? RunwayId, int? PatternAltitude) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() =>
        new ClosedTrafficDepartureDto
        {
            Direction = (int)Direction,
            RunwayId = RunwayId,
            PatternAltitude = PatternAltitude,
        };
}

/// <summary>
/// Pattern exit departure: fly the traffic pattern up to and including <see cref="ExitLeg"/>,
/// then roll out on that leg's heading and depart the area. A crosswind exit (MRC/MLC) flies
/// upwind then turns crosswind; a downwind exit (MRD/MLD) flies upwind, crosswind, then downwind.
/// Unlike <see cref="ClosedTrafficDeparture"/> the circuit has no base/final/landing tail — the
/// aircraft leaves on the exit-leg heading rather than re-entering to land.
/// </summary>
/// <param name="ExitLeg">The leg the aircraft exits on (Crosswind or Downwind).</param>
/// <param name="Direction">Left or right traffic pattern.</param>
public record PatternExitDeparture(PatternEntryLeg ExitLeg, PatternDirection Direction) : DepartureInstruction
{
    public override DepartureInstructionDto ToSnapshot() => new PatternExitDepartureDto { ExitLeg = (int)ExitLeg, Direction = (int)Direction };
}

// Tower commands
public record LineUpAndWaitCommand : ParsedCommand;

/// <summary>
/// CTO with departure instruction and optional altitude override.
/// </summary>
public record ClearedForTakeoffCommand(DepartureInstruction Departure, int? AssignedAltitude = null) : ParsedCommand
{
    public bool CautionWakeTurbulence { get; init; }
}

public record CancelTakeoffClearanceCommand : ParsedCommand;

public record GoAroundCommand(MagneticHeading? AssignedMagneticHeading, int? TargetAltitude, PatternDirection? TrafficPattern) : ParsedCommand;

public record ClearedToLandCommand(bool NoDelete = false) : ParsedCommand
{
    public bool CautionWakeTurbulence { get; init; }

    /// <summary>
    /// Optional landing runway. Lets a controller clear an aircraft that has no
    /// assigned runway yet — e.g. one still following traffic — to land on a named
    /// runway (<c>CLAND 28R</c>). Null = use the aircraft's already-assigned runway
    /// (or, while following, inherit the lead's runway when it joins the pattern).
    /// </summary>
    public string? RunwayId { get; init; }
}

public record LandAndHoldShortCommand(string CrossingRunwayId) : ParsedCommand;

public record CancelLandingClearanceCommand : ParsedCommand;

/// <summary>
/// CLANDF — instructor/RPO forced landing. Grants landing clearance and forces a touchdown,
/// suppressing every automatic go-around and disregarding the stabilized-approach / too-high
/// energy limits. RPO-only (rejected in solo training). Canceled by GA, CTOC, or touchdown.
/// </summary>
public record ForceLandingCommand : ParsedCommand;

// Pattern commands
public record EnterLeftDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterRightDownwindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterLeftCrosswindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterRightCrosswindCommand(string? RunwayId = null) : ParsedCommand;

public record EnterLeftBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterRightBaseCommand(string? RunwayId = null, double? FinalDistanceNm = null) : ParsedCommand;

public record EnterFinalCommand(string? RunwayId = null) : ParsedCommand;

public record MakeLeftTrafficCommand(string? RunwayId, int? Altitude) : ParsedCommand;

public record MakeRightTrafficCommand(string? RunwayId, int? Altitude) : ParsedCommand;

public record TurnCrosswindCommand : ParsedCommand;

public record TurnDownwindCommand : ParsedCommand;

public record TurnBaseCommand : ParsedCommand;

public record ExtendPatternCommand(PatternEntryLeg? Leg = null) : ParsedCommand;

public record MakeShortApproachCommand : ParsedCommand;

public record MakeLeft360Command : ParsedCommand;

public record MakeRight360Command : ParsedCommand;

public record MakeLeft270Command : ParsedCommand;

public record MakeRight270Command : ParsedCommand;

public record PatternSizeCommand(double SizeNm) : ParsedCommand;

public record MakeNormalApproachCommand : ParsedCommand;

public record Cancel270Command : ParsedCommand;

public record MakeLeftSTurnsCommand(int Count = 2) : ParsedCommand;

public record MakeRightSTurnsCommand(int Count = 2) : ParsedCommand;

/// <summary>
/// OFL / OFFSETL: aircraft on upwind/crosswind/downwind doglegs ~30° left of
/// current pattern heading, acquires a parallel track offset
/// <see cref="OffsetNm"/> NM to the left of the original ground track, then
/// resumes the leg heading. Null offset uses the default 0.5 NM.
/// </summary>
public record OffsetLeftPatternCommand(double? OffsetNm = null) : ParsedCommand;

/// <summary>
/// OFR / OFFSETR: aircraft on upwind/crosswind/downwind doglegs ~30° right of
/// current pattern heading, acquires a parallel track offset
/// <see cref="OffsetNm"/> NM to the right of the original ground track, then
/// resumes the leg heading. Null offset uses the default 0.5 NM.
/// </summary>
public record OffsetRightPatternCommand(double? OffsetNm = null) : ParsedCommand;

public record Plan270Command : ParsedCommand;

public record CircleAirportCommand : ParsedCommand;

// Option approach / special ops commands
public record TouchAndGoCommand(string? RunwayId, PatternDirection? TrafficPattern) : ParsedCommand;

public record StopAndGoCommand(PatternDirection? TrafficPattern) : ParsedCommand;

public record LowApproachCommand(PatternDirection? TrafficPattern) : ParsedCommand;

public record ClearedForOptionCommand(PatternDirection? TrafficPattern) : ParsedCommand;

// Hold commands

/// <summary>HPPL / HPPR: 360-degree orbits at present position.</summary>
public record HoldPresentPosition360Command(TurnDirection Direction) : ParsedCommand;

/// <summary>HPP: helicopter hover at present position.</summary>
public record HoldPresentPositionHoverCommand : ParsedCommand;

/// <summary>HFIXL / HFIXR: fly to fix, then orbit with 360-degree turns.</summary>
public record HoldAtFixOrbitCommand(string FixName, double Lat, double Lon, TurnDirection Direction) : ParsedCommand;

/// <summary>HFIX: helicopter fly to fix and hover.</summary>
public record HoldAtFixHoverCommand(string FixName, double Lat, double Lon) : ParsedCommand;

// Helicopter commands
public record AirTaxiCommand(string? Destination) : ParsedCommand;

public record LandCommand(string SpotName, bool NoDelete = false, bool IsTaxiway = false) : ParsedCommand;

public record ClearedTakeoffPresentCommand(DepartureInstruction Departure, int? AssignedAltitude = null) : ParsedCommand;

// Ground commands
public record PushbackCommand(
    MagneticHeading? MagneticHeading,
    string? Taxiway,
    string? FacingTaxiway,
    string? DestinationParking,
    string? DestinationSpot
) : ParsedCommand;

/// <summary>
/// A taxi clearance via named taxiways. <see cref="PathTurnHints"/> carries an optional per-taxiway
/// turn-direction hint (from the <c>&gt;</c>/<c>&lt;</c> glyph the controller may prefix on a token,
/// e.g. <c>TAXI &gt;A B &lt;C</c>): when non-null it has the same length as <see cref="Path"/> and
/// entry <c>i</c> is the turn the aircraft should make onto <c>Path[i]</c> (null = no hint, pathfinder's
/// own choice). The hint biases junction selection only — it never overrides an otherwise-required route.
/// </summary>
public record TaxiCommand(
    List<string> Path,
    List<string> HoldShorts,
    string? DestinationRunway = null,
    bool NoDelete = false,
    string? DestinationParking = null,
    List<string>? CrossRunways = null,
    string? DestinationSpot = null,
    List<TurnDirection?>? PathTurnHints = null
) : ParsedCommand;

/// <summary>
/// Auto-routed taxi: TAXIAUTO &lt;RWY&gt; or TAXIAUTO @&lt;PARKING&gt;. The handler
/// uses <c>TaxiPathfinder.FindRoute</c> to discover a taxiway sequence from the
/// aircraft's current position to the destination, then delegates to the regular
/// Taxi pipeline so hold-short annotation, auto-crossing, and phase handoff all
/// work identically to a user-typed TAXI command.
/// </summary>
public record TaxiAutoCommand(string? DestinationRunway = null, string? DestinationParking = null) : ParsedCommand;

public record HoldPositionCommand : ParsedCommand;

/// <summary>
/// Resume taxi. <c>CrossRunways</c> pre-clears upcoming
/// <see cref="HoldShortReason.RunwayCrossing"/> hold-shorts on the aircraft's
/// taxi route. <c>HoldShorts</c> adds/promotes hold-shorts further on the route
/// — runway targets promote an upcoming RunwayCrossing to ExplicitHoldShort
/// (survives AutoCross); taxiway targets add a new ExplicitHoldShort at the
/// first matching intersection. Both lists are always non-null; empty for bare
/// RES. Canonical form: <c>RES [CROSS rwy...] [HS target...]</c>.
/// </summary>
public record ResumeCommand(IReadOnlyList<string> CrossRunways, IReadOnlyList<string> HoldShorts) : ParsedCommand;

/// <summary>
/// Cross a runway. When <see cref="RunwayId"/> is non-null, the named runway is
/// cleared (immediate satisfaction if currently holding short of it, otherwise
/// pre-clearing the matching upcoming hold-short on the taxi route). When null
/// (bare <c>CROSS</c>), the very next uncleared hold-short on the route is
/// cleared — works whether the aircraft is currently holding short or still
/// taxiing toward it. Bare form clears exactly one hold-short, so the aircraft
/// still stops at any subsequent hold-shorts.
/// </summary>
public record CrossRunwayCommand(string? RunwayId) : ParsedCommand;

public record HoldShortCommand(string Target) : ParsedCommand;

public record AssignRunwayCommand(string RunwayId) : ParsedCommand;

public record FollowCommand(string? TargetCallsign) : ParsedCommand;

public record FollowGroundCommand(string TargetCallsign) : ParsedCommand;

public record GiveWayCommand(string TargetCallsign, string? Location = null) : ParsedCommand;

public record TaxiAllCommand(string? DestinationRunway = null, string? DestinationParking = null, string? DestinationSpot = null) : ParsedCommand;

public record BreakConflictCommand : ParsedCommand;

/// <summary>
/// CLRWY — pull an aircraft holding short of a taxiway with its tail over a runway (issue #172 W2 state)
/// forward just until it is clear of the runway, then hold in position. Valid only from that tail-over-runway
/// hold; rejected otherwise.
/// </summary>
public record ClearRunwayCommand : ParsedCommand;

public record GoCommand : ParsedCommand;

// Exit commands
public record ExitLeftCommand(bool NoDelete = false, string? Taxiway = null, bool Expedite = false) : ParsedCommand;

public record ExitRightCommand(bool NoDelete = false, string? Taxiway = null, bool Expedite = false) : ParsedCommand;

public record ExitTaxiwayCommand(string Taxiway, bool NoDelete = false, bool Expedite = false) : ParsedCommand;

/// <summary>
/// A compound command consisting of sequential blocks,
/// each containing parallel commands and an optional trigger.
/// </summary>
public record WaitCommand(double Seconds) : ParsedCommand;

public record WaitDistanceCommand(double DistanceNm) : ParsedCommand;

/// <summary>
/// A countdown timer. Set form carries <see cref="Seconds"/> and optional free-text
/// <see cref="Message"/> (defaults to "timer expired" at fire time). Cancel form sets
/// <see cref="IsCancel"/> with either a specific <see cref="CancelId"/> or <see cref="CancelAll"/>.
/// Global vs per-aircraft is conveyed by the dispatch callsign, not stored here.
/// </summary>
public record TimerCommand(double? Seconds, string? Message, bool IsCancel, int? CancelId, bool CancelAll) : ParsedCommand;

public record CompoundCommand(List<ParsedBlock> Blocks)
{
    /// <summary>
    /// Original canonical command text that produced this compound command.
    /// Set by the parser or caller for snapshot serialization of CommandQueue.
    /// </summary>
    public string? SourceText { get; init; }
}

public record ParsedBlock(BlockCondition? Condition, List<ParsedCommand> Commands);

public abstract record BlockCondition;

public record LevelCondition(int Altitude) : BlockCondition;

public record AtFixCondition(string FixName, double Lat, double Lon, int? Radial = null, int? Distance = null) : BlockCondition
{
    /// <summary>
    /// Resolves a fix by name from <see cref="Data.NavigationDatabase.Instance"/> and constructs
    /// the condition with its coordinates cached. Use this when you only have the fix name —
    /// callers that already performed a lookup (the parser caches the resolved position) keep
    /// using the positional ctor directly to avoid a redundant lookup.
    /// Throws <see cref="ArgumentException"/> if the fix is unknown to NavData; callers that
    /// expect the fix may not exist should pre-check via <c>GetFixPosition</c> instead.
    /// </summary>
    public static AtFixCondition FromName(string fixName, int? radial = null, int? distance = null)
    {
        var pos = Data.NavigationDatabase.Instance.GetFixPosition(fixName);
        if (pos is null)
        {
            throw new ArgumentException($"Unknown fix '{fixName}' — NavigationDatabase has no entry.", nameof(fixName));
        }

        return new AtFixCondition(fixName, pos.Value.Lat, pos.Value.Lon, radial, distance);
    }
}

public record GiveWayCondition(string TargetCallsign) : BlockCondition;

public record DistanceFinalCondition(double DistanceNm) : BlockCondition;

public record OnHandoffCondition : BlockCondition;

public record OnHoldShortCondition : BlockCondition;

public enum GroundEntityKind
{
    Taxiway,
    Spot,
    Parking,
    Intersection,
}

/// <summary>
/// AT condition that targets a ground entity rather than an airborne fix or altitude.
/// The parser captures the raw user token only (e.g. "A", "5", "TERM2", "A/B"); resolution
/// to a node id, lat/lon, or canonical taxiway name happens in
/// <see cref="CommandDispatcher"/> where the aircraft's ground layout is reachable.
/// SecondTaxiway is set only for <see cref="GroundEntityKind.Intersection"/>.
/// </summary>
public record AtGroundEntityCondition(GroundEntityKind Kind, string Token, string? SecondTaxiway = null) : BlockCondition;

// Track operations commands
public record SetActivePositionCommand(string TcpCode) : ParsedCommand;

public record TrackAircraftCommand(string? TcpCode = null) : ParsedCommand;

public record DropTrackCommand : ParsedCommand;

public record InitiateHandoffCommand(string? TcpCode) : ParsedCommand;

public record ForceHandoffCommand(string TcpCode) : ParsedCommand;

public record AcceptHandoffCommand(string? Callsign = null) : ParsedCommand;

public record CancelHandoffCommand : ParsedCommand;

/// <summary>
/// CT — controller's frequency-change instruction to the pilot ("contact (facility) on (frequency)").
/// Distinct from the radar handoff (HOO/ACCEPT) because in real ATC the radar transfer and the
/// pilot frequency-change are two separate controller actions (FAA 7110.65 §7-6-11).
/// <para><see cref="Target"/> is an optional position pointer — TCP code, position callsign, or
/// facility id. When null, the handler auto-resolves to <c>Track.HandoffPeer</c> (HOO not yet
/// accepted) or <c>Track.Owner</c> (post-accept).</para>
/// </summary>
public record ContactCommand(string? Target) : ParsedCommand;

/// <summary>
/// FCA — frequency change approved (FAA 7110.65 §7-6-11). Used for VFR transit aircraft leaving
/// the area without a next sector to contact. No state mutation — pure pilot speech.
/// </summary>
public record FrequencyChangeApprovedCommand : ParsedCommand;

/// <summary>
/// CLBRV — explicit VFR Class Bravo clearance. FAA 7110.65 §7-9-2 phraseology accepts
/// "cleared through", "to enter", or "out of" Bravo airspace; YAAT stores one generic gate.
/// </summary>
public record ClearedBravoAirspaceCommand : ParsedCommand;

/// <summary>
/// STBY / ROGER - controller response that uses the aircraft callsign and establishes
/// two-way radio communications for VFR Class C/D entry without issuing a maneuver.
/// </summary>
public record AcknowledgePilotContactCommand : ParsedCommand;

public record AcceptAllHandoffsCommand : ParsedCommand;

public record InitiateHandoffAllCommand(string TcpCode) : ParsedCommand;

public record PointOutCommand(string? TcpCode = null) : ParsedCommand;

public record AcknowledgeCommand : ParsedCommand;

public record RejectPointoutCommand : ParsedCommand;

public record RetractPointoutCommand : ParsedCommand;

public record AcknowledgeConflictAlertCommand : ParsedCommand;

public record InhibitConflictAlertCommand : ParsedCommand;

public record PilotReportedAltitudeCommand(int AltitudeHundreds) : ParsedCommand;

public record LeaderDirectionCommand(int Direction) : ParsedCommand;

/// <summary>
/// Instructor TPA J-Ring overlay on YAAT's own radar (emulates STARS <c>*J</c>). <see cref="Size"/>
/// is the ring radius in nm (1-30, per CRC); non-null only when <see cref="Enable"/> is true.
/// </summary>
public record JRingCommand(bool Enable, double? Size) : ParsedCommand;

/// <summary>
/// Instructor TPA Cone overlay on YAAT's own radar (emulates STARS <c>*P</c>). <see cref="Size"/>
/// is the cone length in nm (1-30, per CRC); non-null only when <see cref="Enable"/> is true.
/// </summary>
public record ConeCommand(bool Enable, double? Size) : ParsedCommand;

public record GhostTrackCommand(string Callsign, string? AirportCode, string? RunwayId, double? Latitude, double? Longitude) : ParsedCommand;

/// <summary>
/// AN (strip annotate) parsed command. <see cref="Box"/> is the canonical
/// annotation slot identifier:
/// <list type="bullet">
/// <item><c>"1"</c>..<c>"9"</c> — 3×3 annotation grid (FieldValues[10..18]).</item>
/// <item><c>"8a"</c> / <c>"8b"</c> — col-3 freeform slots below field 8
///   (FieldValues[19..20]). Left-aligned unlike the grid.</item>
/// </list>
/// The parser normalizes the wire tokens (e.g. <c>AN 10 RV</c>, <c>AN 1 RV</c>,
/// and <c>AN 8A RV</c>) to this canonical form so handlers only need to
/// dispatch on the canonical string.
/// </summary>
// Optional <c>StripId</c> targets a specific full strip (departure, arrival,
// or scanned copy) by its <c>STRIP_…</c> id when set. Null means
// callsign-keyed (the historical default), where the handler synthesizes
// <c>STRIP_{callsign}</c>.
public record StripAnnotateCommand(string Box, string? Text, string? StripId = null) : ParsedCommand;

// Tokens = space-separated args after STRIP; server-side handler greedy-matches a bay
// prefix against accessible bays, then parses the remaining 0..2 tokens as rack/index.
// Wire verb stays "STRIP" for backward compat; internal name aligns with HSM (both "move").
public record StripMoveCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

// SCAN <bay>[/<rack>[/<index>]] — copies the aircraft's full strip into an external
// facility's bay while leaving the original strip untouched. Tokens follow the same
// slash-compound dest-spec format as STRIP (see StripMutations.ResolveStripDest).
// Destination must be an external bay (IsExternal=true on the resolver entry); the
// server handler rejects internal-bay destinations so STRIP stays the verb for
// in-facility moves and SCAN's distinct semantics ("duplicate, don't relocate") are
// preserved.
public record StripScanCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

// <c>StripId</c> targets a specific full strip by id (e.g. a scanned copy
// <c>STRIP_{callsign}_{shortGuid}</c>). Null = callsign-keyed default.
public record StripDeleteCommand(string? StripId = null) : ParsedCommand;

// <c>StripId</c>: same id-form support as <see cref="StripDeleteCommand"/>.
public record StripOffsetCommand(string? StripId = null) : ParsedCommand;

public record HalfStripCreateCommand(string BayName, int? Rack, IReadOnlyList<string> Lines) : ParsedCommand;

public record HalfStripAmendCommand(string? BayName, int? Rack, IReadOnlyList<string> Tokens) : ParsedCommand;

public record HalfStripDeleteCommand(string? BayName, int? Rack, IReadOnlyList<string> Tokens) : ParsedCommand;

// HSE: edit a half-strip's full FieldValues array by stripId. Drives the
// inline 3×2 cell grid so per-cell commits can target a specific strip
// regardless of whether FieldValues[0] is empty or duplicated across
// half-strips. Empty entries clear a slot.
public record HalfStripEditCommand(string StripId, IReadOnlyList<string> Lines) : ParsedCommand;

// HSO / HSS: optional source bay + first-line key (single-word source bay only).
//
// HSM is structurally different: bay names can be multi-word ("Local 1") so the
// destination spec spans multiple whitespace tokens, and the parser cannot
// disambiguate without the bay registry. The parser emits raw tokens and the
// handler resolves greedily — matching STRIP's pattern.
public record HalfStripMoveCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

public record HalfStripOffsetCommand(string? BayName, int? Rack, string? LookupKey) : ParsedCommand;

public record HalfStripSlideCommand(string? BayName, int? Rack, string? LookupKey) : ParsedCommand;

// Separator style: matches StripItemType values HandwrittenSeparator=2, WhiteSeparator=3, RedSeparator=4, GreenSeparator=5.
public enum SeparatorStyle
{
    Handwritten = 0,
    White = 1,
    Red = 2,
    Green = 3,
}

public record SeparatorCreateCommand(SeparatorStyle Style, IReadOnlyList<string> Tokens) : ParsedCommand;

// SEPD <bay> [<rack>] <label-or-position>:
// Tokens are the args after SEPD. The handler peels bay via greedy prefix match,
// then treats the trailing token as label-first (match separator labels in bay);
// if no label matches and the token is numeric, fall back to rack/index position.
public record SeparatorDeleteCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

// SEPM <stripId> <bay>/<rack>/<index>: relocate a separator to a new
// rack slot without changing its label or style. Identifies by stripId
// so a drag-drop survives concurrent moves on other clients.
public record SeparatorMoveCommand(string StripId, string DestBayName, int DestRack, int DestIndex) : ParsedCommand;

// SEPE <bay> <rack> <oldLabelOrIndex> <new label...>: atomic label edit. Bay +
// rack + (label-or-1-based-index) locate the existing separator; remaining
// tokens are the new label. Replaces the prior client-side delete+create
// pattern which could race under reconnect. Label may contain spaces.
public record SeparatorEditCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

// BLANK [<bay> [<rack>] [<index>]]: empty tokens → create in printer queue.
public record BlankCreateCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

// vTDLS commands. All four are callsign-prefixed; the server resolves the active TDLS item by
// (facility-derived-from-filed-departure, callsign). TdlsSendCommand carries the nine
// ClearanceDto fields in positional order (Expect, Sid, Transition, Climbout, Climbvia,
// InitialAlt, ContactInfo, DepFreq, LocalInfo) delimited by '|'; empty fields mean null.
public record TdlsQueueCommand : ParsedCommand;

public record TdlsSendCommand(IReadOnlyList<string> Fields) : ParsedCommand;

public record TdlsWilcoCommand : ParsedCommand;

public record TdlsDumpCommand : ParsedCommand;

// BLANKD <bay> [<rack>]: server picks any blank from the matched bay/rack and deletes it.
public record BlankDeleteCommand(IReadOnlyList<string> Tokens) : ParsedCommand;

public record Scratchpad1Command(string Text) : ParsedCommand;

public record Scratchpad2Command(string Text) : ParsedCommand;

public record TemporaryAltitudeCommand(int AltitudeHundreds) : ParsedCommand;

public record CruiseCommand(int AltitudeHundreds) : ParsedCommand;

public record OnHandoffCommand : ParsedCommand;

// Coordination commands
public record CoordinationReleaseCommand(string? ListId) : ParsedCommand;

public record CoordinationHoldCommand(string? ListId, string? Text) : ParsedCommand;

public record CoordinationRecallCommand(string? ListId) : ParsedCommand;

public record CoordinationAcknowledgeCommand(string? ListId) : ParsedCommand;

public record CoordinationAutoAckCommand(string ListId) : ParsedCommand;

// Approach commands

public enum CrossFixAltitudeType
{
    At,
    AtOrAbove,
    AtOrBelow,
}

public enum HoldingEntry
{
    Direct,
    Teardrop,
    Parallel,
}

public record ClearedApproachCommand(
    string? ApproachId,
    string? AirportCode,
    bool Force,
    string? AtFix,
    double? AtFixLat,
    double? AtFixLon,
    string? DctFix,
    double? DctFixLat,
    double? DctFixLon,
    int? CrossFixAltitude,
    CrossFixAltitudeType? CrossFixAltType
) : ParsedCommand;

public record JoinApproachCommand(string ApproachId, string? AirportCode, bool Force) : ParsedCommand;

public record ClearedApproachStraightInCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record JoinApproachStraightInCommand(string ApproachId, string? AirportCode) : ParsedCommand;

public record JoinFinalApproachCourseCommand(string? ApproachId) : ParsedCommand;

public record JoinStarCommand(string StarId, string? Transition, string? RunwayTransition) : ParsedCommand;

public record JoinAirwayCommand(string AirwayId) : ParsedCommand;

public record JoinRadialOutboundCommand(string FixName, double FixLat, double FixLon, int Radial) : ParsedCommand;

public record JoinRadialInboundCommand(string FixName, double FixLat, double FixLon, int Radial) : ParsedCommand;

public record HoldingPatternCommand(
    string FixName,
    double FixLat,
    double FixLon,
    int InboundCourse,
    double LegLength,
    bool IsMinuteBased,
    TurnDirection Direction,
    HoldingEntry? Entry
) : ParsedCommand;

public record PositionTurnAltitudeClearanceCommand(MagneticHeading? MagneticHeading, int? Altitude, string? ApproachId, bool Forced) : ParsedCommand;

public record ClimbViaCommand(int? Altitude) : ParsedCommand;

public record DescendViaCommand(int? Altitude, int? Speed = null, string? SpeedFixName = null, double? SpeedFixLat = null, double? SpeedFixLon = null)
    : ParsedCommand;

public record CrossFixCommand(string FixName, double FixLat, double FixLon, int Altitude, CrossFixAltitudeType AltType, int? Speed) : ParsedCommand;

public record ConstrainedFixAltitude(int AltitudeFt, CrossFixAltitudeType AltType);

public record ConstrainedForceDirectToCommand(
    List<ResolvedFix> Fixes,
    Dictionary<int, ConstrainedFixAltitude> AltitudeConstraints,
    Dictionary<int, int>? SpeedConstraints,
    List<string>? SkippedFixes
) : ParsedCommand;

public record DepartFixCommand(string FixName, double FixLat, double FixLon, MagneticHeading MagneticHeading) : ParsedCommand;

public record ListApproachesCommand(string? AirportCode) : ParsedCommand;

public record ClearedVisualApproachCommand(string RunwayId, string? AirportCode, PatternDirection? TrafficDirection, string? FollowCallsign)
    : ParsedCommand;

public record ReportFieldInSightCommand : ParsedCommand;

public record ReportFieldAdvisoryCommand(FieldAdvisoryDetails Details) : ParsedCommand;

public record ReportFieldInSightForcedCommand : ParsedCommand;

public record ReportTrafficInSightCommand(string? TargetCallsign) : ParsedCommand;

public record ReportTrafficAdvisoryCommand(TrafficAdvisoryDetails Details) : ParsedCommand;

public record ReportTrafficRelativeCommand(TrafficRelativeDetails Details) : ParsedCommand;

public record ReportTrafficPatternCommand(TrafficPatternDetails Details) : ParsedCommand;

public record ReportTrafficLandmarkCommand(TrafficLandmarkDetails Details) : ParsedCommand;

public record ReportTrafficInSightForcedCommand(string? TargetCallsign) : ParsedCommand;

public record SafetyAlertCommand(SafetyAlertDetails Details) : ParsedCommand;

public record WakeAdvisoryCommand : ParsedCommand;

// Queue meta-commands
public record DeleteQueuedCommand(int? BlockNumber = null) : ParsedCommand;

public record ShowQueuedCommand : ParsedCommand;

// Flight plan amendment commands
public record ChangeDestinationCommand(string Airport) : ParsedCommand;

public record CreateFlightPlanCommand(string FlightRules, string AircraftType, int CruiseAltitude, string Route) : ParsedCommand;

public record CreateAbbreviatedFlightPlanCommand(
    uint? BeaconCode,
    string? Scratchpad1,
    string? Scratchpad2,
    string? AircraftType,
    int? CruiseAltitude,
    string FlightRules
) : ParsedCommand;

public record SetRemarksCommand(string Text) : ParsedCommand;

/// <summary>Instructor freetext note assigned to an aircraft (NOTE command). Empty text clears it.</summary>
public record NoteCommand(string Text) : ParsedCommand;

public record CancelIfrCommand : ParsedCommand;

// Turn rate override
public record SetTurnRateCommand(double DegreesPerSecond) : ParsedCommand;

public record ClearTurnRateCommand : ParsedCommand;

public enum AsdexEditField
{
    Scratchpad1,
    Scratchpad2,
    Callsign,
    BeaconCode,
    Category,
    AircraftType,
    Fix,
}

public enum AsdexVerb
{
    Tag,
    Terminate,
    Suspend,
    Unsuspend,
    InhibitAlerts,
}

/// <summary>
/// ASDE-X display field override issued from the YAAT terminal.
/// <see cref="Field"/> selects which override slot on <c>AircraftStarsState</c> to write.
/// </summary>
public record AsdexEditCommand(AsdexEditField Field, string Text) : ParsedCommand;

/// <summary>
/// ASDE-X per-aircraft verb command (suspend/terminate/etc.).
/// </summary>
public record AsdexVerbCommand(AsdexVerb Verb) : ParsedCommand;

/// <summary>
/// Room-wide ASDE-X command to clear all alert inhibits. No aircraft target.
/// </summary>
public record AsdexEnableAllAlertsCommand : ParsedCommand;

// Server/global commands
public record DeleteCommand : ParsedCommand;

/// <summary>
/// Cancel any pending auto-delete on this aircraft and re-arm
/// <see cref="AircraftGroundOps.AutoDeleteExempt"/>. Bare verb <c>NODEL</c>.
/// </summary>
public record CancelAutoDeleteCommand : ParsedCommand;

public record PauseCommand : ParsedCommand;

public record UnpauseCommand : ParsedCommand;

public record SimRateCommand(int Rate) : ParsedCommand;

public record SpawnNowCommand : ParsedCommand;

public record SpawnDelayCommand(int Seconds) : ParsedCommand;

/// <summary>Arm hold-for-release for an airport's IFR departures (<c>HFR &lt;airport&gt;</c>).</summary>
public record HoldForReleaseCommand(string Airport) : ParsedCommand;

/// <summary>Disarm hold-for-release for an airport (<c>HFROFF &lt;airport&gt;</c>); auto-releases anything still held.</summary>
public record DisarmHoldForReleaseCommand(string Airport) : ParsedCommand;

/// <summary>
/// Release a held departure (<c>REL</c> / <c>CTOA</c>). <paramref name="Target"/> is an airport
/// (release the next pending there, or the whole queue when <paramref name="IntervalSeconds"/> is
/// set) or a specific callsign. Disambiguated against the held set at routing time.
/// </summary>
public record ReleaseDepartureCommand(string Target, int? IntervalSeconds = null) : ParsedCommand;

public record AddAircraftCommand(string Args) : ParsedCommand;

public record ConsolidateCommand(string ReceivingTcpCode, string SendingTcpCode, bool Full) : ParsedCommand;

public record DeconsolidateCommand(string TcpCode) : ParsedCommand;
