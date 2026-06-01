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
    /// Optional bundled supplementary CIFP (retired procedures absent from the current cycle).
    /// Differs from <see cref="CifpFilePath"/> when the primary file is a downloaded cycle.
    /// </summary>
    public string? SupplementaryCifpFilePath { get; private set; }

    public CifpDataService() { }

    public Task InitializeAsync() => InitializeAsync(options: null);

    public async Task InitializeAsync(CifpResolveOptions? options)
    {
        options ??= CreateDefaultOptions();
        CifpFilePath = await CifpPathResolver.EnsureCurrentCycleAsync(options).ConfigureAwait(false);

        // Supplementary CIFP for procedures absent from the current cycle's file (e.g. a SID dropped
        // during an amendment/rename gap, like NIMI5 → NIMI6). Prefer the newest cached prior AIRAC
        // cycle — the app caches each cycle it downloads, so this auto-accumulates with no shipped data —
        // then fall back to any explicitly-configured bundle (tests pass one via BundledGzPath).
        var supplementary =
            (CifpPathResolver.ResolvedCycleId is { } currentCycle ? CifpPathResolver.ResolveSupplementaryFromCache(currentCycle) : null)
            ?? CifpPathResolver.ResolveSupplementaryBundledPath(options);
        if (supplementary is not null && CifpFilePath is not null && string.Equals(supplementary, CifpFilePath, StringComparison.OrdinalIgnoreCase))
        {
            supplementary = null;
        }

        SupplementaryCifpFilePath = supplementary;

        if (CifpFilePath is not null)
        {
            Log.LogInformation(
                "CIFP data ready for cycle {Cycle}{Supplementary}",
                CifpPathResolver.ResolvedCycleId ?? AiracCycle.GetCurrentCycleId(),
                SupplementaryCifpFilePath is not null ? " (with supplementary CIFP for retired procedures)" : ""
            );
        }
    }

    /// <summary>
    /// Default resolve options for client/server: download/cache the current AIRAC cycle. Retired-procedure
    /// lookup is served by the newest cached prior cycle (see
    /// <see cref="CifpPathResolver.ResolveSupplementaryFromCache(string)"/>) — no shipped bundle required.
    /// Tests additionally pass an explicit <see cref="CifpResolveOptions.BundledGzPath"/> via
    /// <see cref="Testing.TestVnasData"/> so a clean environment still has a supplementary source.
    /// </summary>
    public static CifpResolveOptions CreateDefaultOptions() => new(AllowDownload: true);

    public void Dispose() { }
}
