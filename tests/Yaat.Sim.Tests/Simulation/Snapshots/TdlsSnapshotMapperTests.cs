using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// <see cref="TdlsSnapshotMapper"/> round-trips the vTDLS session — items with their clearances, the dumped
/// lockout, the active ops configs, the pending auto-WILCOs and the id counter — restores by replacing, and
/// leaves the facility configs alone (a load re-derives those from the ARTCC before the restore runs).
/// </summary>
public class TdlsSnapshotMapperTests
{
    private static readonly DateTime Created = new(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TdlsClearance SentPayload = new()
    {
        Expect = "10 MIN AFT DP",
        Sid = "OAKLAND4",
        Transition = "ALTAM",
        InitialAlt = "5000",
        DepFreq = "120.9",
    };

    private static TdlsState Seeded()
    {
        var tdls = new TdlsState();
        lock (tdls.Gate)
        {
            tdls.Items["TDLS_1"] = new TdlsItemRecord(
                Id: "TDLS_1",
                AircraftId: "AAL100",
                Cid: "1234567",
                FacilityId: "OAK",
                Status: TdlsItemStatus.Pending,
                Sequence: 1,
                CreatedUtc: Created,
                SentUtc: null,
                WilcoUtc: null,
                ExpiresUtc: Created.AddHours(2),
                SentPayload: null
            );
            tdls.Items["TDLS_2"] = new TdlsItemRecord(
                Id: "TDLS_2",
                AircraftId: "N42ZZ",
                Cid: "1234568",
                FacilityId: "OAK",
                Status: TdlsItemStatus.Sent,
                Sequence: 2,
                CreatedUtc: Created.AddMinutes(1),
                SentUtc: Created.AddMinutes(2),
                WilcoUtc: null,
                ExpiresUtc: Created.AddHours(2).AddMinutes(1),
                SentPayload: SentPayload
            );
            tdls.Dumped.Add(new DumpedKey("OAK", "N999XX"));
            tdls.ActiveOpConfigIds["OAK"] = "cfg-north";
            tdls.ScheduledWilcoAt["TDLS_2"] = Created.AddMinutes(2).AddSeconds(3);
            tdls.NextItemId = 3;
        }
        return tdls;
    }

    [Fact]
    public void RoundTrip_PreservesItemsDumpedLockoutAndOpsConfig()
    {
        var dto = TdlsSnapshotMapper.Capture(Seeded());

        Assert.Equal(2, dto.Items.Count);
        Assert.Single(dto.Dumped);
        Assert.Single(dto.ActiveOpConfigs);
        Assert.Equal(3, dto.NextItemId);

        var restored = new TdlsState();
        TdlsSnapshotMapper.Restore(restored, dto);

        lock (restored.Gate)
        {
            Assert.Equal(2, restored.Items.Count);
            Assert.Equal(3, restored.NextItemId);
            Assert.Contains(new DumpedKey("OAK", "N999XX"), restored.Dumped);
            Assert.Equal("cfg-north", restored.ActiveOpConfigIds["OAK"]);

            var sent = restored.Items["TDLS_2"];
            Assert.Equal(TdlsItemStatus.Sent, sent.Status);
            Assert.Equal(SentPayload, sent.SentPayload);
            Assert.Equal(Created.AddMinutes(2), sent.SentUtc);
        }
    }

    /// <summary>The pending auto-WILCO is session-clock state, so a restored run acknowledges at the second the live one did.</summary>
    [Fact]
    public void RoundTrip_PreservesTheScheduledWilcoInstant()
    {
        var dto = TdlsSnapshotMapper.Capture(Seeded());
        var scheduled = Assert.Single(dto.ScheduledWilco);
        Assert.Equal("TDLS_2", scheduled.ItemId);

        var restored = new TdlsState();
        TdlsSnapshotMapper.Restore(restored, dto);

        Assert.Equal(Created.AddMinutes(2).AddSeconds(3), restored.ScheduledWilcoAt["TDLS_2"]);
    }

    [Fact]
    public void Restore_FromAnEmptySnapshot_ClearsThePopulatedSession()
    {
        var empty = TdlsSnapshotMapper.Capture(new TdlsState());

        var populated = Seeded();
        TdlsSnapshotMapper.Restore(populated, empty);

        lock (populated.Gate)
        {
            Assert.Empty(populated.Items);
            Assert.Empty(populated.Dumped);
            Assert.Empty(populated.ActiveOpConfigIds);
            Assert.Empty(populated.ScheduledWilcoAt);
            Assert.Equal(1, populated.NextItemId);
        }
    }

    /// <summary>
    /// A restore runs right after a load re-derived the facility configs, so clearing the session must not
    /// take them with it — <see cref="TdlsState.Reset"/> (scenario unload) is the one that does.
    /// </summary>
    [Fact]
    public void ClearSession_KeepsTheFacilityConfigs_ResetDoesNot()
    {
        var tdls = Seeded();
        tdls.Configs["OAK"] = new TdlsConfig { MandatoryExpect = true };

        tdls.ClearSession();
        Assert.Empty(tdls.Items);
        Assert.True(tdls.Configs.ContainsKey("OAK"));

        tdls.Reset();
        Assert.Empty(tdls.Configs);
    }

    [Fact]
    public void Restore_LeavesTheFacilityConfigsInPlace()
    {
        var target = new TdlsState();
        target.Configs["OAK"] = new TdlsConfig { MandatoryExpect = true };

        TdlsSnapshotMapper.Restore(target, TdlsSnapshotMapper.Capture(Seeded()));

        Assert.True(target.Configs.ContainsKey("OAK"));
        Assert.Equal(2, target.Items.Count);
    }
}
