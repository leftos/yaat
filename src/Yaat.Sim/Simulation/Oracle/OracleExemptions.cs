namespace Yaat.Sim.Simulation.Oracle;

/// <summary>
/// Divergence paths that are legitimately permanent, and therefore are not entries in the generated baseline.
///
/// Two lists with two lifetimes. The baseline file holds what the tick paths <em>currently</em> disagree about and
/// is meant to shrink to nothing. This registry holds what no amount of unification would fix — a value that is not
/// simulation state at all, such as one sampled from the wall clock. Additions arrive as diffs and each one carries
/// a reason, so the list stays reviewable; if it starts growing, that is the signal that something is being accepted
/// here which belongs in the baseline instead.
///
/// Empty by design at creation. Anything the first sweeps turn up is a divergence to retire until proven otherwise.
/// </summary>
public static class OracleExemptions
{
    /// <summary>Normalized path (see <see cref="DivergencePath.Normalize"/>) to the reason it can never converge.</summary>
    public static readonly IReadOnlyDictionary<string, string> PermanentlyAccepted = new Dictionary<string, string>(StringComparer.Ordinal);

    public static bool IsExempt(string normalizedPath) => PermanentlyAccepted.ContainsKey(normalizedPath);
}
