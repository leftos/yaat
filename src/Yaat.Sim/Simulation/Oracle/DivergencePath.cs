using System.Text;

namespace Yaat.Sim.Simulation.Oracle;

/// <summary>
/// Path shaping for oracle divergences. A concrete path names the exact leaf that differs
/// (<c>Aircraft[SWA123].Track.Owner.SectorId</c>); <see cref="Normalize"/> collapses every collection key to
/// <c>[*]</c> so the accepted-divergence baseline is a statement about fields rather than about which aircraft
/// happened to spawn in the run that generated it. Concrete paths stay in the drill-down report.
/// </summary>
public static class DivergencePath
{
    /// <summary>The collection key a normalized path carries in place of a callsign or an index.</summary>
    public const string AnyKey = "[*]";

    /// <summary>
    /// Collapses every <c>[key]</c> segment to <see cref="AnyKey"/>. An unterminated <c>[</c> is copied through
    /// verbatim rather than swallowing the rest of the path.
    /// </summary>
    public static string Normalize(string path)
    {
        if (!path.Contains('[', StringComparison.Ordinal))
        {
            return path;
        }

        var result = new StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] != '[')
            {
                result.Append(path[i]);
                continue;
            }

            int close = path.IndexOf(']', i);
            if (close < 0)
            {
                result.Append(path, i, path.Length - i);
                break;
            }

            result.Append(AnyKey);
            i = close;
        }

        return result.ToString();
    }
}
