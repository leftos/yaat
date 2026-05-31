namespace Yaat.Sim.Data.Airport;

/// <summary>
/// All implemented <see cref="IFilletArcGenerator"/> instances, safe for enumeration.
/// </summary>
public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } = [NullFilletArcGenerator.Instance, new FilletArcGeneratorV2()];

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
