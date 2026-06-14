using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Downloads and caches FAA CIFP (Coded Instrument Flight
/// Procedures) data. One zip file per AIRAC cycle, extracted
/// to %LOCALAPPDATA%/yaat/cache/cifp/.
/// </summary>
public sealed class CifpDataService : IDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger<CifpDataService>();

    /// <summary>
    /// Path to the extracted FAACIFP18 text file for the current AIRAC cycle, or null if
    /// download/extraction failed.
    /// </summary>
    public string? CifpFilePath { get; private set; }

    /// <summary>
    /// Ordered supplementary CIFP chain (newest→oldest) for procedures absent from the current cycle —
    /// the cached prior AIRAC cycles within the recency cap, plus any configured bundle as the oldest
    /// fallback. Excludes <see cref="CifpFilePath"/>. Empty when no older source is available.
    /// </summary>
    public IReadOnlyList<string> SupplementaryCifpFilePaths { get; private set; } = [];

    public CifpDataService() { }

    public Task InitializeAsync() => InitializeAsync(options: null);

    public async Task InitializeAsync(CifpResolveOptions? options)
    {
        options ??= CreateDefaultOptions();
        CifpFilePath = await CifpPathResolver.EnsureCurrentCycleAsync(options).ConfigureAwait(false);

        // Supplementary CIFP chain for procedures absent from the current cycle's file (e.g. a SID dropped
        // during an amendment/rename gap, like NIMI5 → NIMI6, or retired a few cycles back). The cached
        // prior AIRAC cycles within the recency cap (newest→oldest) — the app caches each cycle it
        // downloads, so this auto-accumulates with no shipped data — then any explicitly-configured bundle
        // (tests pass one via BundledGzPath) as the oldest fallback. Procedure resolution walks the chain
        // to the most recent cycle that still carries the procedure.
        var chain = new List<string>();
        if (CifpPathResolver.ResolvedCycleId is { } currentCycle)
        {
            chain.AddRange(CifpPathResolver.ResolveSupplementaryChainFromCache(currentCycle, CifpPathResolver.MaxSupplementaryLookbackCycles));
        }

        if (CifpPathResolver.ResolveSupplementaryBundledPath(options) is { } bundled)
        {
            chain.Add(bundled);
        }

        SupplementaryCifpFilePaths = chain
            .Where(p => CifpFilePath is null || !string.Equals(p, CifpFilePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (CifpFilePath is not null)
        {
            Log.LogInformation(
                "CIFP data ready for cycle {Cycle}{Supplementary}",
                CifpPathResolver.ResolvedCycleId ?? AiracCycle.GetCurrentCycleId(),
                SupplementaryCifpFilePaths.Count > 0
                    ? $" (with {SupplementaryCifpFilePaths.Count} supplementary CIFP cycle(s) for retired procedures)"
                    : ""
            );
        }
    }

    /// <summary>
    /// Default resolve options for client/server: download/cache the current AIRAC cycle. Retired-procedure
    /// lookup is served by the cached prior cycles within the recency cap (see
    /// <see cref="CifpPathResolver.ResolveSupplementaryChainFromCache(string, int)"/>) — no shipped bundle
    /// required. Tests additionally pass an explicit <see cref="CifpResolveOptions.BundledGzPath"/> via
    /// <see cref="Testing.TestVnasData"/> so a clean environment still has a supplementary source.
    /// </summary>
    public static CifpResolveOptions CreateDefaultOptions() => new(AllowDownload: true);

    public void Dispose() { }
}
