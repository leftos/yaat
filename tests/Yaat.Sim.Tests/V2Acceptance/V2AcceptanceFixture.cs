using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// Collection fixture for the "V2 Acceptance" collection. The ground navigator is unconditionally V2
/// (V1 is deleted), so this fixture only pins the pathfinder (<see cref="TaxiPathfinderRouter"/>) to V2;
/// the third layer — fillet geometry — is selected per-layout via
/// <c>new TestAirportGroundData(FilletMode.V2)</c>, not a global.
///
/// <para>
/// <see cref="TaxiPathfinderRouter.Current"/> is a process-global static read by the production taxi path,
/// so the collection that uses this fixture is marked <c>DisableParallelization = true</c>. The production
/// default is already V2 (the joint flip), so this pin is a redundancy guard; it goes away when the V1
/// pathfinder is deleted.
/// </para>
/// </summary>
public sealed class V2AcceptanceFixture : IDisposable
{
    public V2AcceptanceFixture()
    {
        TaxiPathfinderRouter.UseV2 = true;
    }

    public void Dispose()
    {
        // Keep V2 on dispose rather than reverting — the production default is V2 and there is no V1 to
        // fall back to.
        TaxiPathfinderRouter.UseV2 = true;
    }
}

/// <summary>
/// Binds the all-V2 ground stack (<see cref="V2AcceptanceFixture"/>) to the "V2 Acceptance" collection and
/// disables parallelization so the global pathfinder flip cannot race other collections. Tests that drive
/// an aircraft over V2 fillet + V2 pathfinder + the V2 navigator belong here.
/// </summary>
[CollectionDefinition("V2 Acceptance", DisableParallelization = true)]
public sealed class V2AcceptanceCollection : ICollectionFixture<V2AcceptanceFixture>;
