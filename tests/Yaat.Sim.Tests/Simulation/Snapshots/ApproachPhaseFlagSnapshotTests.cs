using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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

    /// <summary>
    /// A boundary hold that held through a controller vector under RMOB keeps that flag through a snapshot, so a rewind or
    /// seek mid-hold still resumes the assigned heading when the hold ends. A fresh hold's snapshot carries no such key.
    /// </summary>
    [Fact]
    public void AirspaceBoundaryHoldPhase_HeldThroughVector_SurvivesSnapshotRoundTrip()
    {
        string fresh = JsonSerializer.Serialize<PhaseDto>(BoundaryHold().ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain("HeldThroughVector", fresh, StringComparison.Ordinal);

        string held = JsonSerializer.Serialize<PhaseDto>(HeldThroughVectorDto(), RecordingJsonOptions.Default);
        string restored = Resnapshot(held);

        Assert.Contains("\"HeldThroughVector\":true", restored, StringComparison.Ordinal);
        Assert.Contains("\"HeldThroughVector\":true", Resnapshot(restored), StringComparison.Ordinal);
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

    private static AirspaceBoundaryHoldPhase BoundaryHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = "SFO",
            NameText = "SFO",
            ReferencePosition = new LatLon(37.62, -122.885),
            OrbitDirection = TurnDirection.Right,
        };

    /// <summary>The snapshot of a boundary hold that held through a controller vector under RMOB.</summary>
    private static AirspaceBoundaryHoldPhaseDto HeldThroughVectorDto() =>
        new()
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 0,
            AirspaceClass = (int)AirspaceClass.Bravo,
            Ident = "SFO",
            NameText = "SFO",
            ReferenceLat = 37.62,
            ReferenceLon = -122.885,
            OrbitDirection = (int)TurnDirection.Right,
            CumulativeTurn = 0,
            LastHeadingDeg = 90,
            Started = true,
            HeldThroughVector = true,
        };

    private static string Resnapshot(string json)
    {
        AirspaceBoundaryHoldPhaseDto dto = Assert.IsType<AirspaceBoundaryHoldPhaseDto>(
            JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default)
        );
        return JsonSerializer.Serialize<PhaseDto>(AirspaceBoundaryHoldPhase.FromSnapshot(dto).ToSnapshot(), RecordingJsonOptions.Default);
    }

    private static HoldingPatternPhase RoundTrip(HoldingPatternPhase phase)
    {
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        HoldingPatternPhaseDto dto = Assert.IsType<HoldingPatternPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        return HoldingPatternPhase.FromSnapshot(dto);
    }
}
