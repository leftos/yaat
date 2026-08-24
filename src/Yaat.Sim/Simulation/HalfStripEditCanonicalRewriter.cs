namespace Yaat.Sim.Simulation;

/// <summary>
/// Rewrites the retired <c>HSE &lt;stripId&gt; line0\line1…</c> canonical (literal
/// half-strip field replacement by id, once emitted by the inline cell grid) into
/// the equivalent <c>HSA &lt;stripId&gt; …</c> id form, which replaces every line
/// literally. Used by <see cref="RecordingSchemaUpgrader"/> to migrate archived
/// action logs in place so sessions recorded before the verb was folded still replay.
///
/// <para>Idempotent: a canonical that does not start with the retired verb is
/// returned as the same instance, so the upgrade can be re-run safely and needs no
/// schema-version gate.</para>
/// </summary>
public static class HalfStripEditCanonicalRewriter
{
    private static readonly string[] RetiredVerbs = ["HSE", "HALFSTRIPEDIT"];

    /// <summary>Rewrites every retired-verb unit of a possibly-compound canonical.</summary>
    public static string Rewrite(string canonical)
    {
        return CompoundCanonical.RewriteUnits(canonical, RewriteUnit);
    }

    private static string RewriteUnit(string unit)
    {
        var spaceIdx = unit.IndexOf(' ');
        var verb = spaceIdx < 0 ? unit : unit[..spaceIdx];
        foreach (var retired in RetiredVerbs)
        {
            if (verb.Equals(retired, StringComparison.OrdinalIgnoreCase))
            {
                return spaceIdx < 0 ? "HSA" : $"HSA{unit[spaceIdx..]}";
            }
        }
        return unit;
    }
}
