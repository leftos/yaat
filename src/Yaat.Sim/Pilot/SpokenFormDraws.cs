using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The seeded, replay-safe decisions that pick a readback's shortened spoken forms: the abbreviated N-number callsign and
/// the grouped contact frequency. Each draw is <see cref="VariantPick.Index"/> over the callsign, the scenario seed and the
/// whole second, against a percentage set by the pilot's trait or the band it speaks at.
/// </summary>
public static class SpokenFormDraws
{
    /// <summary>Percent of readbacks in which a terse pilot abbreviates its N-number.</summary>
    public const int TerseShortCallsignPercent = 80;

    /// <summary>Percent of readbacks in which a normal pilot abbreviates its N-number.</summary>
    public const int NormalShortCallsignPercent = 50;

    /// <summary>Percent of readbacks in which a chatty pilot abbreviates its N-number.</summary>
    public const int ChattyShortCallsignPercent = 15;

    /// <summary>Percent of contact readbacks that group the frequency when the pilot speaks at the busy band.</summary>
    public const int BusyGroupedFrequencyPercent = 75;

    /// <summary>Percent of contact readbacks that group the frequency when the pilot speaks at the normal band.</summary>
    public const int NormalGroupedFrequencyPercent = 15;

    /// <summary>Percent of contact readbacks that group the frequency when the pilot speaks at the quiet band.</summary>
    public const int QuietGroupedFrequencyPercent = 0;

    /// <summary>Percent of held second initial-contact check-ins whose pilot butts in after a first one the controller has not answered.</summary>
    public const int CheckInButtInPercent = 10;

    private const string CallsignFormBody = "callsign-form";
    private const string ContactFrequencyBody = "contact-frequency";
    private const string CheckInButtInBody = "check-in-butt-in";

    /// <summary>
    /// Whether a pilot may abbreviate its callsign in a readback (AIM 4-2-4.a.1-a.3, 7110.65 2-4-9): the callsign is an FAA
    /// N-number (<see cref="CallsignParser.ShortenedNNumberSpoken"/>), the controller has acknowledged the pilot's contact,
    /// the pilot is still on the student's frequency, and no other callsign in <paramref name="otherCallsigns"/> ends in
    /// the same last two characters.
    /// </summary>
    /// <param name="callsign">The pilot's own callsign.</param>
    /// <param name="acknowledged">Whether the controller had acknowledged the pilot's initial contact.</param>
    /// <param name="leftStudentFrequency">Whether the pilot has been sent off the student's frequency.</param>
    /// <param name="otherCallsigns">The callsigns of the other aircraft on the frequency; the pilot's own is ignored.</param>
    public static bool CanShortenCallsign(string callsign, bool acknowledged, bool leftStudentFrequency, IEnumerable<string> otherCallsigns)
    {
        if (!acknowledged || leftStudentFrequency || (CallsignParser.ShortenedNNumberSpoken(callsign) is null))
        {
            return false;
        }

        string own = callsign.Trim();
        string lastTwo = own[^2..];
        return !otherCallsigns.Any(other =>
            !string.Equals(other.Trim(), own, StringComparison.OrdinalIgnoreCase)
            && other.Trim().EndsWith(lastTwo, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Whether this readback, at <paramref name="elapsedSecond"/>, is one in which the pilot abbreviates its callsign: a
    /// terse pilot most of the time, a chatty one rarely (<see cref="PilotTraits.For"/>).
    /// </summary>
    public static bool DrawShortCallsign(string callsign, int rngSeed, int elapsedSecond)
    {
        int percent = PilotTraits.For(callsign, rngSeed) switch
        {
            PilotTrait.Terse => TerseShortCallsignPercent,
            PilotTrait.Normal => NormalShortCallsignPercent,
            PilotTrait.Chatty => ChattyShortCallsignPercent,
            _ => throw new InvalidOperationException($"{callsign}: unknown pilot trait."),
        };
        return VariantPick.Index(callsign, CallsignFormBody, elapsedSecond, rngSeed, 100) < percent;
    }

    /// <summary>
    /// Whether this contact readback, at <paramref name="elapsedSecond"/>, speaks its frequency grouped
    /// (<see cref="PhraseologyVerbalizer.FrequencyToWords(double, bool)"/>): mostly at the busy band, rarely at the normal
    /// band, never at the quiet band. <paramref name="effectiveBand"/> is the band the pilot's trait shifts the frequency to.
    /// </summary>
    public static bool DrawGroupedContactFrequency(string callsign, FrequencyLoadBand effectiveBand, int rngSeed, int elapsedSecond)
    {
        int percent = effectiveBand switch
        {
            FrequencyLoadBand.Busy => BusyGroupedFrequencyPercent,
            FrequencyLoadBand.Normal => NormalGroupedFrequencyPercent,
            FrequencyLoadBand.Quiet => QuietGroupedFrequencyPercent,
            _ => throw new ArgumentOutOfRangeException(nameof(effectiveBand), effectiveBand, "Unknown frequency load band."),
        };
        return VariantPick.Index(callsign, ContactFrequencyBody, elapsedSecond, rngSeed, 100) < percent;
    }

    /// <summary>
    /// Whether this pilot's initial-contact check-in, held while the controller-response gate waits on another pilot's
    /// check-in that ended at <paramref name="firstCallEndSecond"/>, may butt in three seconds later. One draw per
    /// candidate and first call: neither input changes tick to tick, so a held call draws the same answer every tick.
    /// </summary>
    public static bool DrawCheckInButtIn(string callsign, int firstCallEndSecond, int rngSeed) =>
        VariantPick.Index(callsign, CheckInButtInBody, firstCallEndSecond, rngSeed, 100) < CheckInButtInPercent;
}
