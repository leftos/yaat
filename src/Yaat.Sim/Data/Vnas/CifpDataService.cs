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

        var supplementary = CifpPathResolver.ResolveSupplementaryBundledPath(options);
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
                SupplementaryCifpFilePath is not null ? " (supplementary bundle for retired procedures)" : ""
            );
        }
    }

    /// <summary>
    /// Default resolve options for client/server: download/cache current AIRAC only.
    /// Supplementary bundled CIFP is not wired here — tests pass an explicit
    /// <see cref="CifpResolveOptions.BundledGzPath"/> via <see cref="Testing.TestVnasData"/>.
    /// To enable retired-procedure lookup in production, ship <c>FAACIFP18.gz</c> under the app
    /// <c>Data/</c> folder (or pass custom <see cref="CifpResolveOptions"/> at startup).
    /// </summary>
    public static CifpResolveOptions CreateDefaultOptions() => new(AllowDownload: true);

    public void Dispose() { }
}
