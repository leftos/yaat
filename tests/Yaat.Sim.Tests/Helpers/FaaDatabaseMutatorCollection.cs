using Xunit;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Tests that set <see cref="FaaAircraftDatabase"/> to a known state and put it back run alone, so no other test reads it
/// mid-change.
/// </summary>
[CollectionDefinition("FaaDatabaseMutator", DisableParallelization = true)]
public class FaaDatabaseMutatorCollection { }
