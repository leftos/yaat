namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Clean-room fillet generator (plan-then-execute). Not implemented until V2 planner/executor land.
/// </summary>
public sealed class FilletArcGeneratorV2 : IFilletArcGenerator
{
    public string Id => "v2";

    public string DisplayName => "V2 (plan-then-execute)";

    public FilletStatistics Apply(AirportGroundLayout layout) =>
        throw new NotImplementedException("FilletArcGeneratorV2 is not implemented yet; use FilletMode.Legacy.");
}
