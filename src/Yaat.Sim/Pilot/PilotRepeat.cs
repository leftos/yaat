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
            return SoloTrainingOnlyRefusal();
        }

        if (aircraft.IsTunedAwayFrom(ctx.StudentPosition))
        {
            return new CommandResult(false, NotOnFrequencyMessage(aircraft));
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

    /// <summary>
    /// What the instructor is told when the pilot is tuned to another position: "{callsign} is not on your frequency (on
    /// {position})", without the position when the pilot is tuned elsewhere or to one with no callsign (the not-heard
    /// refusal and a say again both say it).
    /// </summary>
    public static string NotOnFrequencyMessage(AircraftState aircraft)
    {
        string message = $"{aircraft.Callsign} is not on your frequency";
        TrackOwner? tuned = aircraft.TunedPosition;
        return ((tuned != TrackOwner.Elsewhere) && (tuned?.Callsign is { Length: > 0 } position)) ? $"{message} (on {position})" : message;
    }

    /// <summary>The refusal of a say again of any form outside solo training, where no pilot speaks.</summary>
    public static CommandResult SoloTrainingOnlyRefusal() =>
        CommandDispatcher.OperatorGuidanceRefusal("AGAIN is solo-training only; pilots speak on the frequency only in solo training");

    /// <summary>
    /// The pilot an unaddressed say again (<c>AGAINU</c>) goes to, among the aircraft still on the student's frequency: the
    /// one whose call awaits a controller response, else the one whose last transmission went out latest (ties to the
    /// ordinal-lower callsign), else none. Reads snapshotted state only, so a replay or a restored rewind picks the same one.
    /// </summary>
    /// <param name="world">The world the pilots are in.</param>
    /// <param name="studentPosition">The student's position, against which a pilot's tuned position is read.</param>
    public static AircraftState? PickUnaddressedTarget(SimulationWorld world, TrackOwner? studentPosition)
    {
        List<AircraftState> candidates = [.. world.GetSnapshot().Where(a => !a.IsTunedAwayFrom(studentPosition))];
        if (world.ActiveFrequency.AwaitingControllerResponseTo is { } awaited)
        {
            AircraftState? awaitedAircraft = candidates.Find(a => string.Equals(a.Callsign, awaited, StringComparison.Ordinal));
            if (awaitedAircraft is not null)
            {
                Log.LogDebug("Unaddressed say again goes to {Callsign}", awaitedAircraft.Callsign);
                return awaitedAircraft;
            }
        }

        AircraftState? last = candidates
            .Where(a => a.LastPilotTransmission is not null)
            .OrderByDescending(a => a.LastPilotTransmission!.AtSeconds)
            .ThenBy(a => a.Callsign, StringComparer.Ordinal)
            .FirstOrDefault();
        Log.LogDebug("Unaddressed say again goes to {Callsign}", last?.Callsign ?? "nobody");
        return last;
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
