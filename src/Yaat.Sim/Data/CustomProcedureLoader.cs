namespace Yaat.Sim.Data;

/// <summary>
/// One ARTCC-supplied CIFP fragment file. <see cref="AirportIcaos"/> lists the airports whose records
/// the file actually contains, so procedure parsing can be scoped without re-scanning every fragment.
/// </summary>
public sealed record CustomProcedureFragment(string ArtccId, string FilePath, IReadOnlySet<string> AirportIcaos);

public sealed class CustomProcedureLoadResult
{
    public List<CustomProcedureFragment> Fragments { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Indexes ARTCC-supplied CIFP fragments from <c>Data/ARTCCs/{ARTCC}/Procedures/*.cifp</c>.
///
/// A fragment is verbatim ARINC 424 records copied out of a published CIFP cycle, pinning a procedure the
/// current FAA cycle has dropped (KOAK's NIMITZ SID is the motivating case). Because the records are
/// unmodified, <see cref="Vnas.CifpParser"/> reads a fragment exactly as it reads a full cycle file — this
/// loader only discovers which files exist and which airports each one covers.
///
/// Warn-don't-throw: an unreadable or record-less file adds a warning and is skipped; the rest still load.
/// </summary>
public static class CustomProcedureLoader
{
    /// <summary>ARINC 424 airport-id columns, and the minimum line length <see cref="Vnas.CifpParser"/> requires.</summary>
    private const int IcaoStart = 6;
    private const int IcaoEnd = 10;
    private const int MinRecordLength = 100;

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/Procedures/*.cifp</c> across every ARTCC subdirectory. ARTCC
    /// directories are walked in sorted order so a duplicate procedure resolves deterministically.
    /// </summary>
    public static CustomProcedureLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new CustomProcedureLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string categoryDir = Path.Combine(artccDir, "Procedures");
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            string artccId = Path.GetFileName(artccDir).ToUpperInvariant();
            foreach (var file in Directory.GetFiles(categoryDir, "*.cifp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                LoadFile(artccId, file, result);
            }
        }

        return result;
    }

    private static void LoadFile(string artccId, string filePath, CustomProcedureLoadResult result)
    {
        HashSet<string> icaos;
        try
        {
            icaos = ReadAirportIcaos(filePath);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to read {filePath}: {ex.Message}");
            return;
        }

        if (icaos.Count == 0)
        {
            result.Warnings.Add($"{filePath}: no CIFP airport records found (expected verbatim 'SUSAP' lines), skipping");
            return;
        }

        result.Fragments.Add(new CustomProcedureFragment(artccId, filePath, icaos));
    }

    /// <summary>
    /// Collects the distinct airport ICAO ids from a fragment's records, using the same line gate and the
    /// same column span <see cref="Vnas.CifpParser"/> keys on — so the index can never disagree with what
    /// the parser will subsequently find. Provenance headers and blank lines fail the gate and are ignored.
    /// </summary>
    private static HashSet<string> ReadAirportIcaos(string filePath)
    {
        var icaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(filePath))
        {
            if (line.Length < MinRecordLength || !line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            string icao = line[IcaoStart..IcaoEnd].Trim();
            if (icao.Length > 0)
            {
                icaos.Add(icao);
            }
        }

        return icaos;
    }
}
