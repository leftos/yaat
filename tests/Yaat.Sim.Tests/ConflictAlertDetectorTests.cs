using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class ConflictAlertDetectorTests
{
    public ConflictAlertDetectorTests()
    {
        TestVnasData.EnsureInitialized();
    }

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
            Position = new LatLon(lat, lon),
            Altitude = altitude,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            IndicatedAirspeed = groundSpeed,
            VerticalSpeed = verticalSpeed,
            Transponder = new AircraftTransponder { Mode = transponderMode },
            IsOnGround = isOnGround,
        };
    }

    // Offset longitude to get ~N nm separation at BaseLat
    // 1° lon ≈ 47.5nm at 37.7°N
    private static double LonOffsetForNm(double nm) => nm / (60.0 * Math.Cos(BaseLat * Math.PI / 180.0));

    private static ConflictAlertContext Ctx(HashSet<string>? existingIds = null) => new(existingIds ?? [], []);

    private static ConflictAlertContext CtxWithAirports(params string[] airports) =>
        new([], ConflictAlertDetector.BuildCorridors(airports, NavigationDatabase.Instance));

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
        b.Stars.IsCaInhibited = true;

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
    // Approach corridor suppression (runway-threshold anchored, purely geometric)
    // -------------------------------------------------------------------------

    [Fact]
    public void ApproachCorridor_BothInCorridor_Suppressed()
    {
        // Two aircraft on the extended KOAK 28R centerline at 5 NM and 3 NM, both
        // inside the 4 NM × 30 NM × glideslope-+1500-ft volume → suppressed.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var first = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([first, other], CtxWithAirports(KoakIcao));

        Assert.Empty(result);
    }

    [Fact]
    public void ApproachCorridor_OneInsideOneOutside_Suppressed()
    {
        // Either-track-in-corridor rule: track A on final at 5 NM (inside), track B
        // 31 NM out (outside the corridor) — A's presence in the volume suppresses CA
        // for the pair regardless of B's position. STARS does not consult phase or
        // approach state; the volume protects every track inside it.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var insider = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);

        // Place outsider close enough horizontally and vertically to trigger CA absent suppression.
        var (otherLat, otherLon) = GeoMath.ProjectPoint(acLat, acLon, new TrueHeading(0), 1.0);
        var outsider = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);

        var result = ConflictAlertDetector.Detect([insider, outsider], CtxWithAirports(KoakIcao));

        Assert.Empty(result);
    }

    [Fact]
    public void ApproachCorridor_BothBeyond30Nm_NotSuppressed()
    {
        // Both aircraft past the 30 NM corridor length — neither is inside the volume,
        // so suppression does not apply and CA fires.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 31.0);
        var first = MakeAircraft("AAL100", acLat, acLon, altitude: 9000, heading: Koak28RHeading, groundSpeed: 200);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 33.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 9000, heading: Koak28RHeading, groundSpeed: 200);

        var result = ConflictAlertDetector.Detect([first, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void ApproachCorridor_BothOutsideLateralWidth_NotSuppressed()
    {
        // Both aircraft > 2 NM cross-track from centerline (outside 4 NM corridor),
        // but close enough to each other for CA. Neither inside volume → CA fires.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var perpCourse = new TrueHeading((Koak10LHeading + 90) % 360);
        var (baseLat, baseLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 10.0);

        var (firstLat, firstLon) = GeoMath.ProjectPoint(baseLat, baseLon, perpCourse, 2.5);
        var first = MakeAircraft("AAL100", firstLat, firstLon, altitude: 3500, heading: Koak28RHeading, groundSpeed: 180);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(baseLat, baseLon, perpCourse, 2.7);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 3500, heading: Koak28RHeading, groundSpeed: 180);

        var result = ConflictAlertDetector.Detect([first, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void ApproachCorridor_BothAboveGlideSlopeCeiling_NotSuppressed()
    {
        // At 10 NM: glideslope ≈ 6 + 10*318 = 3186 ft, ceiling = 3186 + 1500 = 4686 ft.
        // Place both above 4686 ft so neither is inside the corridor volume.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 10.0);
        var first = MakeAircraft("AAL100", acLat, acLon, altitude: 4800, heading: Koak28RHeading, groundSpeed: 180);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 9.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 4900, heading: Koak28RHeading, groundSpeed: 150);

        var result = ConflictAlertDetector.Detect([first, other], CtxWithAirports(KoakIcao));

        Assert.Single(result);
    }

    [Fact]
    public void ApproachCorridor_NonInternalAirport_NotSuppressed()
    {
        // Empty internalAirports → no corridors built → no suppression.
        using var _navDb = SetupKoakNavDb();

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var first = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([first, other], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void ApproachCorridor_FaaLidAirport_Suppressed()
    {
        // 3-char FAA LID airport in internal airports list → corridor builds the same
        // as an ICAO. Mirrors what CommandDispatcher.ResolveAirport produces at runtime.
        var faaLid = "OAK";
        var navDb = TestNavDbFactory.WithRunways(MakeKoak28RRunway(faaLid));
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (acLat, acLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 5.0);
        var first = MakeAircraft("AAL100", acLat, acLon, altitude: 2500, heading: Koak28RHeading, groundSpeed: 150);

        var (otherLat, otherLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.0);
        var other = MakeAircraft("UAL200", otherLat, otherLon, altitude: 2000, heading: Koak28RHeading, groundSpeed: 140);

        var result = ConflictAlertDetector.Detect([first, other], CtxWithAirports(faaLid));

        Assert.Empty(result);
    }

    [Fact]
    public void ApproachCorridor_FaaLidAirport_Suppressed_RegressionForSfoBundle()
    {
        // Regression for the SFO S1-SFO-2 bug bundle: SKW3398 leading WJA1508 on I28R,
        // ~2.3 NM apart and ~951 ft vertical separation. Both inside the 28R corridor
        // → suppressed.
        var faaLid = "SFO";
        var navDb = TestNavDbFactory.WithRunways(MakeKoak28RRunway(faaLid));
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var outboundCourse = new TrueHeading(Koak10LHeading);

        var (leaderLat, leaderLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 0.83);
        var leader = MakeAircraft("SKW3398", leaderLat, leaderLon, altitude: 328, heading: Koak28RHeading, groundSpeed: 126);

        var (followerLat, followerLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 3.16);
        var follower = MakeAircraft("WJA1508", followerLat, followerLon, altitude: 1279, heading: Koak28RHeading, groundSpeed: 144);

        var result = ConflictAlertDetector.Detect([leader, follower], CtxWithAirports(faaLid));

        Assert.Empty(result);
    }

    [Fact]
    public void ApproachCorridor_VfrParallelFinalsNoActiveApproach_Suppressed()
    {
        // Regression for the OAK S2-OAK-3 bug bundle: two VFR pattern aircraft on final
        // to parallel runways 28L/28R, neither with an ActiveApproach (visual finals).
        // STARS does not consult phase or approach state — the corridor volumes apply
        // to every track inside them.
        var faaLid = "OAK";
        var rwy28R = MakeKoak28RRunway(faaLid);
        // Synthesize a parallel 28L 0.087 NM (~530 ft) south of 28R using a perpendicular projection.
        var perp = new TrueHeading((Koak28RHeading + 90) % 360);
        var (lat28L1, lon28L1) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, perp, 0.087);
        var (lat10R, lon10R) = GeoMath.ProjectPoint(Koak10LThreshLat, Koak10LThreshLon, perp, 0.087);
        var rwy28L = new RunwayInfo
        {
            AirportId = faaLid,
            Id = new RunwayIdentifier("28L", "10R"),
            Designator = "28L",
            Lat1 = lat28L1,
            Lon1 = lon28L1,
            Elevation1Ft = KoakElevationFt,
            TrueHeading1 = new TrueHeading(Koak28RHeading),
            Lat2 = lat10R,
            Lon2 = lon10R,
            Elevation2Ft = KoakElevationFt,
            TrueHeading2 = new TrueHeading(Koak10LHeading),
            LengthFt = 6213,
            WidthFt = 150,
        };
        var navDb = TestNavDbFactory.WithRunways(rwy28R, rwy28L);
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Both 1 NM final, ~0.087 NM laterally apart, both at 1000 ft, both VFR, no ActiveApproach.
        var outboundCourse = new TrueHeading(Koak10LHeading);
        var (a28RLat, a28RLon) = GeoMath.ProjectPoint(Koak28RThreshLat, Koak28RThreshLon, outboundCourse, 1.0);
        var on28R = MakeAircraft("N775JW", a28RLat, a28RLon, altitude: 1000, heading: Koak28RHeading, groundSpeed: 90);
        on28R.FlightPlan.FlightRules = "VFR";

        var (a28LLat, a28LLon) = GeoMath.ProjectPoint(lat28L1, lon28L1, outboundCourse, 1.0);
        var on28L = MakeAircraft("N70CS", a28LLat, a28LLon, altitude: 1000, heading: Koak28RHeading, groundSpeed: 90);
        on28L.FlightPlan.FlightRules = "VFR";

        Assert.Null(on28R.Phases?.ActiveApproach);
        Assert.Null(on28L.Phases?.ActiveApproach);

        var result = ConflictAlertDetector.Detect([on28R, on28L], CtxWithAirports(faaLid));

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
        a.FlightPlan.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.2), altitude: 5400);
        b.FlightPlan.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Single(result);
    }

    [Fact]
    public void VfrPair_UsesTargetResolution_NotDetected()
    {
        // Two VFR aircraft 0.3nm apart, 400ft vertical → not detected (outside 0.25nm)
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightPlan.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.3), altitude: 5400);
        b.FlightPlan.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void VfrPair_Uses500ftVertical_NotDetected()
    {
        // Two VFR aircraft same position, 600ft vertical → not detected (outside 500ft)
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightPlan.FlightRules = "VFR";
        var b = MakeAircraft("N67890", altitude: 5600);
        b.FlightPlan.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void IfrVfr_Mixed_UsesVfrThresholds()
    {
        // One IFR + one VFR, 0.2nm apart, 400ft vertical → detected (VFR thresholds when either is VFR)
        var a = MakeAircraft("AAL100", altitude: 5000);
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.2), altitude: 5400);
        b.FlightPlan.FlightRules = "VFR";

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
        b.FlightPlan.FlightRules = "VFR";

        var result = ConflictAlertDetector.Detect([a, b], Ctx());

        Assert.Empty(result);
    }

    [Fact]
    public void VfrHysteresis_ExistingConflict_ClearsAtVfrHysteresis()
    {
        // Existing VFR conflict at 0.28nm (between 0.25 entry and 0.30 hysteresis) stays active
        var a = MakeAircraft("N12345", altitude: 5000);
        a.FlightPlan.FlightRules = "VFR";
        var b = MakeAircraft("N67890", lon: BaseLon + LonOffsetForNm(0.28), altitude: 5000);
        b.FlightPlan.FlightRules = "VFR";

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
        ghost.Ghost.IsUnsupported = true;

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

    private static IDisposable SetupKoakNavDb()
    {
        var navDb = TestNavDbFactory.WithRunways(MakeKoak28RRunway());
        return NavigationDatabase.ScopedOverride(navDb);
    }
}
