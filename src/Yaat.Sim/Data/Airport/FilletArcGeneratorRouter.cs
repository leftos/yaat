namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Runtime selector for the active <see cref="IFilletArcGenerator"/> implementation.
/// <see cref="GeoJsonParser"/> resolves fillet mode explicitly via
/// <see cref="FilletGeneratorFactory"/>; use this router when a caller needs to
/// switch legacy vs V2 without re-parsing (e.g. startup flag or integration tests).
///
/// <para>
/// Switch implementations by setting <see cref="UseV2"/> to true, or by assigning
/// <see cref="Current"/> directly. Defaults to legacy behavior.
/// </para>
/// </summary>
public static class FilletArcGeneratorRouter
{
    private static IFilletArcGenerator _current = FilletGeneratorFactory.Create(FilletMode.Legacy);

    /// <summary>
    /// The active fillet generator. Defaults to legacy. Thread-safety is not guaranteed
    /// across concurrent assignments; switch only at startup or in single-threaded test setup.
    /// </summary>
    public static IFilletArcGenerator Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>
    /// When set to true, replaces <see cref="Current"/> with <see cref="FilletArcGeneratorV2"/>.
    /// When set to false, restores <see cref="LegacyFilletArcGenerator"/>.
    /// </summary>
    public static bool UseV2
    {
        set => _current = FilletGeneratorFactory.Create(value ? FilletMode.V2 : FilletMode.Legacy);
    }
}
