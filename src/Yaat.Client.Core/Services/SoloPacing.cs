using Yaat.Sim.Simulation;

namespace Yaat.Client.Services;

/// <summary>
/// The solo-training pacing values as the client shows them. The parking call-up pace travels and is stored as a rate
/// percent (100% = one call-up every 20 s, 0 = paused), but every control shows it as an interval: paused, or once
/// per 10-120 s. Shared by the session flyout, the scenario-setup dialog and the Settings window.
/// </summary>
public static class SoloPacing
{
    private const int MinIntervalSeconds = 10;
    private const int MaxIntervalSeconds = 120;
    private const double RateTimesIntervalAt100Percent = ScenarioPacing.ParkingInitialCallupBaseIntervalSeconds * 100;
    private const double IntervalRoundingStepSeconds = 10.0;

    /// <summary>Snaps an interval to 0 (paused) or the 10-120 s the controls offer.</summary>
    public static int NormalizeParkingInitialCallupIntervalSeconds(int seconds) =>
        seconds <= 0 ? 0 : Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds);

    /// <summary>The interval, rounded to 10 s, that a stored call-up rate percent stands for.</summary>
    public static int ParkingInitialCallupRateToIntervalSeconds(int ratePercent)
    {
        int rate = ScenarioPacing.ClampParkingInitialCallupPercent(ratePercent);
        if (rate <= 0)
        {
            return 0;
        }

        int seconds = (int)(Math.Round((RateTimesIntervalAt100Percent / rate) / IntervalRoundingStepSeconds) * IntervalRoundingStepSeconds);
        return NormalizeParkingInitialCallupIntervalSeconds(seconds);
    }

    /// <summary>The call-up rate percent an interval stands for; 0 when paused.</summary>
    public static int ParkingInitialCallupIntervalSecondsToRate(int seconds)
    {
        int interval = NormalizeParkingInitialCallupIntervalSeconds(seconds);
        if (interval <= 0)
        {
            return 0;
        }

        return ScenarioPacing.ClampParkingInitialCallupPercent((int)Math.Round(RateTimesIntervalAt100Percent / interval));
    }

    /// <summary>"Paused", or "Once per N sec".</summary>
    public static string FormatParkingInitialCallupInterval(int seconds)
    {
        int interval = NormalizeParkingInitialCallupIntervalSeconds(seconds);
        return interval <= 0 ? "Paused" : $"Once per {interval} sec";
    }

    /// <summary>The stored Settings pacing defaults: the parking call-up rate and the arrival generator rate, in percent.</summary>
    public static (int ParkingInitialCallupRatePercent, int ArrivalGeneratorRatePercent) LoadDefaults(UserPreferences preferences) =>
        (preferences.SoloParkingInitialCallupRatePercent, preferences.SoloArrivalGeneratorRatePercent);

    /// <summary>
    /// The pacing pair a scenario load sends, for every load path: the value chosen in the setup dialog for each control
    /// it showed, and the stored Settings default for each control it did not (a load with no dialog showed neither).
    /// </summary>
    /// <param name="showParking">Whether the setup dialog showed the parking call-up control.</param>
    /// <param name="showArrival">Whether the setup dialog showed the arrival generator control.</param>
    /// <param name="chosen">The dialog's values, as rate percents.</param>
    /// <param name="defaults">The stored Settings defaults (<see cref="LoadDefaults"/>).</param>
    public static (int ParkingInitialCallupRatePercent, int ArrivalGeneratorRatePercent) SelectLoadRates(
        bool showParking,
        bool showArrival,
        (int ParkingInitialCallupRatePercent, int ArrivalGeneratorRatePercent) chosen,
        (int ParkingInitialCallupRatePercent, int ArrivalGeneratorRatePercent) defaults
    ) =>
        (
            showParking ? chosen.ParkingInitialCallupRatePercent : defaults.ParkingInitialCallupRatePercent,
            showArrival ? chosen.ArrivalGeneratorRatePercent : defaults.ArrivalGeneratorRatePercent
        );
}
