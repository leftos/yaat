using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// The approach flags that tell the published missed approach and the approach's hold-in-lieu apart survive a snapshot in
/// both directions: <see cref="ApproachNavigationPhase.IsMissedApproach"/>, <see cref="HoldingPatternPhase.IsHoldInLieu"/>
/// and <see cref="HoldingPatternPhase.IsMissedApproachHold"/>. A snapshot written before the missed-approach-hold field
/// existed reads it false.
/// </summary>
public class ApproachPhaseFlagSnapshotTests
{
    private static readonly ApproachFix Fix = new("REJOY", 38.1, -122.1);

    private static HoldingPatternPhase Hold(bool isHoldInLieu, bool isMissedApproachHold) =>
        new()
        {
            FixName = "REJOY",
            FixLat = 38.1,
            FixLon = -122.1,
            InboundCourse = 224,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            IsHoldInLieu = isHoldInLieu,
            IsMissedApproachHold = isMissedApproachHold,
        };

    [Fact]
    public void HoldingPatternPhase_FlagsSurviveSnapshotRoundTrip()
    {
        HoldingPatternPhase restored = RoundTrip(Hold(isHoldInLieu: true, isMissedApproachHold: true));

        Assert.True(restored.IsHoldInLieu);
        Assert.True(restored.IsMissedApproachHold);
    }

    [Fact]
    public void ApproachNavigationPhase_MissedApproachSurvivesSnapshotRoundTrip()
    {
        var phase = new ApproachNavigationPhase { Fixes = [Fix], IsMissedApproach = true };
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        ApproachNavigationPhaseDto dto = Assert.IsType<ApproachNavigationPhaseDto>(
            JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default)
        );

        var restored = ApproachNavigationPhase.FromSnapshot(dto);

        Assert.True(restored.IsMissedApproach);
    }

    /// <summary>An approach fix's CIFP speed type survives a snapshot, rather than coming back "at or below" (YAAT-146).</summary>
    [Theory]
    [InlineData(CifpSpeedRestrictionType.AtOrBelow)]
    [InlineData(CifpSpeedRestrictionType.AtOrAbove)]
    [InlineData(CifpSpeedRestrictionType.Mandatory)]
    public void ApproachNavigationPhase_FixSpeedTypeSurvivesSnapshotRoundTrip(CifpSpeedRestrictionType type)
    {
        ApproachFix fix = new("REJOY", 38.1, -122.1, SpeedKts: 210) { SpeedType = type };
        var phase = new ApproachNavigationPhase { Fixes = [fix] };
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        ApproachNavigationPhaseDto dto = Assert.IsType<ApproachNavigationPhaseDto>(
            JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default)
        );

        var restored = ApproachNavigationPhase.FromSnapshot(dto);

        Assert.Equal(new CifpSpeedRestriction(210, type), Assert.Single(restored.Fixes).SpeedRestriction);
    }

    /// <summary>A snapshot written before the type was kept (code 1 on every fix speed) restores "at or below", the default.</summary>
    [Fact]
    public void ApproachFixDto_LegacySpeedTypeCode_RestoresAtOrBelow()
    {
        var dto = new ApproachFixDto
        {
            Name = "REJOY",
            Lat = 38.1,
            Lon = -122.1,
            SpeedRestriction = new SpeedRestrictionDto { Type = 1, Speed = 200 },
            IsFlyOver = false,
            IsFaf = false,
            LegType = 0,
            IsArc = false,
        };

        Assert.Equal(CifpSpeedRestrictionType.AtOrBelow, ApproachFix.FromSnapshot(dto).SpeedType);
    }

    [Fact]
    public void HoldingPatternPhaseDto_WithoutMissedApproachHoldField_RestoresFalse()
    {
        string json = JsonSerializer.Serialize<PhaseDto>(
            Hold(isHoldInLieu: true, isMissedApproachHold: false).ToSnapshot(),
            RecordingJsonOptions.Default
        );

        Assert.DoesNotContain("IsMissedApproachHold", json, StringComparison.Ordinal);
        HoldingPatternPhaseDto dto = Assert.IsType<HoldingPatternPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restored = HoldingPatternPhase.FromSnapshot(dto);

        Assert.False(restored.IsMissedApproachHold);
        Assert.True(restored.IsHoldInLieu);
    }

    private static HoldingPatternPhase RoundTrip(HoldingPatternPhase phase)
    {
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        HoldingPatternPhaseDto dto = Assert.IsType<HoldingPatternPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        return HoldingPatternPhase.FromSnapshot(dto);
    }
}
