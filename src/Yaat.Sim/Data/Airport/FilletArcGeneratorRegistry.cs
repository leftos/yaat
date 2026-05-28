namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Implemented <see cref="IFilletArcGenerator"/> instances safe for enumeration and
/// <see cref="FilletComparison"/>. <see cref="FilletArcGeneratorV2"/> is omitted until
/// step 3 — use <see cref="FilletGeneratorFactory.Create"/> for <see cref="FilletMode.V2"/>.
/// </summary>
public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } = [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator()];

    public static IFilletArcGenerator? GetById(string id)
    {
        foreach (var generator in All)
        {
            if (string.Equals(generator.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return generator;
            }
        }

        return null;
    }
}
