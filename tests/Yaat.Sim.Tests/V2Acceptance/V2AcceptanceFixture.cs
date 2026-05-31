using Xunit;

namespace Yaat.Sim.Tests.V2Acceptance;

/// <summary>
/// Serialized test collection for the heavy full-stack V2 acceptance sims (V2 fillet + pathfinder +
/// navigator). All three layers are unconditionally V2 now — pathfinder and navigator have no runtime
/// selector, and fillet geometry is chosen per-layout via <c>new TestAirportGroundData(FilletMode.Standard)</c> —
/// so there is no global to pin. <c>DisableParallelization = true</c> keeps these long, file-writing
/// (TickRecorder) acceptance runs from contending with one another.
/// </summary>
[CollectionDefinition("V2 Acceptance", DisableParallelization = true)]
public sealed class V2AcceptanceCollection;
