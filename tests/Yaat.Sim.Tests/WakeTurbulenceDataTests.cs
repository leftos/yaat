using Xunit;

namespace Yaat.Sim.Tests;

public class WakeTurbulenceDataTests
{
    public WakeTurbulenceDataTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("A388", "A")]
    [InlineData("B77W", "B")]
    [InlineData("B763", "C")]
    [InlineData("B738", "F")]
    [InlineData("E170", "G")]
    [InlineData("C172", "I")]
    public void GetCwt_KnownTypes_ReturnsCorrectCode(string type, string expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.GetCwt(type));
    }

    [Fact]
    public void GetCwt_UnknownType_ReturnsNull()
    {
        Assert.Null(WakeTurbulenceData.GetCwt("ZZZZ"));
    }

    [Theory]
    // CWT category -> coarse weight class. D is a HEAVY widebody bucket (B744/A339/IL76), not Large.
    [InlineData("A388", WakeTurbulenceData.WakeClass.Super)] // A
    [InlineData("B77W", WakeTurbulenceData.WakeClass.Heavy)] // B
    [InlineData("B763", WakeTurbulenceData.WakeClass.Heavy)] // C
    [InlineData("B744", WakeTurbulenceData.WakeClass.Heavy)] // D
    [InlineData("B752", WakeTurbulenceData.WakeClass.Large)] // E (B757)
    [InlineData("B738", WakeTurbulenceData.WakeClass.Large)] // F
    [InlineData("CRJ7", WakeTurbulenceData.WakeClass.Large)] // G
    [InlineData("C172", WakeTurbulenceData.WakeClass.Small)] // I
    public void WakeClassForType_MapsCwtToWeightClass(string type, WakeTurbulenceData.WakeClass expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.WakeClassForType(type, AircraftCategorization.Categorize(type)));
    }

    [Fact]
    public void GetCwt_TypeWithEquipmentSuffix_StripsSlash()
    {
        Assert.Equal(WakeTurbulenceData.GetCwt("B738"), WakeTurbulenceData.GetCwt("B738/L"));
    }

    [Fact]
    public void GetCwt_CaseInsensitive()
    {
        Assert.Equal(WakeTurbulenceData.GetCwt("B738"), WakeTurbulenceData.GetCwt("b738"));
    }

    [Theory]
    // Common airline types go through the FAA ACD physical-dimension formula
    // (~12 arcmin detection threshold, clamp [1.5, 10] nm). Ranges derived from
    // actual wingspan/length/tail data in FaaAcd.json.
    [InlineData("A388", 10.0)] // Super: silhouette ~332 ft, clamped
    [InlineData("B77W", 10.0)] // Heavy widebody: clamped
    [InlineData("B763", 10.0)] // Heavy widebody: ~10.3 nm, clamped
    [InlineData("B738", 7.6)] // Narrowbody jet
    [InlineData("C172", 2.0)] // Small GA
    public void TrafficDetectionRangeNm_PhysicalDimensions(string type, double expected)
    {
        var actual = WakeTurbulenceData.TrafficDetectionRangeNm(type, AircraftCategory.Jet);
        Assert.InRange(actual, expected - 0.5, expected + 0.5);
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 7.6)]
    [InlineData(AircraftCategory.Turboprop, 4.8)]
    [InlineData(AircraftCategory.Piston, 2.0)]
    [InlineData(AircraftCategory.Helicopter, 2.0)]
    public void TrafficDetectionRangeNm_FallbackByCategory(AircraftCategory cat, double expected)
    {
        // "ZZZZ" has no FAA ACD record and no CWT entry, so falls all the way
        // through to the category fallback.
        Assert.Equal(expected, WakeTurbulenceData.TrafficDetectionRangeNm("ZZZZ", cat));
    }

    [Theory]
    // FAA CWT mile-based on-approach minima, 7110.65 TBL 5-5-2. (leader CWT -> follower CWT)
    [InlineData("A388", "B77W", 5.0)] // A -> B  (super, upper-heavy follower)
    [InlineData("A388", "C172", 8.0)] // A -> I  (super, small follower)
    [InlineData("B744", "C172", 6.0)] // B -> I
    [InlineData("B77W", "B738", 5.0)] // B -> F
    [InlineData("B763", "B738", 3.5)] // C -> F  (lower heavy ahead of a narrowbody)
    [InlineData("B763", "CRJ7", 3.5)] // C -> G  (regional jet follower)
    [InlineData("B752", "C172", 4.0)] // E -> I  (B757 special category)
    [InlineData("B738", "A320", 0.0)] // F -> F  (no wake requirement)
    [InlineData("CRJ7", "C172", 0.0)] // G -> I  (regional leader imposes no wake minimum)
    public void OnApproachWakeSeparation_UsesCwtMatrix(string lead, string follow, double expected)
    {
        var nm = WakeTurbulenceData.OnApproachWakeSeparationNm(
            lead,
            AircraftCategorization.Categorize(lead),
            follow,
            AircraftCategorization.Categorize(follow)
        );
        Assert.Equal(expected, nm, precision: 1);
    }

    [Fact]
    public void OnApproachWakeSeparation_CwtIsTighterThanCoarse_ForLowerHeavyAheadOfNarrowbody()
    {
        // B763 (CWT C) ahead of B738 (CWT F): the precise CWT minimum is 3.5 NM, where the coarse
        // Heavy->Large bucket would demand 5 NM. CWT closes the stream up.
        var cwt = WakeTurbulenceData.OnApproachWakeSeparationNm(
            "B763",
            AircraftCategorization.Categorize("B763"),
            "B738",
            AircraftCategorization.Categorize("B738")
        );
        var coarse = WakeTurbulenceData.OnApproachWakeSeparationNm(WakeTurbulenceData.WakeClass.Heavy, WakeTurbulenceData.WakeClass.Large);

        Assert.Equal(3.5, cwt, precision: 1);
        Assert.Equal(5.0, coarse, precision: 1);
        Assert.True(cwt < coarse);
    }

    [Fact]
    public void OnApproachWakeSeparation_UnknownType_FallsBackToCoarse()
    {
        // "ZZZZ" has no CWT, so the pair drops to the coarse weight-class minima: an unknown jet maps to
        // the Large class, which imposes no wake minimum on a following small aircraft.
        var nm = WakeTurbulenceData.OnApproachWakeSeparationNm("ZZZZ", AircraftCategory.Jet, "C172", AircraftCategorization.Categorize("C172"));
        Assert.Equal(0.0, nm, precision: 1);
    }

    [Fact]
    public void OnApproachWakeSeparation_CwtDHeavyLeader_KeepsHeavyWakeFloor_WhenFollowerHasNoCwt()
    {
        // B744 is CWT D = HEAVY. When the follower has no CWT code the pair drops to the coarse weight
        // class, which must still treat the D leader as Heavy (not Large) so a heavy widebody ahead of an
        // unknown jet keeps the Heavy->Large 5 NM wake floor instead of collapsing to no requirement.
        var nm = WakeTurbulenceData.OnApproachWakeSeparationNm("B744", AircraftCategorization.Categorize("B744"), "ZZZZ", AircraftCategory.Jet);
        Assert.Equal(5.0, nm, precision: 1);
    }

    [Theory]
    // Coarse weight-class fallback (no CWT available): pure wake requirement, 0 when none.
    [InlineData(WakeTurbulenceData.WakeClass.Heavy, WakeTurbulenceData.WakeClass.Small, 6.0)]
    [InlineData(WakeTurbulenceData.WakeClass.Heavy, WakeTurbulenceData.WakeClass.Large, 5.0)]
    [InlineData(WakeTurbulenceData.WakeClass.Super, WakeTurbulenceData.WakeClass.Small, 8.0)]
    [InlineData(WakeTurbulenceData.WakeClass.Large, WakeTurbulenceData.WakeClass.Small, 0.0)]
    [InlineData(WakeTurbulenceData.WakeClass.Small, WakeTurbulenceData.WakeClass.Small, 0.0)]
    public void OnApproachWakeSeparation_CoarseClass(WakeTurbulenceData.WakeClass lead, WakeTurbulenceData.WakeClass follow, double expected)
    {
        Assert.Equal(expected, WakeTurbulenceData.OnApproachWakeSeparationNm(lead, follow), precision: 1);
    }
}
