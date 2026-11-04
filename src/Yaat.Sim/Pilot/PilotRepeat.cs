using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The controller's "say again" (<c>AGAIN</c>, <c>AGAIN CS</c>) to one pilot. The command only sets a pending flag on the
/// aircraft (<see cref="AircraftState.PendingRepeat"/> or <see cref="AircraftState.PendingRepeatCallsignOnly"/>);
/// <see cref="SimulationWorld.DrainReadyPilotTransmissions"/> turns the flag into one line once the pilot's own queued
/// line has gone out, so a rewind that restores the flag still owes the repeat and the transient frequency queue is never
/// needed. The line is built here from snapshotted aircraft state only, and draws nothing from any RNG.
/// </summary>
public static class PilotRepeat
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotRepeat");

    /// <summary>
    /// Handles <c>AGAIN</c> / <c>AGAIN CS</c>: refused outside solo training (no pilot speaks there) and for an aircraft
    /// that has left the student's frequency; a no-op while a say again of either form is already pending (the first one
    /// stands); otherwise the matching flag is set.
    /// </summary>
    public static CommandResult HandleSayAgain(SayAgainCommand command, AircraftState aircraft, DispatchContext ctx)
    {
        if (!ctx.SoloTrainingMode)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("AGAIN is solo-training only; pilots speak on the frequency only in solo training");
        }

        if (aircraft.HasLeftStudentFrequency)
        {
            return new CommandResult(false, $"{aircraft.Callsign} is no longer on your frequency");
        }

        if (aircraft.PendingRepeat || aircraft.PendingRepeatCallsignOnly)
        {
            return CommandDispatcher.Ok("Say again already requested");
        }

        if (command.CallsignOnly)
        {
            aircraft.PendingRepeatCallsignOnly = true;
            return CommandDispatcher.Ok("Say again callsign");
        }

        aircraft.PendingRepeat = true;
        return CommandDispatcher.Ok("Say again");
    }

    /// <summary>Whether <paramref name="compound"/> carries nothing but say-again requests.</summary>
    public static bool IsSayAgainOnly(CompoundCommand compound)
    {
        bool any = false;
        foreach (ParsedBlock block in compound.Blocks)
        {
            foreach (ParsedCommand command in block.Commands)
            {
                if (command is not SayAgainCommand)
                {
                    return false;
                }

                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// The line that answers <paramref name="aircraft"/>'s pending say again, marked <see cref="PilotTransmission.IsRepeat"/>
    /// so it never replaces the aircraft's last transmission and its dequeue clears the flag: the stored last transmission
    /// word for word with its original kind (and so the gates the original armed), the full callsign alone for
    /// <c>AGAIN CS</c>, or "I didn't say anything" when the aircraft has not spoken. A repeat is never an initial contact,
    /// so it cannot butt in behind another pilot's check-in.
    /// </summary>
    public static PilotTransmission BuildPendingRepeat(AircraftState aircraft)
    {
        if (aircraft.LastPilotTransmission is not { } last)
        {
            Log.LogDebug("{Callsign} asked to say again with nothing said; answering that it said nothing", aircraft.Callsign);
            return ReplyLine(aircraft, PilotResponder.BuildNothingToRepeat(aircraft));
        }

        if (aircraft.PendingRepeatCallsignOnly)
        {
            return ReplyLine(aircraft, PilotResponder.BuildCallsignOnlyRepeat(aircraft));
        }

        return new PilotTransmission(aircraft.Callsign, last.Text, last.SpeechText, last.SourceKind, last.Kind, last.CanonicalAirtimeSeconds)
        {
            IsRepeat = true,
        };
    }

    /// <summary>A reply the pilot makes to the say again itself: a SAY-channel line, which arms no frequency gate.</summary>
    private static PilotTransmission ReplyLine(AircraftState aircraft, PilotSpeechText text) =>
        new(
            aircraft.Callsign,
            text.Terminal,
            text.Tts,
            PilotResponder.SourceSayReadback,
            PilotTransmissionKind.SayReadback,
            FrequencyState.EstimateAirtimeSeconds(text.Tts)
        )
        {
            IsRepeat = true,
        };
}
