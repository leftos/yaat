using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Snapshot members removed without a schema bump: STARS <c>CaSuppressedWith</c> (CASUP is now a flag on the alert) and
/// the ERAM pointout's <c>IsRecipientSuppressed</c>. A recording written while they existed must still load, because the
/// recording deserializer skips a member the DTO no longer declares.
/// </summary>
public class RemovedSnapshotMembersTests
{
    private const string RecordingWithCaSuppressedWith = "TestData/issue412-wrong-runway-pattern-recording.zip";

    public RemovedSnapshotMembersTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Recording_WrittenWithCaSuppressedWith_StillLoadsItsAircraft()
    {
        using RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingWithCaSuppressedWith);
        Assert.NotNull(archive);

        StateSnapshotDto snapshot = archive.ReadSnapshot(0);

        Assert.NotEmpty(snapshot.Aircraft);
        foreach (AircraftSnapshotDto dto in snapshot.Aircraft)
        {
            Assert.Equal(dto.Callsign, AircraftState.FromSnapshot(dto, null).Callsign);
        }
    }

    [Fact]
    public void EramPointout_WrittenWithIsRecipientSuppressed_StillDeserializes()
    {
        const string json =
            """{"OriginatingFacility":"ZOA","OriginatingSector":"44","ReceivingFacility":"ZOA","ReceivingSector":"40","IsAcknowledged":true,"IsRecipientSuppressed":true,"IsRSideCleared":false,"IsDSideCleared":false}""";

        EramPointoutStateDto? dto = JsonSerializer.Deserialize<EramPointoutStateDto>(json, RecordingJsonOptions.Default);

        Assert.NotNull(dto);
        Assert.Equal("40", dto.ReceivingSector);
        Assert.True(dto.IsAcknowledged);
    }
}
