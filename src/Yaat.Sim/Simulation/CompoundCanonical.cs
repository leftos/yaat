using System.Text;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Unit-wise rewriting of a possibly-compound recorded canonical. Splitting on
/// <c>;</c> / <c>,</c> is safe because the compound parser owns those separators —
/// no strip payload can contain one. Leading/trailing whitespace around each unit
/// and the separators themselves are preserved verbatim.
/// </summary>
public static class CompoundCanonical
{
    /// <summary>
    /// Applies <paramref name="rewriteUnit"/> to every unit of <paramref name="canonical"/>
    /// (each unit is passed trimmed). Returns the original string instance when no
    /// unit changed, so callers can detect a no-op by reference or ordinal equality.
    /// </summary>
    public static string RewriteUnits(string canonical, Func<string, string> rewriteUnit)
    {
        if (canonical.IndexOfAny([';', ',']) < 0)
        {
            return RewritePreservingPadding(canonical, rewriteUnit);
        }

        var result = new StringBuilder(canonical.Length + 16);
        bool changed = false;
        int unitStart = 0;
        for (int i = 0; i <= canonical.Length; i++)
        {
            if (i < canonical.Length && canonical[i] is not (';' or ','))
            {
                continue;
            }
            string unit = canonical[unitStart..i];
            string rewritten = RewritePreservingPadding(unit, rewriteUnit);
            changed |= !ReferenceEquals(rewritten, unit);
            result.Append(rewritten);
            if (i < canonical.Length)
            {
                result.Append(canonical[i]);
            }
            unitStart = i + 1;
        }
        return changed ? result.ToString() : canonical;
    }

    private static string RewritePreservingPadding(string unit, Func<string, string> rewriteUnit)
    {
        string trimmed = unit.Trim();
        if (trimmed.Length == 0)
        {
            return unit;
        }
        string rewritten = rewriteUnit(trimmed);
        if (string.Equals(rewritten, trimmed, StringComparison.Ordinal))
        {
            return unit;
        }
        string lead = unit[..unit.IndexOf(trimmed[0])];
        string trail = unit[(lead.Length + trimmed.Length)..];
        return lead + rewritten + trail;
    }
}
