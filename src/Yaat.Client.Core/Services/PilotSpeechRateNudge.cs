using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>Applies the server's frequency-load factor to the controller's chosen pilot voice speed.</summary>
public static class PilotSpeechRateNudge
{
    private static readonly ILogger Log = AppLog.CreateLogger(nameof(PilotSpeechRateNudge));

    /// <summary>
    /// The speaking rate for one pilot line: the Speech Speed setting times the load factor, clamped to the range the
    /// Speech Speed setting allows.
    /// </summary>
    /// <param name="baseRate">The Settings › Speech Speed value.</param>
    /// <param name="loadFactor">The load factor the server computes for the frequency's band. A NaN, infinite or non-positive value counts as 1.0.</param>
    /// <returns>The rate to hand the synthesizer.</returns>
    public static double Apply(double baseRate, double loadFactor)
    {
        double factor = loadFactor;
        if (!double.IsFinite(loadFactor) || loadFactor <= 0)
        {
            Log.LogWarning("Pilot transmission carried an invalid speech rate factor {LoadFactor}; using 1.0", loadFactor);
            factor = 1.0;
        }

        return Math.Clamp(baseRate * factor, UserPreferences.PilotVoiceSpeechRateMin, UserPreferences.PilotVoiceSpeechRateMax);
    }
}
