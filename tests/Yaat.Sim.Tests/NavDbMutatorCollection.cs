using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests in this collection mutate <see cref="Yaat.Sim.Data.NavigationDatabase.Instance"/> with
/// non-standard (synthetic) data. They run sequentially with each other to avoid cross-test
/// interference. Tests that only use <see cref="Yaat.Sim.Testing.TestVnasData.EnsureInitialized()"/>
/// do NOT need this collection — that call is idempotent and safe for parallel execution.
/// </summary>
[CollectionDefinition("NavDbMutator")]
public class NavDbMutatorCollection { }
