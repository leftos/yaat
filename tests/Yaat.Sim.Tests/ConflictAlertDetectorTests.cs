using Xunit;

namespace Yaat.Sim.Tests;

public class ConflictAlertDetectorTests
{
    // KOAK area — two aircraft near each other
    private const double BaseLat = 37.721;
    private const double BaseLon = -122.221;

    private static AircraftState MakeAircraft(
        string callsign = "AAL100",
        double lat = BaseLat,
        double lon = BaseLon,
        double altitude = 5000,
        double heading = 360,
        double groundSpeed = 250,
        double verticalSpeed = 0,
        string transponderMode = "C",
        bool isOnGround = false
    )
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Altitude = altitude,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            IndicatedAirspeed = groundSpeed,
            VerticalSpeed = verticalSpeed,
            TransponderMode = transponderMode,
            IsOnGround = isOnGround,
        };
    }

    // Offset longitude to get ~N nm separation at BaseLat
    // 1° lon ≈ 47.5nm at 37.7°N
    private static double LonOffsetForNm(double nm) => nm / (60.0 * Math.Cos(BaseLat * Math.PI / 180.0));

    // -------------------------------------------------------------------------
    // Basic detection
    // -------------------------------------------------------------------------

    [Fact]
    public void Converging_SameAltitude_WithinThreshold_Detected()
    {
        // Two aircraft 2nm apart at same altitude, converging
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), altitude: 5000, heading: 270, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
        Assert.Equal("AAL100", result[0].CallsignA);
        Assert.Equal("UAL200", result[0].CallsignB);
    }

    [Fact]
    public void SamePosition_SameAltitude_Detected()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
    }

    [Fact]
    public void SamePosition_VerticalSeparated_NotDetected()
    {
        // Same position but >1000ft vertical — no conflict
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 6100);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void HorizontalSeparated_SameAltitude_NotDetected()
    {
        // 5nm apart at same altitude — no conflict (>3nm)
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(5.0), altitude: 5000);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Current-only detection (no prediction)
    // -------------------------------------------------------------------------

    [Fact]
    public void Diverging_Within_Threshold_StillDetected()
    {
        // Two aircraft 2.5nm apart, same altitude, flying AWAY from each other
        // CA only checks current separation — direction doesn't matter
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.5), altitude: 5000, heading: 90, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
    }

    [Fact]
    public void JustOutsideThreshold_NotDetected_EvenIfConverging()
    {
        // 3.1nm apart (outside 3nm threshold) and converging — no prediction, so no alert
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 300);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.1), altitude: 5000, heading: 270, groundSpeed: 300);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void FarApart_SameAltitude_NotDetected()
    {
        // 4nm apart at same altitude — no conflict
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), altitude: 5000, heading: 90, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Hysteresis
    // -------------------------------------------------------------------------

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsAt_HysteresisThreshold()
    {
        // 3.2nm apart (between normal 3.0 and hysteresis 3.3) — would NOT trigger new alert
        // but should remain if already in conflict
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.2), altitude: 5000, heading: 90);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");

        // Without existing: no detection (3.2 > 3.0, and they're diverging)
        var fresh = ConflictAlertDetector.Detect([a, b]);
        Assert.Empty(fresh);

        // With existing: still in conflict (3.2 < 3.3 hysteresis)
        var existing = new HashSet<string> { id };
        var hysteresis = ConflictAlertDetector.Detect([a, b], existing);
        Assert.Single(hysteresis);
    }

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsWhenBothDimensionsExceed()
    {
        // 3.5nm apart AND 1200ft vertical — both exceed hysteresis thresholds → clears
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.5), altitude: 6200);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");
        var existing = new HashSet<string> { id };

        var result = ConflictAlertDetector.Detect([a, b], existing);

        Assert.Empty(result);
    }

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsWhenHorizontalExceeds()
    {
        // 3.5nm apart (exceeds horizontal hysteresis) but only 900ft vertical (within vertical hysteresis)
        // Either dimension exceeding hysteresis clears the alert
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.5), altitude: 5900);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");
        var existing = new HashSet<string> { id };

        var result = ConflictAlertDetector.Detect([a, b], existing);

        Assert.Empty(result);
    }

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsWhenVerticalExceeds()
    {
        // 2nm apart (within horizontal hysteresis) but 1200ft vertical (exceeds vertical hysteresis)
        // Either dimension exceeding hysteresis clears the alert
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), altitude: 6200);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");
        var existing = new HashSet<string> { id };

        var result = ConflictAlertDetector.Detect([a, b], existing);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Filtering
    // -------------------------------------------------------------------------

    [Fact]
    public void GroundAircraft_Skipped()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000, isOnGround: true);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void NoModeC_Skipped()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000, transponderMode: "S");

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void IsCaInhibited_Skipped()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000);
        b.IsCaInhibited = true;

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Conflict ID
    // -------------------------------------------------------------------------

    [Fact]
    public void ConflictId_Deterministic_Sorted()
    {
        string id1 = ConflictAlertDetector.MakeConflictId("UAL200", "AAL100");
        string id2 = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");

        Assert.Equal(id1, id2);
        Assert.Equal("CA_AAL100_UAL200", id1);
    }

    // -------------------------------------------------------------------------
    // Final approach suppression
    // -------------------------------------------------------------------------

    [Fact]
    public void FinalApproach_OtherInCorridor_Suppressed()
    {
        // Aircraft on final approach heading 284 (KOAK 28R)
        var onFinal = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 3000, heading: 284, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList();

        // Other aircraft 1nm ahead along course, same altitude
        var (aheadLat, aheadLon) = GeoMath.ProjectPoint(BaseLat, BaseLon, new TrueHeading(284), 1.0);
        var other = MakeAircraft("UAL200", aheadLat, aheadLon, altitude: 3000, heading: 284, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([onFinal, other]);

        Assert.Empty(result);
    }

    [Fact]
    public void FinalApproach_OtherBehind_NotSuppressed()
    {
        // Aircraft on final heading 284
        var onFinal = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 3000, heading: 284, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList();

        // Other aircraft 1nm BEHIND (opposite of approach course) — behind has negative along-track
        var (behindLat, behindLon) = GeoMath.ProjectPoint(BaseLat, BaseLon, new TrueHeading(284 + 180), 1.0);
        var other = MakeAircraft("UAL200", behindLat, behindLon, altitude: 3000, heading: 284, groundSpeed: 200);

        var result = ConflictAlertDetector.Detect([onFinal, other]);

        // Behind the approach aircraft → not in corridor → not suppressed → CA fires
        Assert.Single(result);
    }

    // -------------------------------------------------------------------------
    // Multiple pairs
    // -------------------------------------------------------------------------

    [Fact]
    public void ThreeAircraft_TwoConflicts()
    {
        // A, B, C all at same position and altitude — 3 pairs
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("BBB200", altitude: 5000);
        var c = MakeAircraft("CCC300", altitude: 5000);

        var result = ConflictAlertDetector.Detect([a, b, c]);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void EmptyList_NoCrash()
    {
        var result = ConflictAlertDetector.Detect([]);
        Assert.Empty(result);
    }

    [Fact]
    public void SingleAircraft_NoCrash()
    {
        var a = MakeAircraft("AAL100");
        var result = ConflictAlertDetector.Detect([a]);
        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Current-only: vertical speed is irrelevant
    // -------------------------------------------------------------------------

    [Fact]
    public void CurrentViolation_DetectedRegardlessOfVerticalSpeed()
    {
        // 800ft apart (within threshold) — detected even though climbing apart
        var a = MakeAircraft("AAL100", altitude: 5000, verticalSpeed: 2000);
        var b = MakeAircraft("UAL200", altitude: 4200, verticalSpeed: -1000);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
    }

    [Fact]
    public void VerticalSeparated_NotDetected_EvenIfDescendingToward()
    {
        // 1100ft apart (outside threshold) — not detected even though converging vertically
        var a = MakeAircraft("AAL100", altitude: 6100, verticalSpeed: -2000);
        var b = MakeAircraft("UAL200", altitude: 5000, verticalSpeed: 0);

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Both on final approach
    // -------------------------------------------------------------------------

    [Fact]
    public void BothOnFinalApproach_InCorridor_Suppressed()
    {
        // Both aircraft on final, same runway, one ahead of the other
        var leader = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 3000, heading: 284, groundSpeed: 140);
        leader.Phases = MakeFinalApproachPhaseList();

        var (aheadLat, aheadLon) = GeoMath.ProjectPoint(BaseLat, BaseLon, new TrueHeading(284), 1.5);
        var follower = MakeAircraft("UAL200", aheadLat, aheadLon, altitude: 3200, heading: 284, groundSpeed: 140);
        follower.Phases = MakeFinalApproachPhaseList();

        var result = ConflictAlertDetector.Detect([leader, follower]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // VFR thresholds (target resolution: 0.25nm / 500ft)
    // -------------------------------------------------------------------------

    [Fact]
    public void VfrPair_UsesTargetResolution_Detected()
    {
        // Two VFR aircraft 0.2nm apart, 400ft vertical → detected (within 0.25nm / 500ft)
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.2), altitude: 5400);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
    }

    [Fact]
    public void VfrPair_UsesTargetResolution_NotDetected()
    {
        // Two VFR aircraft 0.3nm apart, 400ft vertical → not detected (outside 0.25nm)
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.3), altitude: 5400);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void VfrPair_Uses500ftVertical_NotDetected()
    {
        // Two VFR aircraft same position, 600ft vertical → not detected (outside 500ft)
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightRules = "VFR";
        var b = MakeAircraft("N67890", altitude: 5600);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void IfrVfr_Mixed_UsesVfrThresholds()
    {
        // One IFR + one VFR, 0.2nm apart, 400ft vertical → detected (VFR thresholds when either is VFR)
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.2), altitude: 5400);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Single(result);
    }

    [Fact]
    public void IfrVfr_Mixed_OutsideVfrThreshold_NotDetected()
    {
        // One IFR + one VFR, 2nm apart, 800ft vertical → not detected
        // Would be CA under IFR thresholds (2nm < 3nm, 800ft < 1000ft) but not under VFR (2nm > 0.25nm)
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(2.0), altitude: 5800);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void VfrHysteresis_ExistingConflict_ClearsAtVfrHysteresis()
    {
        // Existing VFR conflict at 0.28nm (between 0.25 entry and 0.30 hysteresis) stays active
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.28), altitude: 5000);
        b.FlightRules = "VFR";

        string id = ConflictAlertDetector.MakeConflictId("N12345", "N67890");

        // Without existing: no detection (0.28 > 0.25)
        var fresh = ConflictAlertDetector.Detect([a, b]);
        Assert.Empty(fresh);

        // With existing: still in conflict (0.28 < 0.30 hysteresis)
        var existing = new HashSet<string> { id };
        var hysteresis = ConflictAlertDetector.Detect([a, b], existing);
        Assert.Single(hysteresis);
    }

    // -------------------------------------------------------------------------
    // Standby transponder filtering
    // -------------------------------------------------------------------------

    [Fact]
    public void Standby_Vs_ModeC_Skipped()
    {
        // One Mode C + one Standby → no CA
        var a = MakeAircraft("AAL100", altitude: 5000, transponderMode: "C");
        var b = MakeAircraft("UAL200", altitude: 5000, transponderMode: "S");

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    [Fact]
    public void Standby_Vs_Standby_Skipped()
    {
        // Both Standby → no CA
        var a = MakeAircraft("AAL100", altitude: 5000, transponderMode: "S");
        var b = MakeAircraft("UAL200", altitude: 5000, transponderMode: "S");

        var result = ConflictAlertDetector.Detect([a, b]);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Final approach helpers
    // -------------------------------------------------------------------------

    private static Phases.PhaseList MakeFinalApproachPhaseList()
    {
        var phases = new Phases.PhaseList();
        var fap = new Phases.Tower.FinalApproachPhase();
        fap.Status = Phases.PhaseStatus.Active;
        phases.Phases.Add(fap);
        return phases;
    }
}
