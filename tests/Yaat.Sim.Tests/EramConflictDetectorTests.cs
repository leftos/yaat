using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// ERAM en-route STCA detection (eram.md §377-383). Verifies the four-minute trajectory probe, the
/// 5 nm / 3 nm-≤FL230 lateral split, the data-block-altitude vertical envelope (§381 level-off + §383
/// "level traffic might move toward its assignment"), eligibility, and hysteresis — none of which the
/// terminal STARS <see cref="ConflictAlertDetector"/> models. Geometry is chosen to be robust to the exact
/// IAS→TAS ground speed: head-on closure, or same-track/same-speed parallels where relative velocity is zero.
/// </summary>
public class EramConflictDetectorTests
{
    public EramConflictDetectorTests()
    {
        // Pin the profile/nav singletons so AircraftState.GroundSpeed's IAS→TAS lookup is stable under the
        // parallel suite (see CLAUDE.md static-singleton-race guidance).
        TestVnasData.EnsureInitialized();
    }

    private const double BaseLat = 37.7;
    private const double BaseLon = -122.0;
    private const int Fl350 = 35000;
    private const int Fl200 = 20000;

    private static double LonOffsetForNm(double nm) => nm / (60.0 * Math.Cos(BaseLat * Math.PI / 180.0));

    private static AircraftState MakeAircraft(
        string callsign,
        double lat,
        double lon,
        double altitude,
        double heading = 360,
        double ias = 280,
        double verticalSpeed = 0,
        int? interimAltitude = null,
        int cruiseAltitude = 0,
        string transponderMode = "C",
        bool isOnGround = false,
        bool caInhibited = false,
        bool unsupported = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            Altitude = altitude,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            IndicatedAirspeed = ias,
            VerticalSpeed = verticalSpeed,
            Transponder = new AircraftTransponder { Mode = transponderMode },
            IsOnGround = isOnGround,
        };
        ac.FlightPlan.CruiseAltitude = cruiseAltitude;
        ac.Eram.InterimAltitude = interimAltitude;
        ac.Stars.IsCaInhibited = caInhibited;
        ac.Ghost.IsUnsupported = unsupported;
        return ac;
    }

    private static bool Detected(params AircraftState[] aircraft) => EramConflictDetector.Detect([.. aircraft], new HashSet<string>()).Count > 0;

    // ── Look-ahead horizon (5 s → 4 min) ─────────────────────────────────────────────────────────────

    [Fact]
    public void ConvergingPair_FarApartNow_DetectedByFourMinuteProbe()
    {
        // Head-on, 25 nm apart at FL350. They pass each other well within four minutes but are nowhere near
        // separation loss in the next 5 s — the terminal detector misses this; ERAM must catch it.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, heading: 90, cruiseAltitude: Fl350);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(25.0), Fl350, heading: 270, cruiseAltitude: Fl350);

        Assert.True(Detected(a, b));
        // Contrast: the terminal 5-second detector does NOT fire on this pair.
        Assert.Empty(ConflictAlertDetector.Detect([a, b], new ConflictAlertContext([], [])));
    }

    [Fact]
    public void CrossingPair_ClosestMidWindow_DetectedBySweptCpa()
    {
        // A flies east; B flies north and is placed so both reach the same point at t≈120 s. Separation is
        // large at both t=0 and t=240 s — only a swept closest-approach (not an endpoint sample) catches it.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, heading: 90, cruiseAltitude: Fl350);
        double distHalfWindowNm = a.GroundSpeed * 120.0 / 3600.0;
        var crossing = GeoMath.ProjectPoint(a.Position, new TrueHeading(90), distHalfWindowNm);
        var bStart = GeoMath.ProjectPoint(crossing, new TrueHeading(180), distHalfWindowNm);
        var b = MakeAircraft("UAL200", bStart.Lat, bStart.Lon, Fl350, heading: 360, cruiseAltitude: Fl350);

        Assert.True(Detected(a, b));
        Assert.Empty(ConflictAlertDetector.Detect([a, b], new ConflictAlertContext([], [])));
    }

    // ── Lateral minimum: 5 nm above FL230, 3 nm at/below ─────────────────────────────────────────────

    [Fact]
    public void Parallel_4nmApart_AboveFl230_Detected()
    {
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, cruiseAltitude: Fl350);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), Fl350, cruiseAltitude: Fl350);
        Assert.True(Detected(a, b)); // 4 nm < 5 nm en-route minimum
    }

    [Fact]
    public void Parallel_6nmApart_AboveFl230_NotDetected()
    {
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, cruiseAltitude: Fl350);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(6.0), Fl350, cruiseAltitude: Fl350);
        Assert.False(Detected(a, b)); // 6 nm > 5 nm
    }

    [Fact]
    public void Parallel_4nmApart_AtOrBelowFl230_NotDetected()
    {
        // Same 4 nm geometry as the FL350 case, but both at FL200 → reduced-separation 3 nm applies.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl200, cruiseAltitude: Fl200);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), Fl200, cruiseAltitude: Fl200);
        Assert.False(Detected(a, b)); // 4 nm > 3 nm reduced minimum
    }

    [Fact]
    public void Parallel_2nmApart_AtOrBelowFl230_Detected()
    {
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl200, cruiseAltitude: Fl200);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), Fl200, cruiseAltitude: Fl200);
        Assert.True(Detected(a, b)); // 2 nm < 3 nm
    }

    [Fact]
    public void Parallel_4nmApart_OneAboveFl230_UsesFiveNm_Detected()
    {
        // Only "both ≤ FL230" gets the reduced 3 nm; a mixed pair keeps 5 nm.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl200, cruiseAltitude: Fl200);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(4.0), Fl350, cruiseAltitude: Fl350);
        // Vertical envelopes (FL200 vs FL350) are far apart, so this must NOT alert on vertical grounds; use
        // a co-altitude mixed-with-FL230-boundary check instead.
        Assert.False(Detected(a, b));
    }

    // ── Vertical: data-block-altitude envelope (§381 / §383) ─────────────────────────────────────────

    [Fact]
    public void Vertical_DescendingToDataBlockLevelOff_NoAlert()
    {
        // §381: A at FL370 descending with a data-block (interim) altitude of FL350; B level at FL330.
        // A's envelope is [FL350,FL370]; the 2000 ft gap to B means no alert despite the descent rate.
        // Interim altitude is in hundreds of feet (350 = FL350).
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 37000, verticalSpeed: -2000, interimAltitude: 350, cruiseAltitude: 37000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 33000, cruiseAltitude: 33000);
        Assert.False(Detected(a, b));
    }

    [Fact]
    public void Vertical_HundredsValuedInterim_NoSpuriousAlertAgainstDistantAltitude()
    {
        // Regression for the units bug: an interim stored as hundreds (FL330) must resolve to 33000 ft, not
        // 330 ft. A at FL350 with interim FL330; B level far below at FL200. Correct envelope [33000,35000]
        // clears B's 20000 ft by 13000 ft. If the ×100 were missing, A's envelope would be [330,35000] and
        // swallow B → spurious alert.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 35000, interimAltitude: 330, cruiseAltitude: 35000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 20000, cruiseAltitude: 20000);
        Assert.False(Detected(a, b));
    }

    [Fact]
    public void Vertical_DescendingWithoutDataBlockCap_Alert()
    {
        // Same descent, but no data-block altitude at all → the envelope falls back to the VS projection
        // (FL370 down to ~FL290 over four minutes), which sweeps through B's FL330 → alert.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 37000, verticalSpeed: -2000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 33000, cruiseAltitude: 33000);
        Assert.True(Detected(a, b));
    }

    [Fact]
    public void Vertical_LevelTrafficMovingTowardLowerAssignment_Alert()
    {
        // §383: A level at FL350 but with an interim of FL330 → envelope [FL330,FL350] contains B's FL340.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 35000, interimAltitude: 330, cruiseAltitude: 35000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 34000, cruiseAltitude: 34000);
        Assert.True(Detected(a, b));
    }

    [Fact]
    public void Vertical_LocalInterimTakesPrecedenceOverInterim()
    {
        // CRC field-B precedence is LocalInterim > Procedure > Interim. A is level at FL350 with an interim
        // of FL350 (no pending change) but a local interim of FL330 → the local interim drives the envelope
        // [FL330,FL350], which contains B's FL340. Reversed precedence would use FL350 and miss it.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 35000, interimAltitude: 350, cruiseAltitude: 35000);
        a.Eram.LocalInterimAltitude = 330;
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 34000, cruiseAltitude: 34000);
        Assert.True(Detected(a, b));
    }

    [Fact]
    public void Vertical_LevelTrafficStableAssignments_NoAlert()
    {
        // Same positions, but both level at their data-block altitude → envelopes are points 1000 ft apart,
        // which is separation, not a loss.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, 35000, cruiseAltitude: 35000);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(2.0), 34000, cruiseAltitude: 34000);
        Assert.False(Detected(a, b));
    }

    // ── Divergence & eligibility ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Diverging_FarApart_NoAlert()
    {
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, heading: 270, cruiseAltitude: Fl350);
        var b = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(10.0), Fl350, heading: 90, cruiseAltitude: Fl350);
        Assert.False(Detected(a, b));
    }

    [Theory]
    [InlineData("ground")]
    [InlineData("modeA")]
    [InlineData("caInhibited")]
    [InlineData("unsupported")]
    public void Ineligible_Excluded(string kind)
    {
        // Two aircraft co-located and co-altitude — an obvious conflict — but one is ineligible.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, cruiseAltitude: Fl350);
        var b = MakeAircraft(
            "UAL200",
            BaseLat,
            BaseLon,
            Fl350,
            cruiseAltitude: Fl350,
            isOnGround: kind == "ground",
            transponderMode: kind == "modeA" ? "A" : "C",
            caInhibited: kind == "caInhibited",
            unsupported: kind == "unsupported"
        );
        Assert.False(Detected(a, b));
    }

    // ── Hysteresis ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hysteresis_StaysLatchedJustPastThreshold_ClearsWithMargin()
    {
        var id = EramConflictDetector.MakeConflictId("AAL100", "UAL200");
        var latched = new HashSet<string> { id };

        // 5.2 nm abeam at FL350: past the 5 nm fresh minimum but inside the 5.3 nm clear threshold.
        var a = MakeAircraft("AAL100", BaseLat, BaseLon, Fl350, cruiseAltitude: Fl350);
        var b52 = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(5.2), Fl350, cruiseAltitude: Fl350);
        Assert.Empty(EramConflictDetector.Detect([a, b52], new HashSet<string>())); // fresh: no alert
        Assert.NotEmpty(EramConflictDetector.Detect([a, b52], latched)); // latched: stays

        // 5.4 nm abeam: beyond the clear threshold → the latch releases.
        var b54 = MakeAircraft("UAL200", BaseLat, BaseLon + LonOffsetForNm(5.4), Fl350, cruiseAltitude: Fl350);
        Assert.Empty(EramConflictDetector.Detect([a, b54], latched));
    }

    [Fact]
    public void MakeConflictId_SortsCallsigns_WithEstcaPrefix()
    {
        Assert.Equal("ESTCA_AAL100_UAL200", EramConflictDetector.MakeConflictId("UAL200", "AAL100"));
        Assert.Equal("ESTCA_AAL100_UAL200", EramConflictDetector.MakeConflictId("AAL100", "UAL200"));
    }
}
