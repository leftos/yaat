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
    private readonly List<NavigationTarget> _originalRoute = [];
    private TrueHeading? _originalTargetHeading;
    private TurnDirection? _originalTurnDirection;
    private double? _originalTargetSpeed;
    private double? _originalTargetAltitude;
    private double? _originalAltitudeCeiling;
    private double _cumulativeTurn;
    private TrueHeading _lastHeading;
    private bool _started;

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
            OriginalRoute = _originalRoute.Count > 0 ? _originalRoute.Select(t => t.ToSnapshot()).ToList() : null,
            OriginalTargetHeadingDeg = _originalTargetHeading?.Degrees,
            OriginalTurnDirection = _originalTurnDirection.HasValue ? (int)_originalTurnDirection.Value : null,
            OriginalTargetSpeed = _originalTargetSpeed,
            OriginalTargetAltitude = _originalTargetAltitude,
            OriginalAltitudeCeiling = _originalAltitudeCeiling,
            CumulativeTurn = _cumulativeTurn,
            LastHeadingDeg = _lastHeading.Degrees,
            Started = _started,
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
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        if (dto.OriginalRoute is not null)
        {
            phase._originalRoute.AddRange(dto.OriginalRoute.Select(NavigationTarget.FromSnapshot));
        }
        phase._originalTargetHeading = dto.OriginalTargetHeadingDeg.HasValue ? new TrueHeading(dto.OriginalTargetHeadingDeg.Value) : null;
        phase._originalTurnDirection = dto.OriginalTurnDirection.HasValue ? (TurnDirection)dto.OriginalTurnDirection.Value : null;
        phase._originalTargetSpeed = dto.OriginalTargetSpeed;
        phase._originalTargetAltitude = dto.OriginalTargetAltitude;
        phase._originalAltitudeCeiling = dto.OriginalAltitudeCeiling;
        phase._cumulativeTurn = dto.CumulativeTurn;
        phase._lastHeading = new TrueHeading(dto.LastHeadingDeg);
        phase._started = dto.Started;
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
        SetHoldingTargets(ctx);
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

        // An explicit vector or navigation command means the controller has taken responsibility for
        // keeping the aircraft clear. Without this the orbit overwrites TargetTrueHeading every tick and
        // the aircraft circles through the vector until it is cleared into the airspace.
        if (ctx.Targets.AssignedMagneticHeading is not null || ctx.Targets.NavigationRoute.Count > 0)
        {
            return true;
        }

        if (!HeldVolumeCanStillBeEntered(ctx.Aircraft))
        {
            return true;
        }

        SetHoldingTargets(ctx);
        var current = ctx.Aircraft.TrueHeading;
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

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (endStatus != PhaseStatus.Completed)
        {
            return;
        }

        if (Mode == AirspaceHoldMode.LevelOff)
        {
            // Only lift the pilot's own cap. A controller "maintain VFR at or below" issued during the
            // level-off replaced it, and that restriction outlives the hold.
            if (ctx.Targets.AltitudeCeiling.HasValue && (int)ctx.Targets.AltitudeCeiling.Value == LevelOffCeilingFtMsl)
            {
                ctx.Targets.AltitudeCeiling = _originalAltitudeCeiling;
                // FlightPhysics clears TargetAltitude when the aircraft captures the capped goal, so the
                // pre-hold climb target has to be put back or the aircraft never resumes it.
                ctx.Targets.TargetAltitude ??= _originalTargetAltitude;
            }

            return;
        }

        // OnStart cleared the route and the hold never repopulates it, so a non-empty route here means
        // the controller issued a direct/navigation command during the hold — preserve it rather than
        // reverting to the pre-hold route. Likewise an assigned heading means the controller vectored.
        if (ctx.Targets.AssignedMagneticHeading is null && ctx.Targets.NavigationRoute.Count == 0)
        {
            ctx.Targets.NavigationRoute.Clear();
            foreach (var target in _originalRoute)
            {
                ctx.Targets.NavigationRoute.Add(CloneNavigationTarget(target));
            }
            ctx.Targets.TargetTrueHeading = _originalTargetHeading;
            ctx.Targets.PreferredTurnDirection = _originalTurnDirection;
        }

        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = _originalTargetSpeed;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return CommandAcceptance.Allowed;
    }

    private void SetHoldingTargets(PhaseContext ctx)
    {
        double maxHold = AircraftPerformance.HoldingSpeed(ctx.AircraftType, ctx.Aircraft.Altitude);
        if (ctx.Targets.TargetSpeed is null || ctx.Targets.TargetSpeed > maxHold)
        {
            ctx.Targets.TargetSpeed = maxHold;
        }

        double offset = OrbitDirection == TurnDirection.Left ? -180 : 180;
        ctx.Targets.TargetTrueHeading = ctx.Aircraft.TrueHeading + offset;
        ctx.Targets.PreferredTurnDirection = OrbitDirection;
    }

    private bool GateSatisfied(AircraftState aircraft) =>
        AirspaceClass switch
        {
            AirspaceClass.Bravo => aircraft.IsClearedIntoBravo,
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
