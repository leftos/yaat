namespace Yaat.Sim.Simulation;

/// <summary>
/// Hoists a leading <c>AS {tcp}</c> out of a scenario preset, bare or behind its WAIT/WAITD gate, so the engine can
/// dispatch the rest of the preset acting as that position. Typed commands never come here: their AS prefix is the
/// router's (<see cref="Commands.TrackResolver.ExtractAsPrefix"/>).
/// </summary>
public static class PresetActingAs
{
    /// <summary>
    /// (null, <paramref name="preset"/>) when the preset carries no leading AS; else (tcp, the preset without the
    /// <c>AS {tcp}</c> tokens, its other tokens joined by single spaces). <c>AS {tcp}</c> with nothing after it, an AS
    /// behind any other verb, or a WAIT/WAITD with no argument before the AS is not hoisted.
    /// </summary>
    public static (string? Tcp, string Command) Split(string preset)
    {
        string[] tokens = preset.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int asIndex = GateLength(tokens);
        if ((tokens.Length < asIndex + 3) || !tokens[asIndex].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            return (null, preset);
        }

        string command = string.Join(' ', tokens.Take(asIndex).Concat(tokens.Skip(asIndex + 2)));
        return (tokens[asIndex + 1], command);
    }

    /// <summary>The number of tokens a leading <c>WAIT n</c> / <c>WAITD n</c> gate takes: 2, else 0.</summary>
    private static int GateLength(string[] tokens) =>
        (tokens.Length >= 2)
        && (tokens[0].Equals("WAIT", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("WAITD", StringComparison.OrdinalIgnoreCase))
            ? 2
            : 0;
}
