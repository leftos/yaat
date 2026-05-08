namespace Yaat.Sim.Simulation;

public static class ScenarioPacing
{
    public const double ParkingInitialCallupBaseIntervalSeconds = 20.0;

    public static int ClampArrivalGeneratorPercent(int percent) => Math.Clamp(percent, 0, 100);

    public static int ClampParkingInitialCallupPercent(int percent) => Math.Clamp(percent, 0, 200);

    public static int ClampPercent(int percent) => ClampArrivalGeneratorPercent(percent);

    public static double EffectiveParkingInitialCallupIntervalSeconds(int ratePercent)
    {
        var rate = ClampParkingInitialCallupPercent(ratePercent);
        return rate <= 0 ? double.PositiveInfinity : ParkingInitialCallupBaseIntervalSeconds * (100.0 / rate);
    }

    public static double EffectiveArrivalGeneratorIntervalSeconds(int intervalTime, int ratePercent)
    {
        var rate = ClampArrivalGeneratorPercent(ratePercent);
        return rate <= 0 ? double.PositiveInfinity : intervalTime * (100.0 / rate);
    }

    public static bool TryReserveParkingInitialCallupSlot(SimScenarioState scenario, double nowSeconds)
    {
        var rate = ClampParkingInitialCallupPercent(scenario.SoloParkingInitialCallupRatePercent);
        if (rate <= 0)
        {
            return false;
        }

        if (double.IsPositiveInfinity(scenario.NextSoloParkingInitialCallupSlotSeconds))
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = nowSeconds;
        }

        if (scenario.NextSoloParkingInitialCallupSlotSeconds > nowSeconds)
        {
            return false;
        }

        scenario.NextSoloParkingInitialCallupSlotSeconds = nowSeconds + EffectiveParkingInitialCallupIntervalSeconds(rate);
        return true;
    }
}
