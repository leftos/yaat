using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>How the pilot keeps clear of the volume.</summary>
public enum AirspaceHoldMode
{
    /// <summary>The boundary is ahead: orbit outside it until the entry gate is satisfied.</summary>
    Orbit,

    /// <summary>
    /// The volume is directly overhead: stay on course and level off beneath its floor. Turning is useless
    /// when the aircraft is already laterally inside the footprint, and flying under a Class B shelf is
    /// normal VFR practice (AIM 3-2-3.d.2.c).
    /// </summary>
    LevelOff,
}

/// <summary>
/// Solo-training VFR self-restriction outside Class B/C airspace. In <see cref="AirspaceHoldMode.Orbit"/>
/// the phase orbits outside the boundary until the entry gate is satisfied, then restores the pre-hold
/// route when the controller has not replaced it with an explicit vector/navigation command. In
/// <see cref="AirspaceHoldMode.LevelOff"/> it caps the climb below the volume's floor and leaves course
/// and speed alone.
/// </summary>
public sealed class AirspaceBoundaryHoldPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirspaceBoundaryHoldPhase");

    private readonly List<NavigationTarget> _originalRoute = [];
    private TrueHeading? _originalTargetHeading;
    private TurnDirection? _originalTurnDirection;
    private double? _originalTargetSpeed;
    private double? _originalTargetAltitude;
    private double? _originalAltitudeCeiling;
    private double _cumulativeTurn;
    private TrueHeading _lastHeading;
    private bool _started;

    // Whether the hold held through a controller vector: under RMOB in free flight, or, in front of a held phase, a new
    // heading or direct into the held Class B (PairedVectorEndsHold). A free-flight hold resumes the assigned heading when it
    // ends only when this is set.
    private bool _heldThroughVector;

    // Whether a hold in front of a held phase stashed a direct it held through in _originalRoute: only then does its end put
    // that route back in place of the held phase's (EndPairedLateral).
    private bool _stashedDirect;

    // The assigned heading standing when the hold first started: a hold in front of a held phase does not read it as a new
    // vector. Written at the first OnStart, and cleared the first time a new heading is seen, so every later heading is
    // judged in its turn.
    private MagneticHeading? _headingStandingAtStart;

    // A hold in front of a held phase: the vertical rate standing when it first started, and the aircraft's controller
    // altitude command count then. Its end puts the saved altitude target and this rate back unless a controller altitude
    // command came during the hold (EndPairedVertical). Null for any other hold.
    private double? _originalDesiredVerticalRate;
    private int? _altitudeCommandCountAtStart;

    public AirspaceClass AirspaceClass { get; init; }
    public string Ident { get; init; } = "";
    public string NameText { get; init; } = "";
    public LatLon ReferencePosition { get; init; }
    public TurnDirection OrbitDirection { get; init; } = TurnDirection.Right;
    public int? VolumeLowerFtMsl { get; init; }
    public int? VolumeUpperFtMsl { get; init; }
    public AirspaceHoldMode Mode { get; init; } = AirspaceHoldMode.Orbit;

    /// <summary><see cref="AirspaceVolume.Id"/> of the held volume, re-resolved each tick for the lateral test.</summary>
    public string VolumeId { get; init; } = "";

    /// <summary>Altitude the aircraft levels at in <see cref="AirspaceHoldMode.LevelOff"/>; null in orbit mode.</summary>
    public int? LevelOffCeilingFtMsl { get; init; }

    public override string Name =>
        (Mode, AirspaceClass) switch
        {
            (AirspaceHoldMode.LevelOff, AirspaceClass.Bravo) => "LevelBelowBravo",
            (AirspaceHoldMode.LevelOff, _) => "LevelBelowCharlie",
            (_, AirspaceClass.Bravo) => "HoldOutsideBravo",
            _ => "HoldOutsideCharlie",
        };

    /// <summary>
    /// Only the orbit slows the aircraft to holding speed. A VFR aircraft levelling under a shelf keeps
    /// cruise speed, so an ATC speed assignment must still take effect.
    /// </summary>
    public override bool ManagesSpeed => Mode == AirspaceHoldMode.Orbit;

    public override PhaseDto ToSnapshot() =>
        new AirspaceBoundaryHoldPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            AirspaceClass = (int)AirspaceClass,
            Ident = Ident,
            NameText = NameText,
            ReferenceLat = ReferencePosition.Lat,
            ReferenceLon = ReferencePosition.Lon,
            OrbitDirection = (int)OrbitDirection,
            VolumeLowerFtMsl = VolumeLowerFtMsl,
            VolumeUpperFtMsl = VolumeUpperFtMsl,
            Mode = (int)Mode,
            VolumeId = VolumeId,
            LevelOffCeilingFtMsl = LevelOffCeilingFtMsl,
            OriginalRoute = _originalRoute.Count > 0 ? [.. _originalRoute.Select(t => t.ToSnapshot())] : null,
            OriginalTargetHeadingDeg = _originalTargetHeading?.Degrees,
            OriginalTurnDirection = _originalTurnDirection.HasValue ? (int)_originalTurnDirection.Value : null,
            OriginalTargetSpeed = _originalTargetSpeed,
            OriginalTargetAltitude = _originalTargetAltitude,
            OriginalAltitudeCeiling = _originalAltitudeCeiling,
            CumulativeTurn = _cumulativeTurn,
            LastHeadingDeg = _lastHeading.Degrees,
            Started = _started,
            HeldThroughVector = _heldThroughVector,
            StashedDirect = _stashedDirect,
            HeadingStandingAtStartDeg = _headingStandingAtStart?.Degrees,
            OriginalDesiredVerticalRate = _originalDesiredVerticalRate,
            AltitudeCommandCountAtStart = _altitudeCommandCountAtStart,
        };

    public static AirspaceBoundaryHoldPhase FromSnapshot(AirspaceBoundaryHoldPhaseDto dto)
    {
        var phase = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = (AirspaceClass)dto.AirspaceClass,
            Ident = dto.Ident,
            NameText = dto.NameText,
            ReferencePosition = new LatLon(dto.ReferenceLat, dto.ReferenceLon),
            OrbitDirection = (TurnDirection)dto.OrbitDirection,
            VolumeLowerFtMsl = dto.VolumeLowerFtMsl,
            VolumeUpperFtMsl = dto.VolumeUpperFtMsl,
            Mode = (AirspaceHoldMode)dto.Mode,
            VolumeId = dto.VolumeId ?? "",
            LevelOffCeilingFtMsl = dto.LevelOffCeilingFtMsl,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        if (dto.OriginalRoute is not null)
        {
            phase._originalRoute.AddRange(dto.OriginalRoute.Select(NavigationTarget.FromSnapshot));
        }
        phase._originalTargetHeading = dto.OriginalTargetHeadingDeg.HasValue ? TrueHeading.FromSnapshot(dto.OriginalTargetHeadingDeg.Value) : null;
        phase._originalTurnDirection = dto.OriginalTurnDirection.HasValue ? (TurnDirection)dto.OriginalTurnDirection.Value : null;
        phase._originalTargetSpeed = dto.OriginalTargetSpeed;
        phase._originalTargetAltitude = dto.OriginalTargetAltitude;
        phase._originalAltitudeCeiling = dto.OriginalAltitudeCeiling;
        phase._cumulativeTurn = dto.CumulativeTurn;
        phase._lastHeading = TrueHeading.FromSnapshot(dto.LastHeadingDeg);
        phase._started = dto.Started;
        phase._heldThroughVector = dto.HeldThroughVector;
        phase._stashedDirect = dto.StashedDirect;
        phase._headingStandingAtStart = dto.HeadingStandingAtStartDeg is { } standingDeg ? new MagneticHeading(standingDeg) : null;
        phase._originalDesiredVerticalRate = dto.OriginalDesiredVerticalRate;
        phase._altitudeCommandCountAtStart = dto.AltitudeCommandCountAtStart;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        if (!_started)
        {
            _originalRoute.Clear();
            _originalRoute.AddRange(ctx.Targets.NavigationRoute.Select(CloneNavigationTarget));
            _originalTargetHeading = ctx.Targets.TargetTrueHeading;
            _originalTurnDirection = ctx.Targets.PreferredTurnDirection;
            _originalTargetSpeed = ctx.Targets.TargetSpeed;
            _originalTargetAltitude = ctx.Targets.TargetAltitude;
            _originalAltitudeCeiling = ctx.Targets.AltitudeCeiling;
            _headingStandingAtStart = ctx.Targets.AssignedMagneticHeading;
            if (ctx.Aircraft.Phases?.HeldPhase is not null)
            {
                _originalDesiredVerticalRate = ctx.Targets.DesiredVerticalRate;
                _altitudeCommandCountAtStart = ctx.Aircraft.ControllerAltitudeCommandCount;
            }

            _started = true;
        }

        _lastHeading = ctx.Aircraft.TrueHeading;

        // Issue #154: airspace boundary holds used to broadcast a pilot self-report
        // ("holding outside the charlie, awaiting two-way"). Real pilots don't narrate
        // their own avoidance manoeuvres on the radio — the controller would just see
        // the aircraft turn. The phase continues to slow / orbit the aircraft so the
        // boundary respect itself still applies, just silently.
        if (Mode == AirspaceHoldMode.LevelOff)
        {
            // Set once, never re-asserted: a controller altitude command nulls AltitudeCeiling, and OnTick
            // reads that as the controller taking responsibility for the shelf.
            ctx.Targets.AltitudeCeiling = LevelOffCeilingFtMsl;
            return;
        }

        ctx.Targets.NavigationRoute.Clear();
        LevelInFrontOfAHeldPhase(ctx);
        SetHoldingTargets(ctx);
    }

    /// <summary>
    /// An orbit in front of a held phase never descends: a descent the phase left (a goal below the aircraft or a descent
    /// rate) becomes a level-off at the present altitude, floored at <see cref="Tower.GoAroundPhase.NoTurnAgl"/> above the
    /// field. A climb is left as it stands. The held phase sets its own targets again when it resumes.
    /// </summary>
    private static void LevelInFrontOfAHeldPhase(PhaseContext ctx)
    {
        bool descending = (ctx.Targets.TargetAltitude < ctx.Aircraft.Altitude) || (ctx.Targets.DesiredVerticalRate < 0);
        if ((ctx.Aircraft.Phases?.HeldPhase is null) || !descending)
        {
            return;
        }

        ctx.Targets.TargetAltitude = Math.Max(ctx.Aircraft.Altitude, ctx.FieldElevation + Tower.GoAroundPhase.NoTurnAgl);
        ctx.Targets.DesiredVerticalRate = null;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (GateSatisfied(ctx.Aircraft))
        {
            return true;
        }

        if (Mode == AirspaceHoldMode.LevelOff)
        {
            return TickLevelOff(ctx);
        }

        if (VectorEndsHold(ctx))
        {
            return true;
        }

        if (!HeldVolumeCanStillBeEntered(ctx.Aircraft))
        {
            return true;
        }

        if (HeldPathClearsTheBravo(ctx))
        {
            return true;
        }

        SetHoldingTargets(ctx);
        TrueHeading current = ctx.Aircraft.TrueHeading;
        double delta = _lastHeading.SignedAngleTo(current);
        _cumulativeTurn += Math.Abs(delta);
        _lastHeading = current;
        if (_cumulativeTurn >= 350)
        {
            _cumulativeTurn -= 360;
        }

        return false;
    }

    /// <summary>
    /// Whether a controller vector ends the orbit. An explicit vector or navigation command means the controller has taken
    /// responsibility for keeping the aircraft clear; without this the orbit overwrites TargetTrueHeading every tick and the
    /// aircraft circles through the vector until it is cleared into the airspace. A free-flight hold ends on one unless the
    /// pilot is told to remain outside this Class B and the vector still takes it in (<see cref="HoldThroughVector"/>). A hold
    /// in front of a held phase follows the same rule on the held Class B for a direct or a heading that is new since the
    /// hold went in (<see cref="PairedVectorEndsHold"/>).
    /// </summary>
    private bool VectorEndsHold(PhaseContext ctx)
    {
        if (ctx.Aircraft.Phases?.HeldPhase is not null)
        {
            return PairedVectorEndsHold(ctx);
        }

        if ((ctx.Targets.AssignedMagneticHeading is null) && !ctx.Targets.HasRouteToFly)
        {
            return false;
        }

        if (!VectorEntersRemainOutsideBravo(ctx))
        {
            return true;
        }

        HoldThroughVector(ctx);
        return false;
    }

    /// <summary>
    /// A hold in front of a held phase follows the free-flight rule for a controller vector, judged on the held Class B
    /// whether or not the pilot is told to remain outside it. A command reaches the pair through the dispatcher's lift: a
    /// held phase that keeps itself on a heading or a direct (an initial climb, a departure procedure) leaves it standing.
    /// A direct (any route to fly: the hold's start cleared the held phase's) that keeps the aircraft outside the Class B
    /// ends the orbit and is flown; one that would take it in is held through, stashed for the end of the hold
    /// (<see cref="HoldThroughVector"/>). A new heading is judged the same way; a heading that was already standing when
    /// the hold went in (<see cref="_headingStandingAtStart"/>) is not a vector and is ignored until a new heading is seen,
    /// after which every heading is judged in its turn.
    /// </summary>
    private bool PairedVectorEndsHold(PhaseContext ctx)
    {
        if (ctx.Targets.HasRouteToFly && (ctx.Targets.NavigationRoute.FirstOrDefault() is { } next))
        {
            if (!TrackEntersHeldBravo(ctx, new TrueHeading(GeoMath.BearingTo(ctx.Aircraft.Position, next.Position))))
            {
                return true;
            }

            HoldThroughVector(ctx);
            _stashedDirect = true;
            return false;
        }

        if (!HasNewHeading(ctx, out MagneticHeading assigned))
        {
            return false;
        }

        // From the first new heading on, every heading is judged in its turn: one given back the value that stood at the
        // start is a vector like any other.
        _headingStandingAtStart = null;
        if (!TrackEntersHeldBravo(ctx, assigned.ToTrue(ctx.Aircraft.Declination)))
        {
            return true;
        }

        _heldThroughVector = true;
        return false;
    }

    /// <summary>
    /// An assigned heading standing now, <paramref name="assigned"/>, that is not the one standing when the hold went in (any
    /// heading once a new one has been seen).
    /// </summary>
    private bool HasNewHeading(PhaseContext ctx, out MagneticHeading assigned)
    {
        if ((ctx.Targets.AssignedMagneticHeading is { } heading) && (heading != _headingStandingAtStart))
        {
            assigned = heading;
            return true;
        }

        assigned = default;
        return false;
    }

    /// <summary>
    /// Whether a level track on <paramref name="track"/> at the aircraft's altitude enters the held Class B within
    /// <see cref="Pilot.ImplicitBravoClearance.WaitingEntryLookaheadSeconds"/>: the projection
    /// <see cref="VectorEntersRemainOutsideBravo"/> makes, whether or not the pilot is told to remain outside.
    /// </summary>
    private bool TrackEntersHeldBravo(PhaseContext ctx, TrueHeading track) =>
        (AirspaceClass == AirspaceClass.Bravo)
        && (
            AirspaceDatabase.Default.FindLevelTrackClassBEntry(
                ctx.Aircraft.Position,
                ctx.Aircraft.Altitude,
                track,
                ctx.Aircraft.GroundSpeed,
                Pilot.ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
                Ident
            )
            is not null
        );

    /// <summary>
    /// The level-off holds until the shelf is behind the aircraft, the controller assigns an altitude, or
    /// the entry gate opens. It deliberately does NOT ask whether the volume is still projected to be
    /// entered: the cap it just imposed is what stops the projection, so that test would end the hold on
    /// the first tick, restore the climb, and re-trigger the hold — a level/climb oscillation.
    /// </summary>
    private bool TickLevelOff(PhaseContext ctx)
    {
        if (!ctx.Targets.AltitudeCeiling.HasValue || (int)ctx.Targets.AltitudeCeiling.Value != LevelOffCeilingFtMsl)
        {
            return true;
        }

        return !LateralPositionStillUnderVolume(ctx.Aircraft);
    }

    /// <summary>
    /// Only the pilot's own cap is lifted: a controller "maintain VFR at or below" issued during the level-off replaced it,
    /// and that restriction outlives the hold. A Class B level-off ended by a replaced or cleared phase list (skipped) hands
    /// its standing cap, saved ceiling and target to <see cref="AircraftState.BravoCap"/> in the same step, so the ceiling is
    /// never left without an owner; the record's own rules end it. Any other level-off puts the saved ceiling back.
    /// </summary>
    private void EndLevelOff(PhaseContext ctx, PhaseStatus endStatus)
    {
        if ((LevelOffCeilingFtMsl is not { } cap) || (ctx.Targets.AltitudeCeiling is not { } ceiling) || ((int)ceiling != cap))
        {
            return;
        }

        if ((endStatus != PhaseStatus.Completed) && (AirspaceClass == AirspaceClass.Bravo))
        {
            var handedOver = new Pilot.BravoCap(Ident, VolumeId, cap, _originalAltitudeCeiling, _originalTargetAltitude);
            ctx.Aircraft.StartBravoCap(handedOver);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "{Callsign}: level-off below {Area} ended {Status}, Class B cap {Cap} handed to the cap record (ceiling {Ceiling})",
                    ctx.Aircraft.Callsign,
                    Ident,
                    endStatus,
                    cap,
                    handedOver.Ceiling
                );
            }

            return;
        }

        ctx.Targets.AltitudeCeiling = _originalAltitudeCeiling;
        // FlightPhysics clears TargetAltitude when the aircraft captures the capped goal, so the
        // pre-hold climb target has to be put back or the aircraft never resumes it.
        ctx.Targets.TargetAltitude ??= _originalTargetAltitude;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (Mode == AirspaceHoldMode.LevelOff)
        {
            EndLevelOff(ctx, endStatus);
            return;
        }

        if (endStatus != PhaseStatus.Completed)
        {
            return;
        }

        EndLateral(ctx);
        EndPairedVertical(ctx);

        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = _originalTargetSpeed;
        }
    }

    /// <summary>
    /// A hold in front of a held phase gives the phase back the altitude target and vertical rate it left when the hold first
    /// started (<see cref="LevelInFrontOfAHeldPhase"/> may have levelled them): a phase that resumes in place and set its
    /// descent only when it started (<see cref="Pattern.MidfieldCrossingPhase"/>) descends again. A controller altitude
    /// command during the hold (<see cref="AircraftState.ControllerAltitudeCommandCount"/> moved) is left standing. A phase
    /// resumed as a restart copy sets its own targets again when it starts. Nothing for any other hold.
    /// </summary>
    private void EndPairedVertical(PhaseContext ctx)
    {
        if (!RestoresPairedVertical(ctx.Aircraft))
        {
            return;
        }

        ctx.Targets.TargetAltitude = _originalTargetAltitude;
        ctx.Targets.DesiredVerticalRate = _originalDesiredVerticalRate;
    }

    /// <summary>
    /// The altitude target a controller altitude command issued during this hold left, which the hold's end leaves standing
    /// (<see cref="EndPairedVertical"/>): the self-release's walk flies it ahead of the resumed leg's own altitude. Null when
    /// no such command came.
    /// </summary>
    internal double? ControllerAltitudeLeftStanding(PhaseContext ctx) =>
        AltitudeCommandedDuringHold(ctx.Aircraft) ? ctx.Targets.TargetAltitude : null;

    /// <summary>Whether a controller altitude command came since this hold, standing in front of a held phase, started.</summary>
    private bool AltitudeCommandedDuringHold(AircraftState aircraft) =>
        (_altitudeCommandCountAtStart is { } count) && (aircraft.ControllerAltitudeCommandCount != count);

    /// <summary>
    /// The altitude target standing once this hold has ended: the one <see cref="EndPairedVertical"/> gives back to the held
    /// phase, else the present one (a controller altitude issued during the hold, or any hold not in front of a held phase).
    /// </summary>
    internal double? TargetAltitudeAfterEnd(PhaseContext ctx) =>
        RestoresPairedVertical(ctx.Aircraft) ? _originalTargetAltitude : ctx.Targets.TargetAltitude;

    /// <summary>Whether the hold's end gives the held phase back its vertical targets: it stands in front of one, and no CM came since.</summary>
    private bool RestoresPairedVertical(AircraftState aircraft) =>
        (_altitudeCommandCountAtStart is { } count) && (aircraft.ControllerAltitudeCommandCount == count);

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd) => CommandAcceptance.Allowed;

    /// <summary>
    /// The lateral targets an orbit that completed hands on. In front of a held phase see <see cref="EndPairedLateral"/>.
    /// Otherwise OnStart cleared the route and the hold never repopulates it, so a non-empty route here means the controller
    /// issued a direct/navigation command during the hold — preserve it rather than reverting to the pre-hold route. Likewise
    /// an assigned heading means the controller vectored; only a hold that held through it under RMOB overwrote it, and
    /// resumes it.
    /// </summary>
    private void EndLateral(PhaseContext ctx)
    {
        if (ctx.Aircraft.Phases?.HeldPhase is not null)
        {
            EndPairedLateral(ctx);
        }
        else if ((ctx.Targets.AssignedMagneticHeading is null) && !ctx.Targets.HasRouteToFly)
        {
            RestoreLateralTargets(ctx);
        }
        else if (_heldThroughVector)
        {
            ResumeAssignedHeading(ctx);
        }
    }

    /// <summary>
    /// In front of a held phase the aircraft flies what the controller gave during the hold, as the held phase would after
    /// that command unheld: a new heading is resumed (<see cref="ResumeAssignedHeading"/>), and a direct stays standing or,
    /// held through, is put back from the stash (<see cref="_stashedDirect"/>); either way the orbit's turn direction is
    /// dropped and the held phase's own route is not put back. With no new heading or direct standing (a held-through heading
    /// the controller cleared included), the held phase's route, heading and turn direction are put back.
    /// </summary>
    private void EndPairedLateral(PhaseContext ctx)
    {
        if (HasNewHeading(ctx, out _))
        {
            ResumeAssignedHeading(ctx);
            ctx.Targets.PreferredTurnDirection = null;
            return;
        }

        if (!ctx.Targets.HasRouteToFly && !_stashedDirect)
        {
            RestoreLateralTargets(ctx);
            return;
        }

        if (!ctx.Targets.HasRouteToFly)
        {
            PutBackRoute(ctx);
        }

        ctx.Targets.PreferredTurnDirection = null;
    }

    /// <summary>Puts back the route, target heading and turn direction saved when the hold first started.</summary>
    private void RestoreLateralTargets(PhaseContext ctx)
    {
        PutBackRoute(ctx);
        ctx.Targets.TargetTrueHeading = _originalTargetHeading;
        ctx.Targets.PreferredTurnDirection = _originalTurnDirection;
    }

    /// <summary>Puts back the route the hold saved: the one standing when it first started, or a direct it held through.</summary>
    private void PutBackRoute(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        foreach (NavigationTarget target in _originalRoute)
        {
            ctx.Targets.NavigationRoute.Add(CloneNavigationTarget(target));
        }
    }

    private void SetHoldingTargets(PhaseContext ctx)
    {
        double maxHold = AircraftPerformance.HoldingSpeed(ctx.AircraftType, ctx.Aircraft.Altitude);
        if (ctx.Targets.TargetSpeed is null)
        {
            // A hold in front of a running phase slows the aircraft to holding speed at most; it never speeds it up.
            ctx.Targets.TargetSpeed = (ctx.Aircraft.Phases?.HeldPhase is not null) ? Math.Min(maxHold, ctx.Aircraft.IndicatedAirspeed) : maxHold;
        }
        else if (ctx.Targets.TargetSpeed > maxHold)
        {
            ctx.Targets.TargetSpeed = maxHold;
        }

        double offset = OrbitDirection == TurnDirection.Left ? -180 : 180;
        ctx.Targets.TargetTrueHeading = ctx.Aircraft.TrueHeading + offset;
        ctx.Targets.PreferredTurnDirection = OrbitDirection;
    }

    /// <summary>
    /// A vector the pilot held through (<see cref="HoldThroughVector"/>) is flown once the hold ends: the orbit overwrote
    /// the target heading, so it is set back to the assigned heading. A route the controller gave is not touched.
    /// </summary>
    private static void ResumeAssignedHeading(PhaseContext ctx)
    {
        if ((ctx.Targets.AssignedMagneticHeading is not { } assigned) || ctx.Targets.HasRouteToFly)
        {
            return;
        }

        TrueHeading target = assigned.ToTrue(ctx.Aircraft.Declination);
        if ((ctx.Targets.TargetTrueHeading is { } current) && (Math.Abs(current.SignedAngleTo(target)) <= 1.0))
        {
            return;
        }

        ctx.Targets.TargetTrueHeading = target;
        ctx.Targets.PreferredTurnDirection = null;
    }

    /// <summary>The track a controller vector gives: the assigned heading, else toward the route's next fix; null for neither.</summary>
    private static TrueHeading? VectorTrack(PhaseContext ctx)
    {
        if (ctx.Targets.AssignedMagneticHeading is { } assigned)
        {
            return assigned.ToTrue(ctx.Aircraft.Declination);
        }

        return ctx.Targets.NavigationRoute.FirstOrDefault() is { } next
            ? new TrueHeading(GeoMath.BearingTo(ctx.Aircraft.Position, next.Position))
            : null;
    }

    /// <summary>
    /// Whether a controller vector or direct still takes the pilot into the Class B it is told to remain outside
    /// (<c>RMOB</c>): its new track, level at its altitude, enters this Class B within
    /// <see cref="Pilot.ImplicitBravoClearance.WaitingEntryLookaheadSeconds"/>. The pilot then keeps holding through it
    /// (<see cref="HoldThroughVector"/>).
    /// </summary>
    private bool VectorEntersRemainOutsideBravo(PhaseContext ctx)
    {
        if ((AirspaceClass != AirspaceClass.Bravo) || !ctx.Aircraft.IsRemainOutsideBravo(Ident) || (VectorTrack(ctx) is not { } newTrack))
        {
            return false;
        }

        return AirspaceDatabase.Default.FindLevelTrackClassBEntry(
            ctx.Aircraft.Position,
            ctx.Aircraft.Altitude,
            newTrack,
            ctx.Aircraft.GroundSpeed,
            Pilot.ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
            Ident
        )
            is not null;
    }

    /// <summary>
    /// Keeps holding through a controller vector or direct into the Class B (in free flight, one the pilot is told to remain
    /// outside; in front of a held phase, a direct into the held Class B), and marks the hold as having done so. A direct is
    /// stashed for the end of the hold: its route replaces the route the hold restores, so the orbit is not flown over by
    /// it, and the pilot flies it once cleared in. An assigned heading stays assigned and is resumed then
    /// (<see cref="ResumeAssignedHeading"/>).
    /// </summary>
    private void HoldThroughVector(PhaseContext ctx)
    {
        _heldThroughVector = true;
        if (ctx.Targets.HasRouteToFly)
        {
            _originalRoute.Clear();
            _originalRoute.AddRange(ctx.Targets.NavigationRoute.Select(CloneNavigationTarget));
            ctx.Targets.NavigationRoute.Clear();
        }
    }

    private bool GateSatisfied(AircraftState aircraft) =>
        AirspaceClass switch
        {
            AirspaceClass.Bravo => aircraft.IsClearedIntoBravo(Ident),
            AirspaceClass.Charlie => aircraft.HasMadeInitialContact && aircraft.HasControllerAcknowledgedInitialContact,
            _ => true,
        };

    /// <summary>
    /// True while the aircraft is still beneath the held volume's footprint, now or 60 s ahead. Once the
    /// shelf is behind it the cap comes off and the climb resumes. Falls back to holding when the volume
    /// can't be resolved — better a stale cap than a silent airspace bust.
    /// </summary>
    private bool LateralPositionStillUnderVolume(AircraftState aircraft)
    {
        if (AirspaceDatabase.Default.FindById(VolumeId) is not { } volume)
        {
            return true;
        }

        return volume.ContainsLateral(aircraft.Position) || volume.ContainsLateral(AirspaceDatabase.ProjectPosition(aircraft, 60.0));
    }

    /// <summary>
    /// The route the hold saved when it first started (the held phase's live route then), or a direct it held through: what
    /// its end puts back (<see cref="PutBackRoute"/>).
    /// </summary>
    public IReadOnlyList<NavigationTarget> StashedRoute => _originalRoute;

    /// <summary>
    /// The scenario second the boundary hold in front of <paramref name="aircraft"/>'s held phase last ended by itself because
    /// the held path cleared the Class B (<see cref="HeldPathClearsTheBravo"/>); null when none has. Kept for the re-hold
    /// warning only, outside the snapshot.
    /// </summary>
    public static double? SelfReleasedAtSeconds(AircraftState aircraft) =>
        SelfReleases.TryGetValue(aircraft, out StrongBox<double>? at) ? at.Value : null;

    private static readonly ConditionalWeakTable<AircraftState, StrongBox<double>> SelfReleases = [];

    /// <summary>
    /// The self-release: a Class B hold in front of a held phase, not holding through a vector, ends by itself, as a grant
    /// ends it but with no clearance, once the whole remaining held path clears the held Class B by one turn margin
    /// (<see cref="Pilot.BravoPathPredictor.TurnMarginNm"/>, the radius of the orbit the aircraft flies). The path judged is
    /// the one the resume would fly: the held phase, or the restart copy its <see cref="Phase.ResumeAfterBoundaryHold"/>
    /// builds (never started), from the aircraft's present position (<see cref="Pilot.BravoPathPredictor.PredictResume"/>).
    /// Judged once per second of the hold. The margin is the hysteresis: the trigger holds on any entry of the 60 s path, the
    /// release needs the whole path clear by the margin, so a path between the two neither holds nor releases; a downwind
    /// running within one margin of the boundary never self-releases and holds until cleared in or vectored, accepted as the
    /// margin's cost. A held phase with no path source never self-releases. A hold flying through a vector into the Class B
    /// never ends on this test: the vector's track decides then.
    /// </summary>
    private bool HeldPathClearsTheBravo(PhaseContext ctx)
    {
        if (
            _heldThroughVector
            || (AirspaceClass != AirspaceClass.Bravo)
            || (ctx.Aircraft.Phases is not { HeldPhase: { } held } phases)
            || (Math.Floor(ElapsedSeconds) <= Math.Floor(ElapsedSeconds - ctx.DeltaSeconds))
        )
        {
            return false;
        }

        if (!ResumedPathClears(ctx, phases, held))
        {
            return false;
        }

        SelfReleases.AddOrUpdate(ctx.Aircraft, new StrongBox<double>(ctx.ScenarioElapsedSeconds));
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[BoundaryHold] {Callsign}: {Held} path clears the {Area} Class B, the hold ends by itself",
                ctx.Aircraft.Callsign,
                held.Name,
                Ident
            );
        }

        return true;
    }

    /// <summary>
    /// Whether the path of the phase the hold's end would install (the restart copy when there is one, else the held phase)
    /// clears the held Class B by the turn margin. A held phase of a kind the predictor has no path source for
    /// (<see cref="Pilot.BravoPathPredictor.HasPathSource"/>) never clears, and its resume is never built: a teardrop
    /// re-entry's resume writes its own completion flag, so a once-a-second read must not call it.
    /// </summary>
    private bool ResumedPathClears(PhaseContext ctx, PhaseList phases, Phase held)
    {
        if (!Pilot.BravoPathPredictor.HasPathSource(held))
        {
            return false;
        }

        AircraftState aircraft = ctx.Aircraft;
        Phase installed = held.ResumeAfterBoundaryHold(ctx).Copy ?? held;
        return (Pilot.BravoPathPredictor.PredictResume(aircraft, phases, installed, ctx, this) is { } path)
            && Pilot.BravoPathPredictor.KeepsClearOf(path, AirspaceDatabase.Default, Ident, Pilot.BravoPathPredictor.TurnMarginNm(aircraft));
    }

    private bool HeldVolumeCanStillBeEntered(AircraftState aircraft)
    {
        if (VolumeLowerFtMsl is null || VolumeUpperFtMsl is null)
        {
            return true;
        }

        if (aircraft.Altitude >= VolumeLowerFtMsl.Value && aircraft.Altitude <= VolumeUpperFtMsl.Value)
        {
            return true;
        }

        const double lookaheadSeconds = 60.0;
        double projectedAltitude = AirspaceDatabase.ProjectAltitude(aircraft, lookaheadSeconds);
        double low = Math.Min(aircraft.Altitude, projectedAltitude);
        double high = Math.Max(aircraft.Altitude, projectedAltitude);
        return (high >= VolumeLowerFtMsl.Value) && (low <= VolumeUpperFtMsl.Value);
    }

    private static NavigationTarget CloneNavigationTarget(NavigationTarget target) => NavigationTarget.FromSnapshot(target.ToSnapshot());
}
