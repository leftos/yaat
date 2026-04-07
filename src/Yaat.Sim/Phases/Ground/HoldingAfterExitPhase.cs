using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft has exited the runway and is holding, awaiting taxi instructions.
/// Speed=0, IsOnGround=true. Accepts Taxi, Hold, and Delete.
/// Never completes on its own — waits for an RPO command.
///
/// On start: broadcasts "clear of runway {rwy} at {twy}".
/// Does NOT snap heading — the aircraft keeps the heading from RunwayExitPhase.
/// </summary>
public sealed class HoldingAfterExitPhase : Phase
{
    private string? _runwayId;
    private string? _exitTaxiway;
    private int? _holdShortNodeId;

    /// <summary>
    /// The hold-short node this aircraft is occupying. Used by
    /// <see cref="SimulationEngine"/> to mark the node as occupied so other
    /// aircraft don't select the same exit.
    /// </summary>
    public int? HoldShortNodeId => _holdShortNodeId;

    public HoldingAfterExitPhase() { }

    public HoldingAfterExitPhase(string? runwayId, string? exitTaxiway, int? holdShortNodeId)
    {
        _runwayId = runwayId;
        _exitTaxiway = exitTaxiway;
        _holdShortNodeId = holdShortNodeId;
    }

    public override string Name => "Holding After Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        // Broadcast "clear of runway"
        string rwy = _runwayId ?? ctx.Aircraft.Phases?.AssignedRunway?.Designator ?? "unknown";
        string twy = _exitTaxiway ?? ctx.Aircraft.CurrentTaxiway ?? "taxiway";
        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} clear of runway {rwy} at {twy}");

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: holding after exit at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            ctx.Aircraft.TrueHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new HoldingAfterExitPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            RunwayId = _runwayId,
            ExitTaxiway = _exitTaxiway,
            HoldShortNodeId = _holdShortNodeId,
        };

    public static HoldingAfterExitPhase FromSnapshot(HoldingAfterExitPhaseDto dto)
    {
        var phase = new HoldingAfterExitPhase();
        phase._runwayId = dto.RunwayId;
        phase._exitTaxiway = dto.ExitTaxiway;
        phase._holdShortNodeId = dto.HoldShortNodeId;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
