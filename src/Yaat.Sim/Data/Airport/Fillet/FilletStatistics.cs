namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Per-pass tallies returned by <see cref="IFilletArcGenerator.Apply"/>. Each field
/// also appears in the <c>LogInformation</c> summary the pass emits, but tests and
/// diagnostic tools can read these directly without scraping logs.
/// </summary>
public sealed record FilletStatistics(
    int FilletedNodes,
    int ArcsCreated,
    int CollinearMerges,
    int CoincidentNodesMerged,
    int OrphansRescued,
    int RedundantPreserveEdgesRemoved,
    int DuplicateCornerArcsRemoved,
    int ParallelBypassEdgesRemoved,
    int DirectShortensAdded
)
{
    /// <summary>Statistics for a no-op fillet pass (<see cref="FilletMode.None"/>).</summary>
    public static FilletStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
