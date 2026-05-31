using Xunit;

namespace Yaat.Sim.Tests.Acceptance;

/// <summary>
/// Serialized test collection for the heavy full-stack acceptance sims (fillet graph + pathfinder +
/// navigator). Fillet geometry is chosen per-layout via <c>new TestAirportGroundData(FilletMode.Standard)</c>.
/// <c>DisableParallelization = true</c> keeps these long, file-writing (TickRecorder) acceptance runs
/// from contending with one another.
/// </summary>
[CollectionDefinition("Acceptance", DisableParallelization = true)]
public sealed class AcceptanceCollection;
