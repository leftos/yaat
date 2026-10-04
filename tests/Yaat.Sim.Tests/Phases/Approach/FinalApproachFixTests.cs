using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests.Phases.Approach;

/// <summary>
/// <see cref="FinalApproachFix.IsInside"/>: the inside-the-final-approach-fix test the dispatcher's cross-runway
/// FOLLOW refusal reads, extracted into a shared helper. An aircraft is inside the final approach fix when it is
/// established on the final — out on the approach side of the landing threshold (<see cref="AirborneFollowHelper.AlongFinalNm"/>
/// at or beyond zero) and within <see cref="AirborneFollowHelper.OnFinalMaxCrossTrackNm"/> of the extended
/// centerline — and no further out than <see cref="ApproachGateDatabase.InsideFafLimitNm"/> (7110.65 §5-7-1.b.4).
///
/// The test process never loads real CIFP into <see cref="ApproachGateDatabase"/> — only
/// <c>ApproachGateDatabaseTests</c> initializes it, with synthetic fields — so no real runway resolves a published
/// FAF and every limit below is the 5 NM ceiling rather than a published distance.
/// </summary>
public class FinalApproachFixTests
{
    /// <summary>How far past the limit the outside cases sit.</summary>
    private const double LimitOffsetNm = 1.0;

    public FinalApproachFixTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>KOAK 28R: a real runway from navdata, the one the cross-runway FOLLOW tests use.</summary>
    private static RunwayInfo Runway =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    /// <summary><paramref name="runway"/>'s inside-the-FAF limit: its published FAF distance bounded by the 5 NM ceiling.</summary>
    private static double LimitNm(RunwayInfo runway) =>
        ApproachGateDatabase.InsideFafLimitNm(ApproachGateDatabase.GetFafDistanceNm(runway.AirportId, runway.Designator, thresholdDisplacementNm: 0));

    /// <summary>A point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the extended centerline.</summary>
    private static LatLon OffFinal(RunwayInfo runway, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    /// <summary>A point <paramref name="pastNm"/> past the landing threshold, along the landing direction.</summary>
    private static LatLon PastThreshold(RunwayInfo runway, double pastNm) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, pastNm);

    private static AircraftState AircraftAt(LatLon position, RunwayInfo runway) =>
        new()
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = 900,
            IndicatedAirspeed = 90,
            IsOnGround = false,
        };

    [Fact]
    public void IsInside_OnFinalInsideTheLimit_IsTrue()
    {
        RunwayInfo runway = Runway;

        Assert.True(FinalApproachFix.IsInside(AircraftAt(OffFinal(runway, 2.0, 0), runway), runway, layout: null));
    }

    [Fact]
    public void IsInside_OnFinalBeyondTheLimit_IsFalse()
    {
        RunwayInfo runway = Runway;
        double beyondNm = LimitNm(runway) + LimitOffsetNm;

        Assert.False(FinalApproachFix.IsInside(AircraftAt(OffFinal(runway, beyondNm, 0), runway), runway, layout: null));
    }

    [Fact]
    public void IsInside_PastTheThreshold_IsFalse()
    {
        RunwayInfo runway = Runway;

        Assert.False(FinalApproachFix.IsInside(AircraftAt(PastThreshold(runway, 0.5), runway), runway, layout: null));
    }

    [Fact]
    public void IsInside_OffTheCenterline_IsFalse()
    {
        RunwayInfo runway = Runway;
        double offCenterlineNm = AirborneFollowHelper.OnFinalMaxCrossTrackNm + 0.5;

        Assert.False(FinalApproachFix.IsInside(AircraftAt(OffFinal(runway, 2.0, offCenterlineNm), runway), runway, layout: null));
    }

    [Fact]
    public void IsInside_NoPublishedFaf_UsesTheFiveMileCeiling()
    {
        RunwayInfo runway = Runway;

        // Precondition: this runway has no published FAF in the loaded procedures, so the limit is the ceiling.
        Assert.Null(ApproachGateDatabase.GetFafDistanceNm(runway.AirportId, runway.Designator, thresholdDisplacementNm: 0));

        Assert.True(FinalApproachFix.IsInside(AircraftAt(OffFinal(runway, 4.0, 0), runway), runway, layout: null));
        Assert.False(FinalApproachFix.IsInside(AircraftAt(OffFinal(runway, 6.0, 0), runway), runway, layout: null));
    }
}
