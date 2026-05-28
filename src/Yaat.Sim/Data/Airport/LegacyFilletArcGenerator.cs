namespace Yaat.Sim.Data.Airport;

/// <summary>
/// <see cref="IFilletArcGenerator"/> adapter delegating to the existing static
/// <see cref="FilletArcGenerator.Apply"/> implementation.
/// </summary>
public sealed class LegacyFilletArcGenerator : IFilletArcGenerator
{
    public string Id => "legacy";

    public string DisplayName => "Legacy (pair + cleanup passes)";

    public FilletStatistics Apply(AirportGroundLayout layout) => FilletArcGenerator.Apply(layout);
}
