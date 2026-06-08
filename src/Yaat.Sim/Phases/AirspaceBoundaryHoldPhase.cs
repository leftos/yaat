using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>
/// Solo-training VFR self-restriction outside Class B/C airspace. The phase orbits outside
/// the boundary until the entry gate is satisfied, then restores the pre-hold route when the
/// controller has not replaced it with an explicit vector/navigation command.
/// </summary>
public sealed class AirspaceBoundaryHoldPhase : Phase
{
    private readonly List<NavigationTarget> _originalRoute = [];
    private TrueHeading? _originalTargetHeading;
    private TurnDirection? _originalTurnDirection;
    private double? _originalTargetSpeed;
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

    public override string Name => AirspaceClass == AirspaceClass.Bravo ? "HoldOutsideBravo" : "HoldOutsideCharlie";

    public override bool ManagesSpeed => true;

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
            OriginalRoute = _originalRoute.Count > 0 ? _originalRoute.Select(t => t.ToSnapshot()).ToList() : null,
            OriginalTargetHeadingDeg = _originalTargetHeading?.Degrees,
            OriginalTurnDirection = _originalTurnDirection.HasValue ? (int)_originalTurnDirection.Value : null,
            OriginalTargetSpeed = _originalTargetSpeed,
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
            _started = true;
        }

        _lastHeading = ctx.Aircraft.TrueHeading;
        ctx.Targets.NavigationRoute.Clear();
        SetHoldingTargets(ctx);

        // Issue #154: airspace boundary holds used to broadcast a pilot self-report
        // ("holding outside the charlie, awaiting two-way"). Real pilots don't narrate
        // their own avoidance manoeuvres on the radio — the controller would just see
        // the aircraft turn. The phase continues to slow / orbit the aircraft so the
        // boundary respect itself still applies, just silently.
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (GateSatisfied(ctx.Aircraft))
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

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (endStatus != PhaseStatus.Completed)
        {
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
