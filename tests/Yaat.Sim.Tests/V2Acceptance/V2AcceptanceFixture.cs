using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// Collection fixture that flips the ground stack to all-V2 for the duration of the
/// "V2 Acceptance" collection: pathfinder (<see cref="TaxiPathfinderRouter"/>) and navigator
/// (<see cref="GroundNavigatorRouter"/>). The third layer — fillet geometry — is selected
/// per-layout via <c>new TestAirportGroundData(FilletMode.V2)</c>, not a global, so it stays the
/// test's responsibility, not the fixture's.
///
/// <para>
/// <see cref="TaxiPathfinderRouter.Current"/> and <see cref="GroundNavigatorRouter.UseV2"/> are
/// process-global statics read by the production taxi path, so the collection that uses this
/// fixture is marked <c>DisableParallelization = true</c>. xUnit runs parallel-capable
/// collections first (in parallel), then parallel-disabled collections sequentially — so no
/// V1-default test runs concurrently with the flip. The ctor flips to V2 and <see cref="Dispose"/>
/// restores V1, leaving the production default for any later sequential collection.
/// </para>
///
/// <para>
/// This is the permanent stand-in for the manual three-file source flip used while building out
/// Ground-Graph-V2. It is NOT the joint flip — production defaults stay V1 until then.
/// </para>
/// </summary>
public sealed class V2AcceptanceFixture : IDisposable
{
    public V2AcceptanceFixture()
    {
        TaxiPathfinderRouter.UseV2 = true;
        GroundNavigatorRouter.UseV2 = true;
    }

    public void Dispose()
    {
        // Post joint-flip the production default is already V2, so this fixture is a no-op and is
        // slated for removal. Keep V2 on dispose rather than reverting to V1 — a revert would flip
        // any later sequential collection back to the now-deleted V1 stack.
        TaxiPathfinderRouter.UseV2 = true;
        GroundNavigatorRouter.UseV2 = true;
    }
}

/// <summary>
/// Binds the all-V2 ground stack (<see cref="V2AcceptanceFixture"/>) to the "V2 Acceptance"
/// collection and disables parallelization so the global router flip cannot race the V1-default
/// suite. Tests that drive an aircraft over V2 fillet + V2 pathfinder + V2 navigator belong here.
/// </summary>
[CollectionDefinition("V2 Acceptance", DisableParallelization = true)]
public sealed class V2AcceptanceCollection : ICollectionFixture<V2AcceptanceFixture>;
