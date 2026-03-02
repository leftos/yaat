namespace Yaat.Sim.Data;

/// <summary>
/// Metadata for a video map from the ARTCC configuration.
/// </summary>
public sealed class VideoMapMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required List<string> Tags { get; init; }
    public required string SourceFileName { get; init; }

    /// <summary>"A" or "B" brightness category for STARS display.</summary>
    public required string BrightnessCategory { get; init; }

    public required int StarsId { get; init; }
    public required bool AlwaysVisible { get; init; }
    public required bool TdmOnly { get; init; }
}
