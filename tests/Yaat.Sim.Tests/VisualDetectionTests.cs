using Xunit;

namespace Yaat.Sim.Tests;

public class VisualDetectionTests
{
    // KOAK: 37.721, -122.221, elevation 9ft, Runway 28R heading ~284°
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;
    private const double AptElev = 9.0;

    // Test-only conspicuity cap for the airport-acquisition tests below.
    // 25 nm is the large-hub ceiling from VisualAcquisition.AirportSizeCapNm,
    // chosen here so range assertions are not gated by airport size.
    private const double LargeCap = 25.0;

    public VisualDetectionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static IReadOnlyList<MetarParser.CloudLayer> Bkn(int agl) => [new MetarParser.CloudLayer(MetarParser.CloudCover.Broken, agl)];

    // -------------------------------------------------------------------------
    // CanSeeAirport — basic cases
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeAirport_InFront_WithinRange_BelowCeiling_True()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, Bkn(5000), 10.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_Behind_False()
    {
        // Aircraft heading north, airport to the south → behind
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        Assert.False(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, Bkn(5000), 10.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_BeyondVisibility_False()
    {
        // 1SM visibility ≈ 0.869nm, airport ~2nm away
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, Bkn(5000), 1.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_AboveCeiling_False()
    {
        // Ceiling 2000 AGL + 9ft elevation = 2009 MSL, aircraft at 3000 MSL
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, Bkn(2000), 10.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_NoCeiling_StillChecksRangeAndBearing()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 10000);
        Assert.True(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_AboveFL180_False()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        Assert.False(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_NoVisibility_UsesHorizonAndSizeCap()
    {
        // Aircraft 5nm away at 3000 ft AGL, no METAR. Horizon = 0.5 * 1.23 * sqrt(2991) ≈ 33.6 nm,
        // capped by LargeCap = 25 nm. 5 < 25 → acquire.
        var ac = MakeAircraft(37.80, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, null, 0.0, LargeCap).Acquired);
    }

    [Fact]
    public void CanSeeAirport_LongRangeAtJetAltitude_True()
    {
        // The user's empirical case: B738 at 5000 ft on a vectored downwind/base
        // ~18 nm out can absolutely see KOAK on a CAVOK day. Horizon at 4991 ft AGL
        // = 0.5 * 1.23 * sqrt(4991) ≈ 43.4 nm; large-airport cap = 25 nm; → 18 < 25.
        // Pre-multi-factor model (12 nm hard cap) failed this case.
        var ac = MakeAircraft(38.022, -122.221, heading: 180, altitude: 5000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, null, 0.0, LargeCap);
        Assert.True(result.Acquired, $"Expected acquired at ~18 nm / 5000 ft, got {result.Reason} (range={result.MaxRangeNm:F1} nm)");
    }

    [Fact]
    public void CanSeeAirport_LowAltitudeHorizonLimits_False()
    {
        // 100 ft AGL → horizon = 0.5 * 1.23 * sqrt(91) ≈ 5.9 nm. Aircraft 12 nm
        // out cannot see the field at this altitude even on a clear day.
        var ac = MakeAircraft(37.521, -122.221, heading: 0, altitude: 100);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, null, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.OutOfRange, result.Reason);
    }

    [Fact]
    public void CanSeeAirport_PastAbeam_Within120_True()
    {
        // Downwind-past-abeam case: aircraft due north of the field, heading 070,
        // so the field bears 180 — 110° off the nose. A multi-acre airport is
        // visible out the side window (AIM §8-1-6.c.2 ±100° arc + head turn), so
        // the airport hemisphere is ±120°, wider than the ±90° traffic gate.
        var ac = MakeAircraft(37.77, -122.221, heading: 70, altitude: 3000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap);
        Assert.True(result.Acquired, $"Expected acquired with field 110° off the nose, got {result.Reason}");
    }

    [Fact]
    public void CanSeeAirport_Beyond120_False()
    {
        // Field bears 180, heading 045 → 135° off the nose: aft of the wing line,
        // outside even the widened airport hemisphere.
        var ac = MakeAircraft(37.77, -122.221, heading: 45, altitude: 3000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.BehindOwnship, result.Reason);
    }

    [Fact]
    public void CanSeeTraffic_At105Degrees_AcquiresOverShoulder()
    {
        // Traffic bears 180, heading 075 → 105° off the nose: aft of abeam but inside
        // the ±110° alerted-search arc. The AIM's own recommended scan starts "over
        // the left shoulder" (§8-1-6.c.3, §8-1-8.j.1) and RTIS hands the pilot a clock
        // position, so a shallow over-the-shoulder look is normal technique.
        var own = MakeAircraft(37.75, -122.221, heading: 75, altitude: 3000);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 3000);
        var result = VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 10.0, 0.0);
        Assert.True(result.Acquired, $"Expected acquired with traffic 105° off the nose, got {result.Reason}");
    }

    [Fact]
    public void CanSeeTraffic_At120Degrees_BehindOwnship()
    {
        // Traffic bears 180, heading 060 → 120° off the nose: past the head+eye yaw
        // limit for a foveated point target (±110°), even though the airport's wider
        // field-of-view gate would still accept this bearing.
        var own = MakeAircraft(37.75, -122.221, heading: 60, altitude: 3000);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 3000);
        var result = VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 10.0, 0.0);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.BehindOwnship, result.Reason);
    }

    [Fact]
    public void CanSeeAirport_TenSmVisibility_DoesNotCapRange()
    {
        // US METARs report at most "10SM", which means 10 statute miles OR MORE —
        // a reporting ceiling, not a measurement. A clear-day 10SM METAR must not
        // cap acquisition at 8.69 nm; the horizon and airport-size caps govern.
        // Same geometry as CanSeeAirport_LongRangeAtJetAltitude_True (~18 nm out
        // at 5000 ft), which acquires with no METAR at all.
        var ac = MakeAircraft(38.022, -122.221, heading: 180, altitude: 5000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap);
        Assert.True(result.Acquired, $"Expected acquired at ~18 nm with a 10SM METAR, got {result.Reason} (range={result.MaxRangeNm:F1} nm)");
    }

    [Fact]
    public void CanSeeAirport_NineSmVisibility_StillCapsRange()
    {
        // Below the 10SM reporting ceiling the value is a real measurement and must
        // bind — but as a slant-range envelope, not the literal surface figure: at
        // 5000 ft (4991 AGL) the 9 SM report gives 9 × 0.869 × 1.5 × (4991/3000)
        // ≈ 19.5 nm. An aircraft 21 nm out is beyond it → OutOfRange.
        var ac = MakeAircraft(38.071, -122.221, heading: 180, altitude: 5000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 9.0, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.OutOfRange, result.Reason);
    }

    // -------------------------------------------------------------------------
    // Airport visibility envelope — slant-range model (issue #345)
    //
    // Surface METAR visibility is a surface-horizontal point statistic (AIM
    // §7-1-15); flight visibility differs (7110.65 §7-5-7/§7-5-8 NOTE). The
    // airport cap is vis × 0.869 × 1.5 × max(1, AGL/3000ft): the 1.5 credit
    // absorbs prevailing-visibility pessimism (half-horizon rule, lower-of-two
    // observations), and the height ratio is the Koschmieder slab — above the
    // surface obscuration the line of sight crosses less of it. Traffic (point
    // targets) keeps the strict literal cap.
    // -------------------------------------------------------------------------

    [Fact]
    public void Airport1000And3_ShortFinal_AcquiresAndNeverLoses()
    {
        // The legally-clearable 1000/3 visual (7110.65 §7-4-3.b, AIM §5-5-11.b.1):
        // a jet at 900 AGL, 3.8 nm out, under a 3SM report must be able to report
        // the field (envelope 3 × 0.869 × 1.5 = 3.91 nm), and once acquired must
        // hold it all the way in (maintain = envelope × 1.25).
        var ac = MakeAircraft(37.721 + (3.8 / 60.0), -122.221, heading: 180, altitude: AptElev + 900);
        var acquire = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 3.0, 0.0, LargeCap);
        Assert.True(acquire.Acquired, $"1000/3 short final must acquire, got {acquire.Reason} (range={acquire.MaxRangeNm:F2} nm)");

        foreach (double distNm in new[] { 3.8, 3.0, 2.0, 1.0 })
        {
            var maintain = VisualDetection.TryMaintainAirportContact(ac, AptElev, null, 3.0, distNm);
            Assert.True(maintain.Acquired, $"field must stay in sight at {distNm} nm under 3SM");
        }
    }

    [Fact]
    public void Airport3Sm_PatternAltitudeAbeam_Acquires()
    {
        // 3SM under a BKN040 deck, 2000 AGL on a downwind abeam ~1.6 nm from the
        // ARP: fully immersed in the obscuration (ratio floors at 1) the envelope
        // is a flat 3.91 nm — the field right off the wing is inside it.
        var ac = MakeAircraft(37.721 + (1.6 / 60.0), -122.221, heading: 104, altitude: AptElev + 2000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, Bkn(4000), 3.0, 0.0, LargeCap);
        Assert.True(result.Acquired, $"pattern-altitude abeam under 3SM must acquire, got {result.Reason} (range={result.MaxRangeNm:F2} nm)");
    }

    [Fact]
    public void Airport3Sm_DescentFromAbove_AcquiresAndHoldsThroughDescent()
    {
        // Above the obscuration the envelope grows with height: at 9000 ft AGL a
        // 3SM report gives 3.91 × (9000/3000) ≈ 11.7 nm. A jet descending on a
        // normal 400 ft/nm gradient stays inside the shrinking envelope the whole
        // way down (descent-stability: gradient ≤ H/V_eff ≈ 767 ft/nm at 3SM).
        var acquireAc = MakeAircraft(37.721 + (11.0 / 60.0), -122.221, heading: 180, altitude: AptElev + 9000);
        var acquire = VisualDetection.TryAcquireAirport(acquireAc, AptLat, AptLon, AptElev, null, 3.0, 0.0, LargeCap);
        Assert.True(acquire.Acquired, $"3SM from 9000 AGL at 11 nm must acquire, got {acquire.Reason} (range={acquire.MaxRangeNm:F2} nm)");

        foreach (var (distNm, agl) in new[] { (9.0, 8200.0), (6.0, 7000.0), (3.0, 5800.0), (1.0, 5000.0) })
        {
            var ac = MakeAircraft(37.721 + (distNm / 60.0), -122.221, heading: 180, altitude: AptElev + agl);
            var maintain = VisualDetection.TryMaintainAirportContact(ac, AptElev, null, 3.0, distNm);
            Assert.True(maintain.Acquired, $"contact must hold at {distNm} nm / {agl} ft AGL on a 400 ft/nm descent");
        }
    }

    [Fact]
    public void Airport_NineVsTenSm_CliffSoftened()
    {
        // 9SM at 2000 AGL: envelope 9 × 0.869 × 1.5 ≈ 11.7 nm — a real bound, but
        // no longer a 7.8-vs-uncapped cliff against the censored 10SM report.
        var ac = MakeAircraft(37.721 + (5.0 / 60.0), -122.221, heading: 180, altitude: AptElev + 2000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 9.0, 0.0, LargeCap);
        Assert.True(result.Acquired);
        Assert.InRange(result.MaxRangeNm, 11.5, 12.0);
    }

    /// <summary>
    /// The no-flap invariant: because acquire and maintain share one envelope function
    /// (maintain adds the ×1.25 tracking credit on top), a just-acquired field can never
    /// be immediately lost by the maintain check — at any visibility and any altitude.
    /// </summary>
    [Theory]
    [InlineData(1.0, 500.0)]
    [InlineData(3.0, 500.0)]
    [InlineData(3.0, 2000.0)]
    [InlineData(3.0, 9000.0)]
    [InlineData(6.0, 5000.0)]
    [InlineData(9.0, 2000.0)]
    [InlineData(9.0, 9000.0)]
    public void Airport_AcquireRangeAlwaysInsideMaintainEnvelope(double visSm, double agl)
    {
        var ac = MakeAircraft(37.721 + (1.0 / 60.0), -122.221, heading: 180, altitude: AptElev + agl);
        var acquire = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, visSm, 0.0, LargeCap);
        Assert.True(acquire.Acquired);

        var maintainAtCap = VisualDetection.TryMaintainAirportContact(ac, AptElev, null, visSm, acquire.MaxRangeNm);
        Assert.True(
            maintainAtCap.Acquired,
            $"vis={visSm}SM agl={agl}: a field acquired at the cap ({acquire.MaxRangeNm:F2} nm) must not be immediately lost by maintain"
        );
    }

    [Fact]
    public void CanSeeTraffic_TenSmVisibility_DoesNotCapRange()
    {
        // B77W target ~9 nm out is within its 10 nm size-based detection range;
        // a clear-day 10SM METAR (8.69 nm literal) must not veto the sighting.
        var own = MakeAircraft(37.87, -122.221, heading: 180, altitude: 10000);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 10000);
        tgt.AircraftType = "B77W";
        var result = VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 10.0, 0.0);
        Assert.True(result.Acquired, $"Expected acquired at ~9 nm with a 10SM METAR, got {result.Reason} (range={result.MaxRangeNm:F1} nm)");
    }

    [Fact]
    public void CanSeeTraffic_FiveSmVisibility_StillCapsRange()
    {
        // 5 SM ≈ 4.3 nm binds below the B77W's 10 nm size range → OutOfRange at ~9 nm.
        var own = MakeAircraft(37.87, -122.221, heading: 180, altitude: 10000);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 10000);
        tgt.AircraftType = "B77W";
        var result = VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 5.0, 0.0);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.OutOfRange, result.Reason);
    }

    [Fact]
    public void CanSeeAirport_BeyondSizeCap_False()
    {
        // 27 nm out at 8000 ft. Horizon ≈ 0.5 * 1.23 * sqrt(7991) ≈ 55 nm — not the limiter.
        // Large cap = 25 nm — IS the limiter. 27 > 25 → fail.
        var ac = MakeAircraft(38.171, -122.221, heading: 180, altitude: 8000);
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, null, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.OutOfRange, result.Reason);
    }

    // -------------------------------------------------------------------------
    // AirportSizeCapNm — polygon-extent classifier
    // -------------------------------------------------------------------------

    [Fact]
    public void AirportSizeCap_KOAK_LargeFieldNearCeiling()
    {
        // KOAK has 3 runways spread over ~2 nm of ground. Should land near the
        // upper end of the [15, 25] nm scale.
        double cap = VisualAcquisition.AirportSizeCapNm("OAK");
        Assert.InRange(cap, 18.0, 25.0);
    }

    [Fact]
    public void AirportSizeCap_UnknownAirport_ReturnsFloor()
    {
        // No runway data → conservative floor (small-field cap).
        double cap = VisualAcquisition.AirportSizeCapNm("ZZZZ");
        Assert.Equal(15.0, cap);
    }

    // -------------------------------------------------------------------------
    // CanSeeAirportForRunway — approach side check
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeForRunway_OnApproachSide_True()
    {
        // Runway heading 284° → approach from ~104° (east side)
        // Aircraft to the east of airport, heading west toward airport
        var ac = MakeAircraft(37.721, -122.15, heading: 270, altitude: 3000);
        Assert.True(
            VisualDetection.TryAcquireAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, new TrueHeading(284.0), 0.0, LargeCap).Acquired
        );
    }

    [Fact]
    public void CanSeeForRunway_OnDepartureSide_False()
    {
        // Aircraft to the west of airport (departure end for Rwy 28R), looking east at airport
        var ac = MakeAircraft(37.721, -122.30, heading: 90, altitude: 3000);
        Assert.False(
            VisualDetection.TryAcquireAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, new TrueHeading(284.0), 0.0, LargeCap).Acquired
        );
    }

    [Fact]
    public void CanSeeForRunway_OnDownwind_True()
    {
        // Aircraft slightly south of airport, on a left downwind for 28R
        // Heading north-ish (350°) so airport is in forward hemisphere
        // bearing from airport to aircraft is roughly south (~180°), approach side reciprocal is 104°
        // 180-104 = 76° < 120° → should pass approach-side check
        var ac = MakeAircraft(37.69, -122.221, heading: 350, altitude: 3000);
        Assert.True(
            VisualDetection.TryAcquireAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, new TrueHeading(284.0), 0.0, LargeCap).Acquired
        );
    }

    // -------------------------------------------------------------------------
    // CanSeeTraffic
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeTraffic_InFront_WithinRange_True()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, Bkn(5000), AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_Behind_False()
    {
        var own = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.TryAcquireTraffic(own, tgt, Bkn(5000), AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_OppositeSidesOfCeiling_False()
    {
        // Ceiling at 3000 AGL + 9 = 3009 MSL. Own at 2500 (below), target at 4000 (above)
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 2500);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 4000);
        Assert.False(VisualDetection.TryAcquireTraffic(own, tgt, Bkn(3000), AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_BeyondVisibility_False()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        // 0.5SM = 0.43nm, targets ~1.3nm apart
        Assert.False(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 0.5, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_AboveFL180_True()
    {
        // Pilots can see traffic in Class A — only visual separation is prohibited (7110.65 §7-1-1)
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 18000);
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeAirport_AboveFL180_StillFalse()
    {
        // Visual approaches still prohibited in Class A
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        Assert.False(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, null, 10.0, 0.0, LargeCap).Acquired);
    }

    // -------------------------------------------------------------------------
    // Multi-layer cloud obstruction
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeTraffic_ObstructingLayerBetween_False()
    {
        // Ownship 5000 MSL, target 8000 MSL, BKN at 6000 AGL (≈6009 MSL) → layer lies strictly between → fail MixedCeiling
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 8000);
        IReadOnlyList<MetarParser.CloudLayer> layers = [new(MetarParser.CloudCover.Broken, 6000)];
        var result = VisualDetection.TryAcquireTraffic(own, tgt, layers, AptElev, 10.0, 0.0);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.MixedCeiling, result.Reason);
        Assert.NotNull(result.BindingLayer);
        Assert.Equal(MetarParser.CloudCover.Broken, result.BindingLayer.Cover);
        Assert.Equal(6000, result.BindingLayer.BaseFeetAgl);
    }

    [Fact]
    public void CanSeeTraffic_ScatteredLayerBetween_True()
    {
        // Same altitudes but SCT instead of BKN — scattered has gaps and should not obstruct
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 8000);
        IReadOnlyList<MetarParser.CloudLayer> layers = [new(MetarParser.CloudCover.Scattered, 6000)];
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, layers, AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_BothBelowMultipleLayers_True()
    {
        // Both aircraft below SCT020 / BKN070 / OVC200 — all layers above both → visible
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 1500);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 1800);
        IReadOnlyList<MetarParser.CloudLayer> layers =
        [
            new(MetarParser.CloudCover.Scattered, 2000),
            new(MetarParser.CloudCover.Broken, 7000),
            new(MetarParser.CloudCover.Overcast, 20000),
        ];
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, layers, AptElev, 10.0, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_MixedAcrossHigherLayer_IgnoresLowerScattered()
    {
        // Ownship 5000, target 22000, layers SCT020 BKN070 OVC200. The BKN070
        // (7000 AGL → ~7009 MSL) is strictly between them → fail, binding = BKN070.
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 22000);
        IReadOnlyList<MetarParser.CloudLayer> layers =
        [
            new(MetarParser.CloudCover.Scattered, 2000),
            new(MetarParser.CloudCover.Broken, 7000),
            new(MetarParser.CloudCover.Overcast, 20000),
        ];
        var result = VisualDetection.TryAcquireTraffic(own, tgt, layers, AptElev, 10.0, 0.0);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.MixedCeiling, result.Reason);
        Assert.NotNull(result.BindingLayer);
        Assert.Equal(7000, result.BindingLayer.BaseFeetAgl);
    }

    [Fact]
    public void CanSeeAirport_BetweenTwoBknLayers_False()
    {
        // Aircraft at 10,000 MSL with BKN050 + OVC200 → above BKN050, binding = BKN050
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 10000);
        IReadOnlyList<MetarParser.CloudLayer> layers = [new(MetarParser.CloudCover.Broken, 5000), new(MetarParser.CloudCover.Overcast, 20000)];
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, layers, 10.0, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.AboveCeiling, result.Reason);
        Assert.NotNull(result.BindingLayer);
        Assert.Equal(5000, result.BindingLayer.BaseFeetAgl);
    }

    [Fact]
    public void CanSeeAirport_AboveHighOvc_WithLowerSctBelow_False()
    {
        // Regression: SCT020 (not a ceiling) + OVC150. Aircraft at 16,000 MSL is
        // below FL180 so InClassA doesn't fire, but it's above the OVC150 layer →
        // fail AboveCeiling with binding = OVC150. The scattered layer appears in
        // Layers but is correctly ignored.
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 16000);
        IReadOnlyList<MetarParser.CloudLayer> layers = [new(MetarParser.CloudCover.Scattered, 2000), new(MetarParser.CloudCover.Overcast, 15000)];
        var result = VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, layers, 10.0, 0.0, LargeCap);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.AboveCeiling, result.Reason);
        Assert.NotNull(result.BindingLayer);
        Assert.Equal(MetarParser.CloudCover.Overcast, result.BindingLayer.Cover);
        Assert.Equal(15000, result.BindingLayer.BaseFeetAgl);
    }

    [Fact]
    public void CanSeeAirport_BelowAllLayers_True()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 1500);
        IReadOnlyList<MetarParser.CloudLayer> layers = [new(MetarParser.CloudCover.Scattered, 2000), new(MetarParser.CloudCover.Broken, 7000)];
        Assert.True(VisualDetection.TryAcquireAirport(ac, AptLat, AptLon, AptElev, layers, 10.0, 0.0, LargeCap).Acquired);
    }

    // -------------------------------------------------------------------------
    // Bank angle occlusion
    // -------------------------------------------------------------------------

    [Fact]
    public void BankOcclusion_RightTurn_TargetLeftAndBelow_Occluded()
    {
        // Right bank +25°, target on left (high-wing side) at same altitude
        Assert.True(VisualDetection.IsOccludedByBank(25.0, new TrueHeading(360), new TrueHeading(315), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetLeftAndAbove_NotOccluded()
    {
        // Right bank +25°, target on left but well above (above 1000ft buffer)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, new TrueHeading(360), new TrueHeading(315), 3000, 4500));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetRightAndBelow_NotOccluded()
    {
        // Right bank +25°, target on right (low-wing side)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, new TrueHeading(360), new TrueHeading(45), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetAhead_NotOccluded()
    {
        // Right bank +25°, target ahead (within 10° nose cone)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, new TrueHeading(360), new TrueHeading(5), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_LeftTurn_TargetRightAndBelow_Occluded()
    {
        // Left bank -25°, target on right (high-wing side) at same altitude
        Assert.True(VisualDetection.IsOccludedByBank(-25.0, new TrueHeading(360), new TrueHeading(45), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ShallowBank_NotOccluded()
    {
        // Bank only 12° → below threshold
        Assert.False(VisualDetection.IsOccludedByBank(12.0, new TrueHeading(360), new TrueHeading(315), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ModerateBank_SameAltitude_Occluded()
    {
        // Bank 20° (moderate), target at same altitude (within 500ft buffer)
        Assert.True(VisualDetection.IsOccludedByBank(20.0, new TrueHeading(360), new TrueHeading(315), 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ModerateBank_Target600Above_NotOccluded()
    {
        // Bank 20° (moderate), target 600ft above → above 500ft buffer for moderate bank
        Assert.False(VisualDetection.IsOccludedByBank(20.0, new TrueHeading(360), new TrueHeading(315), 3000, 3600));
    }

    // -------------------------------------------------------------------------
    // Aircraft size (CWT-based range)
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeTraffic_SmallTarget_ShortRange()
    {
        // C172 (36ft ws, 27ft len, 9ft tail) → ~2.7 nm formula-derived range
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        // Target ~2nm south (within range)
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 3000);
        tgt.AircraftType = "C172";
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, null, 0.0).Acquired);

        // Target ~5nm south (beyond range)
        var tgtFar = MakeAircraft(37.67, -122.221, heading: 180, altitude: 3000);
        tgtFar.AircraftType = "C172";
        Assert.False(VisualDetection.TryAcquireTraffic(own, tgtFar, null, AptElev, null, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_MediumJet_MidRange()
    {
        // B738 (118ft ws, 129ft len, 41ft tail) → ~10.1 nm formula-derived range
        var own = MakeAircraft(37.82, -122.221, heading: 180, altitude: 5000);
        // Target ~6nm south (within 10.1 nm B738 range)
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 5000);
        tgt.AircraftType = "B738";
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, null, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_HeavyWidebody_LongRange()
    {
        // B77W → clamped to 12 nm max
        var own = MakeAircraft(37.87, -122.221, heading: 180, altitude: 10000);
        // Target ~9nm south (within 10 nm clamp)
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 10000);
        tgt.AircraftType = "B77W";
        Assert.True(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, null, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_DetectionRangeScalesWithAircraftSize()
    {
        // Formula sanity: a C172 target is unreachable at 5nm while a B738 target
        // at the same 5nm is easily within range. Proves the dimension-based
        // formula distinguishes sizes, not just the CWT bucket.
        var own = MakeAircraft(37.80, -122.221, heading: 180, altitude: 5000);
        var nearC172 = MakeAircraft(37.73, -122.221, heading: 180, altitude: 5000);
        nearC172.AircraftType = "C172";
        Assert.False(VisualDetection.TryAcquireTraffic(own, nearC172, null, AptElev, null, 0.0).Acquired);

        var nearB738 = MakeAircraft(37.73, -122.221, heading: 180, altitude: 5000);
        nearB738.AircraftType = "B738";
        Assert.True(VisualDetection.TryAcquireTraffic(own, nearB738, null, AptElev, null, 0.0).Acquired);
    }

    [Fact]
    public void CanSeeTraffic_UnknownType_FallsBackToCategory()
    {
        // Unknown type falls back to Jet category (10.1 nm range). Put target well
        // beyond that so the test still proves the fallback is bounded.
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        // Target ~15 nm south of ownship
        var tgt = MakeAircraft(37.50, -122.221, heading: 180, altitude: 3000);
        tgt.AircraftType = "ZZZZ";
        Assert.False(VisualDetection.TryAcquireTraffic(own, tgt, null, AptElev, null, 0.0).Acquired);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude)
    {
        return new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
        };
    }
}
