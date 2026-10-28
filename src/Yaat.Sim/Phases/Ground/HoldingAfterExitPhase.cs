using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
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
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingAfterExitPhase");

    private string? _runwayId;
    private string? _exitTaxiway;
    private int? _holdShortNodeId;
    private bool _stoppedInsideHoldingDistance;
    private string? _heldShortOfRunway;

    /// <summary>
    /// The aircraft has not cleared the runway: it holds at a bar inside the runway's holding distance (an exit the controller
    /// named whose bar is short of the standoff, with nothing beyond it), or it stopped with its nose at another runway's holding
    /// position marking and its tail still inside this runway's (<see cref="HeldShortOfRunway"/>, a crossover shorter than the
    /// aircraft). Set once on start.
    /// </summary>
    public bool StoppedInsideHoldingDistance => _stoppedInsideHoldingDistance;

    /// <summary>
    /// The runway whose holding position marking the nose stopped at, with the tail still inside <see cref="RunwayId"/>'s
    /// (<see cref="RunwayTailClearance.Stop.TailInsideLine"/>); null when the stop left the aircraft clear of it or held at a bar.
    /// </summary>
    public string? HeldShortOfRunway => _heldShortOfRunway;

    /// <summary>
    /// The hold-short node this aircraft is occupying. Used by
    /// <see cref="SimulationEngine"/> to mark the node as occupied so other
    /// aircraft don't select the same exit.
    /// </summary>
    public int? HoldShortNodeId => _holdShortNodeId;

    /// <summary>
    /// The runway the aircraft just exited. Used by the client info text
    /// to render "clear of runway {id} via {taxiway}".
    /// </summary>
    public string? RunwayId => _runwayId;

    public HoldingAfterExitPhase() { }

    /// <param name="runwayId">The runway just exited or pulled clear of.</param>
    /// <param name="exitTaxiway">The taxiway the aircraft holds on.</param>
    /// <param name="holdShortNodeId">The runway's bar the aircraft passed.</param>
    /// <param name="heldShortOfRunway">
    /// The other runway whose line the nose stopped at with the tail still inside <paramref name="runwayId"/>'s; null otherwise.
    /// </param>
    public HoldingAfterExitPhase(string? runwayId, string? exitTaxiway, int? holdShortNodeId, string? heldShortOfRunway)
    {
        _runwayId = runwayId;
        _exitTaxiway = exitTaxiway;
        _holdShortNodeId = holdShortNodeId;
        _heldShortOfRunway = heldShortOfRunway;
    }

    public override string Name => "Holding After Exit";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;
        _stoppedInsideHoldingDistance = (_heldShortOfRunway is not null) || IsStopInsideHoldingDistance(ctx);
        string rwy = _runwayId ?? ctx.Aircraft.Phases?.AssignedRunway?.Designator ?? "unknown";
        string twy = _exitTaxiway ?? ctx.Aircraft.Ground.CurrentTaxiway ?? "taxiway";

        // With no layout there was no exit to take: RunwayExitPhase rolled the aircraft to a stop on the
        // runway itself, so the pilot has nothing to report clear of.
        if ((_exitTaxiway is null) && (ctx.GroundLayout is null))
        {
            Log.LogDebug("[Exit] {Callsign}: no ground layout, holding on the runway — no clear-of-runway call", ctx.Aircraft.Callsign);
        }
        else if (_heldShortOfRunway is { } otherRunway)
        {
            Log.LogDebug(
                "[Exit] {Callsign}: nose at runway {Other}'s line on {Twy}, tail inside {Runway}'s — reporting not clear",
                ctx.Aircraft.Callsign,
                otherRunway,
                twy,
                rwy
            );
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                ctx.Aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildHoldingShortNotClearOfRunwayText(ctx.Aircraft, otherRunway, twy, rwy),
                Pilot.PilotResponder.SoloPositionsTower
            );
        }
        else if (_stoppedInsideHoldingDistance)
        {
            Log.LogDebug(
                "[Exit] {Callsign}: holding at bar #{Bar} inside the {Runway} holding distance — no clear-of-runway call",
                ctx.Aircraft.Callsign,
                _holdShortNodeId,
                _runwayId
            );
        }
        else
        {
            // Broadcast "clear of runway" — solo TWR students need the TTS so they can hand off
            // to ground; solo GND students aren't on tower frequency at this point.
            PilotSpeechText text = Pilot.PilotResponder.BuildClearOfRunwayText(ctx.Aircraft, rwy, twy);
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                ctx.Aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                text,
                Pilot.PilotResponder.SoloPositionsTower
            );
        }

        Log.LogDebug(
            "[Exit] {Callsign}: holding after exit at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees
        );
    }

    private bool IsStopInsideHoldingDistance(PhaseContext ctx)
    {
        if ((ctx.GroundLayout is not { } layout) || (_holdShortNodeId is not { } barId) || (_runwayId is null))
        {
            return false;
        }

        return layout.Nodes.TryGetValue(barId, out GroundNode? bar) && !layout.IsAtRunwayHoldingDistance(bar, _runwayId);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        Pilot.TaxiInRequest.TryAnnounce(ctx, ElapsedSeconds, _runwayId, _exitTaxiway);
        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is holding clear of the runway after exit; only HOLD, FOLLOWG, or a new TAXI/ATXI/LAND apply"),
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
            StoppedInsideHoldingDistance = _stoppedInsideHoldingDistance,
            HeldShortOfRunway = _heldShortOfRunway,
        };

    public static HoldingAfterExitPhase FromSnapshot(HoldingAfterExitPhaseDto dto)
    {
        var phase = new HoldingAfterExitPhase
        {
            _runwayId = dto.RunwayId,
            _exitTaxiway = dto.ExitTaxiway,
            _holdShortNodeId = dto.HoldShortNodeId,
            _stoppedInsideHoldingDistance = dto.StoppedInsideHoldingDistance,
            _heldShortOfRunway = dto.HeldShortOfRunway,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
