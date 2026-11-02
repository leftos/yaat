using Xunit;

namespace Yaat.Client.Tests;

/// <summary>
/// The tests that set or watch the process-wide <see cref="Yaat.Client.Services.Speech.SherpaNativeLoader.CpuFallbackReason"/>
/// and its event; they run one at a time so none sees another's value.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuFallbackReasonCollection
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "CpuFallbackReason";
}
