namespace Yaat.Sim.Data.Airport;

/// <summary>
/// No-op fillet generator for <see cref="FilletMode.None"/> and raw-layout tests.
/// </summary>
public sealed class NullFilletArcGenerator : IFilletArcGenerator
{
    public static readonly NullFilletArcGenerator Instance = new();

    private NullFilletArcGenerator() { }

    public string Id => "none";

    public string DisplayName => "None (no fillet)";

    public FilletStatistics Apply(AirportGroundLayout layout) => FilletStatistics.Empty;
}
