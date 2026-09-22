using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Serialized test collection for the tests that assign <c>GroundConflictDetector.DebugSink</c>. The sink is a static
/// field with no per-test isolation, so two tests setting it at once would each capture the other's lines — or drop
/// their own when the other clears it. <c>DisableParallelization = true</c> is what keeps them apart, since the
/// assembly runs its collections in parallel (<c>xunit.runner.json</c>).
/// </summary>
[CollectionDefinition("GroundConflictDebugSink", DisableParallelization = true)]
public sealed class GroundConflictDebugSinkCollection;
