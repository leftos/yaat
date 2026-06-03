using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter landing: decelerate toward 0 KIAS while descending.
/// Below FlareAltitude (50ft AGL): slow descent at FlareDescentRate.
/// Touchdown at speed=0, altitude=field elevation. No rollout.
/// </summary>
public sealed class HelicopterLandingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HelicopterLandingPhase");

    private double _fieldElevation;
    private bool _touchedDown;

    public override string Name => "Landing-H";

    public override PhaseDto ToSnapshot() =>
        new HelicopterLandingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            TouchedDown = _touchedDown,
        };

    public static HelicopterLandingPhase FromSnapshot(HelicopterLandingPhaseDto dto)
    {
        var phase = new HelicopterLandingPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._touchedDown = dto.TouchedDown;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;

        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.TargetSpeed = 0;

        Log.LogDebug("[Landing-H] {Callsign}: started descent, fieldElev={Elev:F0}ft", ctx.Aircraft.Callsign, _fieldElevation);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_touchedDown)
        {
            return true;
        }

        // Continuously enforce hover (TargetSpeed=0) so the descent doesn't
        // bleed altitude into forward motion. Without this the heli accelerates
        // forward as it descends and overshoots the spot by hundreds of feet.
        ctx.Targets.TargetSpeed = 0;

        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        double flareAlt = CategoryPerformance.FlareAltitude(ctx.Category);

        if (agl <= flareAlt)
        {
            double flareRate = CategoryPerformance.FlareDescentRate(ctx.Category);
            ctx.Targets.DesiredVerticalRate = -flareRate;
        }

        // Touchdown when at ground level
        if (agl <= 0)
        {
            _touchedDown = true;
            Log.LogDebug("[Landing-H] {Callsign}: touchdown", ctx.Aircraft.Callsign);
            ctx.Aircraft.IsOnGround = true;
            ctx.Aircraft.Altitude = _fieldElevation;
            ctx.Aircraft.VerticalSpeed = 0;
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetAltitude = null;
            ctx.Targets.DesiredVerticalRate = null;
            ctx.Targets.TargetSpeed = null;
            return true;
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (_touchedDown)
        {
            return cmd switch
            {
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected("helicopter has touched down; only DEL applies until the landing completes"),
            };
        }

        // Before touchdown, GA and EL/ER/EXIT are handled in-phase; every other airborne
        // maneuvering command (FH, turns, CM/DM, SPD, DCT, DEL) pulls the heli off the descent
        // and back to the command queue. The dispatcher dry-runs before clearing the phase.
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
