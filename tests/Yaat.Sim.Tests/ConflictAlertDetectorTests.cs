using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class ConflictAlertDetectorTests
{
    // KOAK area — two aircraft near each other
    private const double BaseLat = 37.721;
    private const double BaseLon = -122.221;

    // KOAK 28R approximate geometry
    private const string KoakIcao = "KOAK";
    private const double Koak28RThreshLat = 37.7213;
    private const double Koak28RThreshLon = -122.2208;
    private const double Koak10LThreshLat = 37.7289;
    private const double Koak10LThreshLon = -122.2045;
    private const double KoakElevationFt = 6.0;
    private const double Koak28RHeading = 284.0;
    private const double Koak10LHeading = 104.0;

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

    private static ConflictAlertContext Ctx(HashSet<string>? existingIds = null) => new(existingIds ?? [], []);

    private static ConflictAlertContext CtxWithAirports(params string[] airports) => new([], [.. airports]);

    // -------------------------------------------------------------------------
    // Basic detection
    // -------------------------------------------------------------------------

    [Fact]
    public void Converging_SameAltitude_WithinThreshold_Detected()
    {
        // Two aircraft 2nm apart at same altitude, converging
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), altitude: 5000, heading: 270, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Single(result);
        Assert.Equal("AAL100", result[0].CallsignA);
        Assert.Equal("UAL200", result[0].CallsignB);
    }

    [Fact]
    public void SamePosition_SameAltitude_Detected()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void SamePosition_VerticalSeparated_NotDetected()
    {
        // Same position but >1000ft vertical — no conflict
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 6100);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void HorizontalSeparated_SameAltitude_NotDetected()
    {
        // 5nm apart at same altitude — no conflict (>3nm), both flying north so parallel (not diverging)
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 360, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(5.0), altitude: 5000, heading: 360, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Prediction and divergence
    // -------------------------------------------------------------------------

    [Fact]
    public void Diverging_WithinThreshold_NotDetected()
    {
        // Two aircraft 2.5nm apart, same altitude, flying AWAY from each other
        // Divergence check suppresses the alert
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.5), altitude: 5000, heading: 90, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void Converging_JustOutsideThreshold_DetectedByPrediction()
    {
        // 3.1nm apart (outside 3nm threshold) and converging at 300 kts each
        // 5-second prediction: each moves ~0.42nm closer → predicted ~2.26nm < 3nm
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 300);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.1), altitude: 5000, heading: 270, groundSpeed: 300);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void FarApart_SameAltitude_NotDetected()
    {
        // 4nm apart at same altitude, diverging — no conflict
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), altitude: 5000, heading: 90, groundSpeed: 250);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void Prediction_FarApart_ConvergingFast_Detected()
    {
        // 4nm apart but converging at 500 kts each (1000 kts closure rate)
        // 5-second prediction: closure ~1.39nm → predicted ~2.61nm < 3nm
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 500);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), altitude: 5000, heading: 270, groundSpeed: 500);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void Prediction_FarApart_SlowConverge_NotDetected()
    {
        // 5nm apart, converging at 150 kts each (300 kts closure)
        // 5-second prediction: closure ~0.42nm → predicted ~4.58nm > 3nm
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 90, groundSpeed: 150);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(5.0), altitude: 5000, heading: 270, groundSpeed: 150);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void Diverging_Horizontally_ButConverging_Vertically_Detected()
    {
        // 2nm apart horizontally (within threshold), diverging horizontally
        // But converging vertically (1100ft apart → predicted ~933ft)
        // Not fully diverging (only horizontal increases) → still alerts
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 6100, heading: 270, groundSpeed: 250, verticalSpeed: -2000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), altitude: 5000, heading: 90, groundSpeed: 250, verticalSpeed: 0);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        // Predicted vertical: 6100 + (-2000*5/60) = 5933, 5000 + 0 = 5000, gap = 933 ft < 1000 ft
        // Predicted horizontal increases (diverging) but predicted vertical decreases → not fully diverging
        Assert.Single(result);
    }

    [Fact]
    public void VerticalConverging_PredictionDetectsConflict()
    {
        // 1100ft apart vertically (outside threshold), same position
        // Aircraft descending toward each other → prediction catches violation
        var a = MakeAircraft("AAL100", altitude: 6100, verticalSpeed: -2000);
        var b = MakeAircraft("UAL200", altitude: 5000, verticalSpeed: 0);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        // Predicted: 6100 + (-2000*5/60) = 5933, gap = 933 ft < 1000 ft
        Assert.Single(result);
    }

    // -------------------------------------------------------------------------
    // Current violation with vertical divergence
    // -------------------------------------------------------------------------

    [Fact]
    public void CurrentViolation_VerticalDiverging_Suppressed()
    {
        // 800ft apart vertically (within threshold), same position, climbing apart
        // Predicted vertical: 5167 vs 4117 = 1050ft (outside threshold)
        // Separation IS increasing → suppressed per STARS spec
        var a = MakeAircraft("AAL100", altitude: 5000, verticalSpeed: 2000);
        var b = MakeAircraft("UAL200", altitude: 4200, verticalSpeed: -1000);

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Hysteresis
    // -------------------------------------------------------------------------

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsAt_HysteresisThreshold()
    {
        // 3.2nm apart (between normal 3.0 and hysteresis 3.3), diverging
        // Would NOT trigger new alert, but should remain if already in conflict
        // (hysteresis ignores divergence for existing alerts — only checks current separation)
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 360, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.2), altitude: 5000, heading: 360, groundSpeed: 250);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");

        // Without existing: no detection (3.2 > 3.0, parallel)
        var fresh = ConflictAlertDetector.Detect([a, b], Ctx());
        Assert.Empty(fresh);

        // With existing: still in conflict (3.2 < 3.3 hysteresis)
        var hysteresis = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));
        Assert.Single(hysteresis);
    }

    [Fact]
    public void Hysteresis_ExistingConflict_ClearsWhenBothDimensionsExceed()
    {
        // 3.5nm apart AND 1200ft vertical — both exceed hysteresis thresholds → clears
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(3.5), altitude: 6200);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");

        var result = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));

        Assert.Empty(result);
    }

    [Fact]
    public void Hysteresis_ExistingConflict_Diverging_Clears()
    {
        // Existing conflict, aircraft within hysteresis thresholds but fully diverging → clears
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, altitude: 5000, heading: 270, groundSpeed: 250);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), altitude: 5800, heading: 90, groundSpeed: 250, verticalSpeed: 500);

        string id = ConflictAlertDetector.MakeConflictId("AAL100", "UAL200");

        // Within hysteresis thresholds (2nm < 3.3, 800ft < 1100ft) but diverging both dimensions
        var result = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void NoModeC_Skipped()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000, transponderMode: "S");

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void IsCaInhibited_Skipped()
    {
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("UAL200", altitude: 5000);
        b.IsCaInhibited = true;

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

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
    // Final approach suppression (runway-threshold anchored)
    // -------------------------------------------------------------------------

    [Fact]
    public void FinalApproach_OtherInCorridor_Suppressed()
    {
        // Aircraft on final approach to KOAK 28R. Other aircraft in the approach corridor
        // ahead of the threshold along the extended centerline.
        SetupKoakNavDb();

        // Aircraft 5nm out on approach course (along reciprocal of 284°, i.e., 104° from threshold)
        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        // Other aircraft 3nm out on same course, same altitude
        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(KoakIcao));

        Assert.Empty(result);
    }

    [Fact]
    public void FinalApproach_OtherBeyond30Nm_NotSuppressed()
    {
        // Approach aircraft at 29nm, other at 31nm — only 2nm apart but other is outside 30 NM corridor
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 29.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 9000, heading: Koak28RHeading, groundSpeed: 200);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 31.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 9000, heading: Koak28RHeading, groundSpeed: 200);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void FinalApproach_OtherBehindThreshold_NotSuppressed()
    {
        // Other aircraft on the airport side of the threshold (negative along-track)
        // Approach aircraft close to threshold so they're within CA range of each other
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 2.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 800, heading: Koak28RHeading, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        // Other aircraft 0.5nm behind threshold (on the runway side)
        var runwayCourse = new TrueHeading(Koak28RHeading);
        var (behindLat, behindLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, runwayCourse, 0.5);
        var other = MakeAircraft("UAL200", behindLat, behindLon, altitude: 800, heading: Koak28RHeading, groundSpeed: 150);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void FinalApproach_OtherOutsideLateralWidth_NotSuppressed()
    {
        // Other aircraft 2.5nm cross-track from centerline — outside 2nm half-width
        // but within 3nm CA threshold (distance ≈ 2.5nm when at same along-track)
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 10.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 3500, heading: Koak28RHeading, groundSpeed: 180);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        // Position other aircraft at same along-track but 2.5nm perpendicular
        var perpCourse = new TrueHeading((Koak10LHeading + 90) % 360);
        var (baseLat, baseLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 10.0);
        var (otherLat, otherLon) = GeoMath.ProjectPoint(baseLat, baseLon, perpCourse, 2.5);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 3500, heading: Koak28RHeading, groundSpeed: 180);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void FinalApproach_OtherAboveGlideSlopeCeiling_NotSuppressed()
    {
        // Other aircraft above glideslope + 1500ft at its along-track distance
        // At 10nm: glideslope = 6 + 10*318 = 3186 ft, ceiling = 3186 + 1500 = 4686 ft
        // Place other at 4800ft (above ceiling). Approach aircraft at 4500ft (within 300ft → CA threshold).
        // Offset 1nm apart along approach to ensure clear non-divergent geometry.
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 10.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 4500, heading: Koak28RHeading, groundSpeed: 180);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        // Other aircraft 1nm closer to threshold — converging geometry (approach aircraft catching up)
        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 9.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 4800, heading: Koak28RHeading, groundSpeed: 150);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void FinalApproach_NonInternalAirport_NotSuppressed()
    {
        // Airport not in internal airports list → suppression does not apply
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        // Empty internal airports list — KOAK not included
        var result = ConflictAlertDetector.Detect([onFinal, other], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void FinalApproach_NonIcaoAirport_NotSuppressed()
    {
        // 3-char FAA LID airport → ICAO filter excludes it
        var faaLid = "OAK";
        var navDb = TestNavDbFactory.WithRunways(MakeKoak28RRunway(faaLid));
        NavigationDatabase.SetInstance(navDb);

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var onFinal = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);
        onFinal.Phases = MakeFinalApproachPhaseList(faaLid, "28R", Koak28RHeading);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([onFinal, other], CtxWithAirports(faaLid));

        Assert.Single(result);
    }

    [Fact]
    public void BothOnFinalApproach_InCorridor_Suppressed()
    {
        // Both aircraft on final to same runway, both in corridor → suppressed
        SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (leaderLat, leaderLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var leader = MakeAircraft("AAL100", leaderLat, leaderLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 140);
        leader.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        var (followerLat, followerLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 7.0);
        var follower = MakeAircraft("UAL200", followerLat, followerLon, altitude: 3000, heading: Koak28RHeading, groundSpeed: 140);
        follower.Phases = MakeFinalApproachPhaseList(KoakIcao, "28R", Koak28RHeading);

        var result = ConflictAlertDetector.Detect([leader, follower], CtxWithAirports(KoakIcao));

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Multiple pairs
    // -------------------------------------------------------------------------

    [Fact]
    public void ThreeAircraft_ThreeConflicts()
    {
        // A, B, C all at same position and altitude — 3 pairs
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("BBB200", altitude: 5000);
        var c = MakeAircraft("CCC300", altitude: 5000);

        var result = ConflictAlertDetector.Detect([a, b, c], Ctx());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void EmptyList_NoCrash()
    {
        var result = ConflictAlertDetector.Detect([], Ctx());
        Assert.Empty(result);
    }

    [Fact]
    public void SingleAircraft_NoCrash()
    {
        var a = MakeAircraft("AAL100");
        var result = ConflictAlertDetector.Detect([a], Ctx());
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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void IfrVfr_Mixed_UsesVfrThresholds()
    {
        // One IFR + one VFR, 0.2nm apart, 400ft vertical → detected (VFR thresholds when either is VFR)
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.2), altitude: 5400);
        b.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

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
        var fresh = ConflictAlertDetector.Detect([a, b], Ctx());
        Assert.Empty(fresh);

        // With existing: still in conflict (0.28 < 0.30 hysteresis)
        var hysteresis = ConflictAlertDetector.Detect([a, b], Ctx(new HashSet<string> { id }));
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

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void Standby_Vs_Standby_Skipped()
    {
        // Both Standby → no CA
        var a = MakeAircraft("AAL100", altitude: 5000, transponderMode: "S");
        var b = MakeAircraft("UAL200", altitude: 5000, transponderMode: "S");

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void UnsupportedGhostTrack_Skipped()
    {
        // Ghost track (IsUnsupported) at same position/altitude as real aircraft → no CA
        var real = MakeAircraft("AAL100", altitude: 5000);
        var ghost = MakeAircraft("UAL200", altitude: 5000);
        ghost.IsUnsupported = true;

        var result = ConflictAlertDetector.Detect([real, ghost], Ctx());

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo MakeKoak28RRunway(string airportId = KoakIcao) =>
        new()
        {
            AirportId = airportId,
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = Koak28RThreshLat,
            Lon1 = Koak28RThreshLon,
            Elevation1Ft = KoakElevationFt,
            TrueHeading1 = new TrueHeading(Koak28RHeading),
            Lat2 = Koak10LThreshLat,
            Lon2 = Koak10LThreshLon,
            Elevation2Ft = KoakElevationFt,
            TrueHeading2 = new TrueHeading(Koak10LHeading),
            LengthFt = 6213,
            WidthFt = 150,
        };

    private static void SetupKoakNavDb()
    {
        var navDb = TestNavDbFactory.WithRunways(MakeKoak28RRunway());
        NavigationDatabase.SetInstance(navDb);
    }

    private static Phases.PhaseList MakeFinalApproachPhaseList(string airportCode, string runwayId, double finalCourseHeading)
    {
        var phases = new Phases.PhaseList();
        var fap = new Phases.Tower.FinalApproachPhase();
        fap.Status = Phases.PhaseStatus.Active;
        phases.Phases.Add(fap);
        phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = $"I{runwayId}",
            AirportCode = airportCode,
            RunwayId = runwayId,
            FinalApproachCourse = new TrueHeading(finalCourseHeading),
        };
        return phases;
    }
}
