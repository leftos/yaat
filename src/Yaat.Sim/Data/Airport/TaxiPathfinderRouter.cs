namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Runtime selector for the active <see cref="ITaxiPathfinder"/> implementation.
/// Production code routes all pathfinding calls through <see cref="Current"/>.
/// Tests continue to call <see cref="TaxiPathfinder"/> static methods directly.
///
/// <para>
/// Switch implementations by setting <see cref="UseV2"/> to true, or by
/// assigning <see cref="Current"/> directly for finer-grained control
/// (e.g., a custom wrapper in integration tests).
/// </para>
///
/// <para>
/// Default: <see cref="TaxiPathfinderV1Adapter"/> (v1 static methods, unchanged behavior).
/// </para>
/// </summary>
public static class TaxiPathfinderRouter
{
    private static ITaxiPathfinder _current = new TaxiPathfinderV1Adapter();

    /// <summary>
    /// The active pathfinder instance used by all production callers.
    /// Defaults to a <see cref="TaxiPathfinderV1Adapter"/>. Thread-safety is
    /// not guaranteed across concurrent assignments; switch only at startup or
    /// in single-threaded test setup.
    /// </summary>
    public static ITaxiPathfinder Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>
    /// Convenience setter. When set to true, replaces <see cref="Current"/> with a
    /// <see cref="TaxiPathfinderV2"/> instance. When set to false, restores a
    /// <see cref="TaxiPathfinderV1Adapter"/>. Has no effect when <see cref="Current"/>
    /// was replaced directly via the setter.
    /// </summary>
    public static bool UseV2
    {
        set => _current = value ? new TaxiPathfinderV2() : new TaxiPathfinderV1Adapter();
    }
}
