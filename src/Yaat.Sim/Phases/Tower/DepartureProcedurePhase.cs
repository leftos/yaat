using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Flies the leading ARINC-424 heading/course legs of a charted SID — VA (heading→altitude),
/// CA (course→altitude), VI/CI (heading/course→intercept), VM (heading→manual), and
/// course-tracked CF — that the flat route resolver drops. Begins after <see cref="InitialClimbPhase"/>
/// clears the DER + 400 ft AGL TERPS gate; on completing the last coded leg it loads the remaining
/// fix-to-fix route into <see cref="ControlTargets.NavigationRoute"/> and hands back to
/// <c>FlightPhysics.UpdateNavigation</c>. A heading or direct-to command takes the aircraft off the
/// procedure (the phase yields immediately).
/// </summary>
public sealed class DepartureProcedurePhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("DepartureProcedurePhase");

    private const double FixArrivalNm = 0.5;
    private const double MaxInterceptDeg = 45.0;
    private const double CrossTrackGainDegPerNm = 25.0;
    private const double HeadingEstablishedDeg = 10.0;
    private const double MaxInterceptSeconds = 180.0;

    private int _legIndex;
    private bool _overridden;
    private LatLon? _legEntryPosition;
    private double? _previousSignedCrossTrack;
    private double _legElapsedSeconds;

    /// <summary>The coded leading legs to fly (heading/course legs and any interior fixes).</summary>
    public required List<ProcedureLeg> Legs { get; init; }

    /// <summary>Fix-to-fix route loaded into NavigationRoute once the coded legs are flown.</summary>
    public required List<NavigationTarget> PostRoute { get; init; }

    /// <summary>Controller-assigned climb-to altitude, if any (CTO bundled CM/DM).</summary>
    public int? AssignedAltitude { get; init; }

    /// <summary>Filed cruise altitude (feet MSL) — climb ceiling when no controller altitude.</summary>
    public int CruiseAltitude { get; init; }

    public override string Name => "DepartureProcedure";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetAltitude = ResolveClimbCeiling();
        _legEntryPosition = ctx.Aircraft.Position;
        ApplyActiveLegHeading(ctx);
        Log.LogDebug(
            "[DepartureProcedure] {Callsign}: started with {Count} coded legs, ceiling={Ceil:F0}ft",
            ctx.Aircraft.Callsign,
            Legs.Count,
            ctx.Targets.TargetAltitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_overridden)
        {
            return true;
        }

        if (_legIndex >= Legs.Count)
        {
            return Finish(ctx);
        }

        _legElapsedSeconds += ctx.DeltaSeconds;
        var leg = Legs[_legIndex];
        bool sequence = leg.Type switch
        {
            ProcedureLegType.HeadingToAltitude => FlyToAltitude(ctx, leg, asTrack: false),
            ProcedureLegType.CourseToAltitude => FlyToAltitude(ctx, leg, asTrack: true),
            ProcedureLegType.HeadingToIntercept => FlyToIntercept(ctx, leg, asTrack: false),
            ProcedureLegType.CourseToIntercept => FlyToIntercept(ctx, leg, asTrack: true),
            ProcedureLegType.CourseToFix => TrackCourseToFix(ctx, leg),
            ProcedureLegType.HeadingToManual => HoldManualHeading(ctx, leg),
            _ => FlyToFix(ctx, leg),
        };

        if (sequence)
        {
            AdvanceLeg(ctx);
            if (_legIndex >= Legs.Count)
            {
                return Finish(ctx);
            }
        }

        return false;
    }

    private void AdvanceLeg(PhaseContext ctx)
    {
        _legIndex++;
        _legElapsedSeconds = 0;
        _legEntryPosition = ctx.Aircraft.Position;
        _previousSignedCrossTrack = null;
        ctx.Targets.PreferredTurnDirection = null;
        if (_legIndex < Legs.Count)
        {
            ApplyActiveLegHeading(ctx);
        }
    }

    /// <summary>Sets the initial target heading + preferred turn direction when a leg becomes active.</summary>
    private void ApplyActiveLegHeading(PhaseContext ctx)
    {
        var leg = Legs[_legIndex];
        if (
            leg.CourseMagnetic is { } course
            && leg.Type is not (ProcedureLegType.TrackToFix or ProcedureLegType.DirectToFix or ProcedureLegType.InitialFix or ProcedureLegType.Arc)
        )
        {
            ctx.Targets.TargetTrueHeading = new MagneticHeading(course).ToTrue(ctx.Aircraft.Declination);
        }

        if (leg.Turn is { } turn && !HeadingEstablished(ctx, leg))
        {
            ctx.Targets.PreferredTurnDirection = turn;
        }
    }

    private bool HeadingEstablished(PhaseContext ctx, ProcedureLeg leg)
    {
        if (leg.CourseMagnetic is not { } course)
        {
            return true;
        }
        var target = new MagneticHeading(course).ToTrue(ctx.Aircraft.Declination);
        return ctx.Aircraft.TrueHeading.AbsAngleTo(target) < HeadingEstablishedDeg;
    }

    private bool FlyToAltitude(PhaseContext ctx, ProcedureLeg leg, bool asTrack)
    {
        SteerHeadingOrTrack(ctx, leg, asTrack);
        return leg.TargetAltitudeFt is { } target && ctx.Aircraft.Altitude >= target;
    }

    private bool FlyToIntercept(PhaseContext ctx, ProcedureLeg leg, bool asTrack)
    {
        SteerHeadingOrTrack(ctx, leg, asTrack);

        // Intercept when within turn-radius lead of the next leg's course line (or it flips across).
        if (_legIndex + 1 >= Legs.Count)
        {
            return false;
        }
        var next = Legs[_legIndex + 1];
        if (next.FixPosition is not { } anchor || next.CourseMagnetic is not { } nextCourse)
        {
            return false;
        }

        var courseTrue = new MagneticHeading(nextCourse).ToTrue(ctx.Aircraft.Declination);
        double signed = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, anchor, courseTrue);
        double turnRate = ctx.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category);
        double leadNm = ctx.Aircraft.GroundSpeed / (turnRate * 62.832);

        bool flipped = _previousSignedCrossTrack is { } prev && ((prev > 0 && signed <= 0) || (prev < 0 && signed >= 0));
        _previousSignedCrossTrack = signed;

        // Guard a divergent heading that never intercepts (bad CIFP data): give up and proceed
        // to the next leg rather than flying the heading forever.
        if (_legElapsedSeconds >= MaxInterceptSeconds)
        {
            Log.LogWarning(
                "[DepartureProcedure] {Callsign}: intercept leg {Idx} did not capture in {Sec:F0}s (xt={Xt:F1}nm) — sequencing",
                ctx.Aircraft.Callsign,
                _legIndex,
                _legElapsedSeconds,
                signed
            );
            return true;
        }

        return Math.Abs(signed) <= leadNm || flipped;
    }

    private bool TrackCourseToFix(PhaseContext ctx, ProcedureLeg leg)
    {
        if (leg.FixPosition is not { } fix || leg.CourseMagnetic is not { } course)
        {
            return FlyToFix(ctx, leg);
        }

        var courseTrue = new MagneticHeading(course).ToTrue(ctx.Aircraft.Declination);
        SteerCourseLine(ctx, fix, courseTrue);
        return GeoMath.DistanceNm(ctx.Aircraft.Position, fix) < FixArrivalNm;
    }

    private bool HoldManualHeading(PhaseContext ctx, ProcedureLeg leg)
    {
        SteerHeadingOrTrack(ctx, leg, asTrack: false);
        // VM/FM terminate on controller vectors; the phase yields via OnCommandAccepted.
        return false;
    }

    private bool FlyToFix(PhaseContext ctx, ProcedureLeg leg)
    {
        if (leg.FixPosition is not { } fix)
        {
            return true;
        }
        ctx.Targets.TargetTrueHeading = new TrueHeading(GeoMath.BearingTo(ctx.Aircraft.Position, fix));
        return GeoMath.DistanceNm(ctx.Aircraft.Position, fix) < FixArrivalNm;
    }

    /// <summary>Fly a raw heading (VA/VI), or a wind-corrected ground track (CA/CI) via a synthetic anchor.</summary>
    private void SteerHeadingOrTrack(PhaseContext ctx, ProcedureLeg leg, bool asTrack)
    {
        if (leg.CourseMagnetic is not { } course)
        {
            return;
        }
        var courseTrue = new MagneticHeading(course).ToTrue(ctx.Aircraft.Declination);

        if (asTrack && _legEntryPosition is { } anchor)
        {
            SteerCourseLine(ctx, anchor, courseTrue);
        }
        else
        {
            ctx.Targets.TargetTrueHeading = courseTrue;
        }

        if (leg.Turn is { } turn && ctx.Aircraft.TrueHeading.AbsAngleTo(courseTrue) >= HeadingEstablishedDeg)
        {
            ctx.Targets.PreferredTurnDirection = turn;
        }
        else
        {
            ctx.Targets.PreferredTurnDirection = null;
        }
    }

    /// <summary>Proportional cross-track steering onto a course line through an anchor (intrinsic wind correction).</summary>
    private void SteerCourseLine(PhaseContext ctx, LatLon anchor, TrueHeading courseTrue)
    {
        double signed = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, anchor, courseTrue);
        double correction = Math.Clamp(signed * CrossTrackGainDegPerNm, -MaxInterceptDeg, MaxInterceptDeg);
        ctx.Targets.TargetTrueHeading = new TrueHeading(courseTrue.Degrees - correction);
    }

    private bool Finish(PhaseContext ctx)
    {
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        var flown = new HashSet<string>(Legs.Where(l => l.FixName is not null).Select(l => l.FixName!), StringComparer.OrdinalIgnoreCase);
        int start = 0;
        while (start < PostRoute.Count && flown.Contains(PostRoute[start].Name))
        {
            start++;
        }
        for (int i = start; i < PostRoute.Count; i++)
        {
            ctx.Targets.NavigationRoute.Add(PostRoute[i]);
        }

        Log.LogDebug(
            "[DepartureProcedure] {Callsign}: coded legs complete, loaded {Count} route fixes",
            ctx.Aircraft.Callsign,
            ctx.Targets.NavigationRoute.Count
        );
        return true;
    }

    private double ResolveClimbCeiling()
    {
        if (AssignedAltitude is { } assigned)
        {
            return assigned;
        }
        if (CruiseAltitude > 0)
        {
            return CruiseAltitude;
        }
        double highestRestriction = 0;
        foreach (var leg in Legs)
        {
            if (leg.AltitudeRestriction is { } r && r.Altitude1Ft > highestRestriction)
            {
                highestRestriction = r.Altitude1Ft;
            }
            if (leg.TargetAltitudeFt is { } t && t > highestRestriction)
            {
                highestRestriction = t;
            }
        }
        return highestRestriction > 0 ? highestRestriction : 17000;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            CanonicalCommandType.DirectTo
            or CanonicalCommandType.AppendDirectTo
            or CanonicalCommandType.TurnLeftDirectTo
            or CanonicalCommandType.TurnRightDirectTo
            or CanonicalCommandType.ForceDirectTo
            or CanonicalCommandType.AppendForceDirectTo
            or CanonicalCommandType.FlyHeading
            or CanonicalCommandType.TurnLeft
            or CanonicalCommandType.TurnRight
            or CanonicalCommandType.RelativeLeft
            or CanonicalCommandType.RelativeRight
            or CanonicalCommandType.FlyPresentHeading
            or CanonicalCommandType.ForceHeading => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx)
    {
        // A lateral instruction (heading or direct-to) takes the aircraft off the coded procedure;
        // altitude/speed adjustments are additive and leave the procedure flying.
        bool lateralOverride = cmd switch
        {
            CanonicalCommandType.FlyHeading
            or CanonicalCommandType.TurnLeft
            or CanonicalCommandType.TurnRight
            or CanonicalCommandType.RelativeLeft
            or CanonicalCommandType.RelativeRight
            or CanonicalCommandType.FlyPresentHeading
            or CanonicalCommandType.ForceHeading
            or CanonicalCommandType.DirectTo
            or CanonicalCommandType.AppendDirectTo
            or CanonicalCommandType.ForceDirectTo
            or CanonicalCommandType.AppendForceDirectTo
            or CanonicalCommandType.TurnLeftDirectTo
            or CanonicalCommandType.TurnRightDirectTo => true,
            _ => false,
        };
        if (lateralOverride)
        {
            _overridden = true;
        }
    }

    public override PhaseDto ToSnapshot() =>
        new DepartureProcedurePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Legs = Legs.Select(l => l.ToSnapshot()).ToList(),
            PostRoute = PostRoute.Select(t => t.ToSnapshot()).ToList(),
            AssignedAltitude = AssignedAltitude,
            CruiseAltitude = CruiseAltitude,
            LegIndex = _legIndex,
            Overridden = _overridden,
            LegEntryPosition = _legEntryPosition,
            PreviousSignedCrossTrack = _previousSignedCrossTrack,
            LegElapsedSeconds = _legElapsedSeconds,
        };

    public static DepartureProcedurePhase FromSnapshot(DepartureProcedurePhaseDto dto)
    {
        var phase = new DepartureProcedurePhase
        {
            Legs = dto.Legs.Select(ProcedureLeg.FromSnapshot).ToList(),
            PostRoute = dto.PostRoute.Select(NavigationTarget.FromSnapshot).ToList(),
            AssignedAltitude = dto.AssignedAltitude,
            CruiseAltitude = dto.CruiseAltitude,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._legIndex = dto.LegIndex;
        phase._overridden = dto.Overridden;
        phase._legEntryPosition = dto.LegEntryPosition;
        phase._previousSignedCrossTrack = dto.PreviousSignedCrossTrack;
        phase._legElapsedSeconds = dto.LegElapsedSeconds;
        return phase;
    }
}
