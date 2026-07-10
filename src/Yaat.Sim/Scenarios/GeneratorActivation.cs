namespace Yaat.Sim.Scenarios;

/// <summary>
/// Decides whether a traffic generator may spawn on a given tick.
///
/// A generator is normally gated by its authored time window: it starts at <c>StartTimeOffset</c> and stops
/// after <c>MaxTime</c>, both measured in absolute scenario seconds. An instructor can override that window
/// live from the generator editor, and the override wins from that point on — ticking a generator's Active
/// box starts traffic immediately even if the clock is before the window opened or after it closed.
///
/// Activation is derived fresh each tick rather than latched, so a generator can be switched back on after
/// its window has expired.
/// </summary>
public static class GeneratorActivation
{
    public static bool IsActive(IGeneratorConfig config, double elapsedSeconds)
    {
        if (config.Enabled is { } manualOverride)
        {
            return manualOverride;
        }

        if (elapsedSeconds < config.StartTimeOffset)
        {
            return false;
        }

        return config.MaxTime is not { } maxTime || elapsedSeconds <= maxTime;
    }
}
