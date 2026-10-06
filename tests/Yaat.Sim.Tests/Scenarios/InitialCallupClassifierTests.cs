using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// The initial call-up plan a spawn gets from where it sits and its timed presets (YAAT-308), with the preset strings
/// the ZOA scenario inventory found on real spawns.
/// </summary>
[Collection("NavDbMutator")]
public class InitialCallupClassifierTests
{
    private static readonly InitialCallupSpawn AtStand = new(IsRunwaySpawn: false, HasTaxiGraph: true);

    public InitialCallupClassifierTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static List<PresetCommand> Presets(params string[] commands) => [.. commands.Select(c => new PresetCommand { Command = c })];

    private static InitialCallupPlan AtStandWith(params string[] commands) =>
        InitialCallupClassifier.Classify("TEST1", Presets(commands), AtStand).Plan;

    [Fact]
    public void NoPresetsAtAStand_StandCall() => Assert.Equal(InitialCallupPlan.StandCall, AtStandWith());

    [Fact]
    public void PushOnly_AfterPush() => Assert.Equal(InitialCallupPlan.AfterPush, AtStandWith("PUSH Z"));

    [Fact]
    public void PushThenWaitTaxiToSpot_AfterTaxiArrival() =>
        Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("PUSH T9", "WAIT 51 TAXI T9 $9"));

    [Fact]
    public void WaitTaxiToSpot_AfterTaxiArrival() => Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("WAIT 30 TAXI M4 M1 $1"));

    [Fact]
    public void TaxiToTaxiwayHoldShort_AfterTaxiArrival() => Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("TAXI T421 C HS C"));

    [Fact]
    public void TaxiToRunwayFromAStand_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("TAXI B1 A 35L"));

    [Fact]
    public void RwyFormTaxiFromAStand_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("RWY 30 T U W W1"));

    [Fact]
    public void PushThenWaitTaxiToRunway_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("PUSH B3", "WAIT 60 TAXI B3 B W W1 30"));

    [Fact]
    public void TaxiToRunway_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("TAXI A1 1R"));

    [Fact]
    public void TaxiWithARunwayHoldShortAsItsLastStop_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("TAXI A HS 28L"));

    [Fact]
    public void TaxiWithARunwayHoldShortThenASpotHoldShort_AfterTaxiArrival() =>
        // The HS clause owns every following token, so $5 is a spot hold short listed after the runway bar: the runway
        // is not the route's last stop.
        Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("TAXI B HS 28L $5"));

    [Fact]
    public void TaxiToASpotWithARunwayHoldShort_AfterTaxiArrival() =>
        Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("TAXI B $5 HS 28L"));

    [Fact]
    public void TaxiToAStandWithARunwayHoldShort_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("TAXI B @22 HS 28L"));

    [Fact]
    public void TaxiToAStand_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("TAXI A @GA7"));

    [Fact]
    public void AirTaxiToASpot_AfterTaxiArrival() => Assert.Equal(InitialCallupPlan.AfterTaxiArrival, AtStandWith("ATXI $7A"));

    [Fact]
    public void AirTaxiToARunway_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("ATXI 28L"));

    [Fact]
    public void AirTaxiToAHelipadOrStand_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("ATXI @FDX1"));

    [Fact]
    public void FollowGround_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("FOLLOWG N738SP"));

    [Fact]
    public void FollowGroundAfterAPush_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("PUSH Z", "WAIT 60 FOLLOWG N738SP"));

    [Fact]
    public void NoTaxiGraph_None()
    {
        var noGraph = new InitialCallupSpawn(IsRunwaySpawn: false, HasTaxiGraph: false);

        Assert.Equal(InitialCallupPlan.None, InitialCallupClassifier.Classify("N312PC", Presets(), noGraph).Plan);
    }

    [Fact]
    public void GraphlessGroundSpawnWithASayPreset_None()
    {
        var noGraph = new InitialCallupSpawn(IsRunwaySpawn: false, HasTaxiGraph: false);

        Assert.Equal(InitialCallupPlan.None, InitialCallupClassifier.Classify("N312PC", Presets("SAY REQUEST IFR CLEARANCE"), noGraph).Plan);
    }

    private static InitialCallupPlan OnRunwayWith(string callsign, params string[] commands) =>
        InitialCallupClassifier.Classify(callsign, Presets(commands), new InitialCallupSpawn(IsRunwaySpawn: true, HasTaxiGraph: false)).Plan;

    [Theory]
    [InlineData("CTO")] // Y-KG FAT PTACs: SKW5336 on FAT 29R, CTO at 0 s
    [InlineData("CTOMLT")]
    public void RunwaySpawnWithATakeoffPreset_None(string takeoff) => Assert.Equal(InitialCallupPlan.None, OnRunwayWith("SKW5336", takeoff));

    [Fact]
    public void RunwaySpawnWithASayPreset_RunwaySayOnly() =>
        Assert.Equal(InitialCallupPlan.RunwaySayOnly, OnRunwayWith("EJA682", "SAY READY FOR RELEASE"));

    [Fact]
    public void RunwaySpawnWithASayAndATakeoffPreset_None() =>
        Assert.Equal(InitialCallupPlan.None, OnRunwayWith("EJA682", "SAY READY FOR RELEASE", "WAIT 60 CTO"));

    [Fact]
    public void RunwaySpawnWithNoPreset_RunwayNoPreset() =>
        // S3-BAY-2 (A): N513SJ C421 lined up on AUN 25 with no preset.
        Assert.Equal(InitialCallupPlan.RunwayNoPreset, OnRunwayWith("N513SJ"));

    [Fact]
    public void RunwaySpawnOnALayoutWithATaxiGraph_StillFollowsTheRunwayRules()
    {
        var onRunway = new InitialCallupSpawn(IsRunwaySpawn: true, HasTaxiGraph: true);

        Assert.Equal(InitialCallupPlan.RunwayNoPreset, InitialCallupClassifier.Classify("N98W", Presets(), onRunway).Plan);
    }

    private static PresetTaxiStop? StopAtStandWith(params string[] commands) =>
        InitialCallupClassifier.Classify("TEST1", Presets(commands), AtStand).TaxiStop;

    [Fact]
    public void WaitTaxiToSpot_RecordsTheSpot() =>
        Assert.Equal(new PresetTaxiStop(PresetTaxiStopKind.Spot, "1", null), StopAtStandWith("PUSH M4", "WAIT 30 TAXI M4 M1 $1"));

    [Fact]
    public void TaxiToTaxiwayHoldShort_RecordsTheHoldShort() =>
        Assert.Equal(new PresetTaxiStop(PresetTaxiStopKind.TaxiwayHoldShort, "C", null), StopAtStandWith("TAXI T41W C HS C"));

    [Fact]
    public void TaxiWithNoDestination_RecordsTheLastTaxiway() =>
        Assert.Equal(new PresetTaxiStop(PresetTaxiStopKind.RouteEnd, "C", null), StopAtStandWith("TAXI B C"));

    [Fact]
    public void AirTaxiToASpot_RecordsTheSpot() => Assert.Equal(new PresetTaxiStop(PresetTaxiStopKind.Spot, "7A", null), StopAtStandWith("ATXI $7A"));

    [Fact]
    public void AirTaxiWithNoDestination_None() => Assert.Equal(InitialCallupPlan.None, AtStandWith("ATXI"));

    [Fact]
    public void PushOnly_RecordsNoStop() => Assert.Null(StopAtStandWith("PUSH Z"));

    [Fact]
    public void UnparseablePreset_IsSkippedAndTheRestClassified() => Assert.Equal(InitialCallupPlan.AfterPush, AtStandWith("XYZZY PLUGH", "PUSH Z"));
}
