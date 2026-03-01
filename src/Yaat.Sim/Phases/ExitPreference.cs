namespace Yaat.Sim.Phases;

public enum ExitSide { Left, Right }

public sealed class ExitPreference
{
    public ExitSide? Side { get; init; }
    public string? Taxiway { get; init; }
}
