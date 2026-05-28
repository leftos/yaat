namespace Yaat.Sim.Data.Airport;

/// <summary>
/// All registered <see cref="IFilletArcGenerator"/> implementations for enumeration,
/// parameterized tests, and LayoutInspector comparison.
/// </summary>
public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } =
    [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()];

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
