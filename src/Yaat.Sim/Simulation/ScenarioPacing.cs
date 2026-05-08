namespace Yaat.Sim.Simulation;

public static class ScenarioPacing
{
    public static int ClampPercent(int percent) => Math.Clamp(percent, 0, 100);

    public static double EffectiveArrivalGeneratorIntervalSeconds(int intervalTime, int ratePercent)
    {
        var rate = ClampPercent(ratePercent);
        return rate <= 0 ? double.PositiveInfinity : intervalTime * (100.0 / rate);
    }
}
