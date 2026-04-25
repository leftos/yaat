using System.Text;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Builds deterministic pilot-readback strings from accepted dispatched commands. Used by
/// <see cref="CommandDispatcher"/> in solo-training mode to push readbacks into
/// <see cref="AircraftState.PendingNotifications"/>.
///
/// Most readbacks come from inverting <see cref="PhraseologyRule"/>s via
/// <see cref="PhraseologyVerbalizer"/> — controllers and pilots share the same vocabulary,
/// so the rule that would have parsed the controller's words is also the readback. Pilot-only
/// utterances (spawn check-in, "going around" volunteered) live here directly because they
/// have no controller-side rule equivalent.
///
/// Output format:
/// <code>"[N123AB] descend and maintain five thousand, november one two three alpha bravo."</code>
/// The <c>[ICAO]</c> bracket prefix is for terminal rendering (so the user sees who is talking
/// even if multiple aircraft talk back close together); the spoken-callsign tail is the real
/// ATC closing form.
/// </summary>
public static class PilotResponder
{
    /// <summary>
    /// Returns the readback line for an accepted compound command, or <see langword="null"/> if
    /// none of its commands had a verbalization. The caller is responsible for the
    /// <c>SoloTrainingMode</c> gate — this method renders unconditionally.
    ///
    /// Compound conditions (e.g., <c>at SUNOL turn left 270</c>) become a leading "at sunol,"
    /// clause attached to the first command in the block. Subsequent commands in the same block
    /// don't repeat the lead-in. The readback fires once at dispatch acceptance for the entire
    /// compound, not again when a queued block's trigger fires.
    /// </summary>
    public static string? BuildReadback(CompoundCommand compound, AircraftState aircraft)
    {
        var clauses = new List<string>();
        foreach (var block in compound.Blocks)
        {
            var conditionLead = FormatCondition(block.Condition);
            foreach (var cmd in block.Commands)
            {
                var clause = PhraseologyVerbalizer.Verbalize(cmd);
                if (string.IsNullOrEmpty(clause))
                {
                    continue;
                }

                clauses.Add(conditionLead is null ? clause : conditionLead + " " + clause);
                conditionLead = null; // condition is spoken once per block
            }
        }

        if (clauses.Count == 0)
        {
            return null;
        }

        return Format(aircraft, string.Join(", ", clauses));
    }

    /// <summary>
    /// Pilot-initiated spawn check-in: "Ground, {callsign} at the {parking}, ready to taxi."
    /// Fired by <c>AtParkingPhase</c> 5 seconds after spawn for IFR aircraft (with a flight plan)
    /// in solo-training mode. No controller-side rule equivalent — this is a pure pilot
    /// utterance, so it lives here rather than in <c>PhraseologyRules</c>.
    /// </summary>
    public static string BuildReadyToTaxi(AircraftState aircraft)
    {
        var location = aircraft.Ground.ParkingSpot is { Length: > 0 } spot ? $"at {spot.ToLowerInvariant()}" : "at the ramp";
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] ground, {spoken} {location}, ready to taxi.";
    }

    private static string Format(AircraftState aircraft, string body)
    {
        var spoken = CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return $"[{aircraft.Callsign}] {body}, {spoken}.";
    }

    private static string? FormatCondition(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            AtFixCondition fix => $"at {fix.FixName.ToLowerInvariant()},",
            LevelCondition level => $"at {PhraseologyVerbalizer.AltitudeWords(level.Altitude)},",
            _ => null, // GiveWayCondition and other condition kinds have their own dispatch path.
        };
}
