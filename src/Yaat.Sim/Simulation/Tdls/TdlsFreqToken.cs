using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// The <c>$FREQ</c> token a Facility Engineer writes into a vTDLS contact-info or local-info value ("CTC $FREQ TO PUSH"),
/// which vTDLS fills with the controller's primary frequency. Here that is the student position's frequency.
/// <para>
/// Substitution is a projection only: the configured strings live in the process-cached ARTCC tree every room shares,
/// so a value is substituted on its way to a client or into a payload and never written back into the config.
/// </para>
/// </summary>
public static class TdlsFreqToken
{
    private static readonly ILogger Log = SimLog.CreateLogger("TdlsFreqToken");

    /// <summary>The literal token a configured value carries.</summary>
    public const string Token = "$FREQ";

    /// <summary>
    /// The student position's primary frequency in MHz, three decimals (<c>121.100</c>). Null when the scenario has no
    /// student position, no ARTCC configuration, the position is not in that configuration, or it has no frequency.
    /// </summary>
    public static string? StudentPrimaryFrequencyMhz(SimScenarioState? scenario)
    {
        TrackOwner? student = scenario?.StudentPosition;
        ArtccConfigRoot? artcc = scenario?.ArtccConfig;
        if ((student is null) || (artcc is null))
        {
            return null;
        }

        PositionConfig? position = artcc.FindPositionByCallsign(student.Callsign, student.FacilityId);
        if (position is null)
        {
            Log.LogDebug("No position {Callsign} in the ARTCC configuration; {Token} stays literal", student.Callsign, Token);
            return null;
        }

        if (position.Frequency <= 0)
        {
            Log.LogDebug("Position {Callsign} has no frequency; {Token} stays literal", student.Callsign, Token);
            return null;
        }

        return (position.Frequency / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <paramref name="value"/> with every <see cref="Token"/> replaced by <paramref name="frequencyMhz"/>; the value
    /// unchanged when there is no frequency to put in.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Apply(string? value, string? frequencyMhz) =>
        ((value is null) || (frequencyMhz is null)) ? value : value.Replace(Token, frequencyMhz, StringComparison.Ordinal);
}
