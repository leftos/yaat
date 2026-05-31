namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Maps <see cref="FilletMode"/> to a concrete <see cref="IFilletArcGenerator"/> instance.
/// </summary>
public static class FilletGeneratorFactory
{
    public static IFilletArcGenerator Create(FilletMode mode) =>
        mode switch
        {
            FilletMode.None => NullFilletArcGenerator.Instance,
            FilletMode.Standard => new FilletArcGenerator(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
}
