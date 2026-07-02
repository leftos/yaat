using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="ProcedureLegResolver.ExtractActiveDepartureLegs"/> selects the leading run of coded
/// legs (heading/intercept + course-to-fix) a departure procedure phase flies, stopping at the
/// first plain fix so interior fix-to-fix legs keep FlightPhysics' turn anticipation.
/// </summary>
[Collection("NavDbMutator")]
public class ProcedureLegResolverTests
{
    public ProcedureLegResolverTests()
    {
        // Resolving CD/VD/FD/CR/VR legs needs the recommended navaid's position from the nav DB.
        TestVnasData.EnsureInitialized();
    }

    private static CifpLeg DistanceLeg(CifpPathTerminator pt, string navaidOrFix, double course, double distanceNm, CifpAltitudeRestriction? alt) =>
        new(
            FixIdentifier: pt == CifpPathTerminator.FC ? navaidOrFix : "",
            PathTerminator: pt,
            TurnDirection: null,
            Altitude: alt,
            Speed: null,
            FixRole: CifpFixRole.None,
            Sequence: 10,
            OutboundCourse: course,
            LegDistanceNm: distanceNm,
            VerticalAngle: null,
            RecommendedNavaidId: pt == CifpPathTerminator.FC ? null : navaidOrFix
        );

    [Fact]
    public void Coast9Vd_ResolvesToHeadingToDistance_WithNavaidDistanceAndWindow()
    {
        if (NavigationDatabase.Instance.GetFixPosition("OAK") is not { } oak)
        {
            return; // OAK navaid not in test data — skip.
        }

        // KOAK COAST9 RWY 30 leg 10: heading 296° to OAK 4.0 DME, between 1400 and 2000.
        var window = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 2000, 1400);
        var legs = ProcedureLegResolver.Resolve([DistanceLeg(CifpPathTerminator.VD, "OAK", 296.0, 4.0, window)]);

        Assert.Single(legs);
        var leg = legs[0];
        Assert.Equal(ProcedureLegType.HeadingToDistance, leg.Type);
        Assert.Equal(296.0, leg.CourseMagnetic);
        Assert.Equal(4.0, leg.TerminationDistanceNm);
        Assert.NotNull(leg.TerminationReferencePosition);
        Assert.Equal(oak.Lat, leg.TerminationReferencePosition!.Value.Lat, precision: 4);
        Assert.Equal(oak.Lon, leg.TerminationReferencePosition.Value.Lon, precision: 4);
        Assert.Equal(CifpAltitudeRestrictionType.Between, leg.AltitudeRestriction!.Type);
        Assert.Equal(2000, leg.AltitudeRestriction.Altitude1Ft);

        // The distance leg is a coded leg, so it stays the leading leg the phase flies.
        var active = ProcedureLegResolver.ExtractActiveDepartureLegs(legs);
        Assert.NotNull(active);
        Assert.Equal(ProcedureLegType.HeadingToDistance, active![0].Type);
    }

    [Fact]
    public void CdLeg_ResolvesToCourseToDistance()
    {
        if (NavigationDatabase.Instance.GetFixPosition("OAK") is null)
        {
            return;
        }
        var legs = ProcedureLegResolver.Resolve([DistanceLeg(CifpPathTerminator.CD, "OAK", 135.0, 9.0, null)]);
        Assert.Single(legs);
        Assert.Equal(ProcedureLegType.CourseToDistance, legs[0].Type);
        Assert.Equal(9.0, legs[0].TerminationDistanceNm);
    }

    [Fact]
    public void FcLeg_ResolvesFromOriginFix_NotNavaid()
    {
        if (NavigationDatabase.Instance.GetFixPosition("SEGUL") is not { } segul)
        {
            return;
        }
        // FC: track a course from SEGUL for 5 nm. Reference is the origin fix, not a navaid.
        var legs = ProcedureLegResolver.Resolve([DistanceLeg(CifpPathTerminator.FC, "SEGUL", 90.0, 5.0, null)]);
        Assert.Single(legs);
        Assert.Equal(ProcedureLegType.CourseToDistance, legs[0].Type);
        Assert.Equal(segul.Lat, legs[0].TerminationReferencePosition!.Value.Lat, precision: 4);
    }

    [Fact]
    public void VrLeg_ResolvesToHeadingToRadial_WithTheta()
    {
        if (NavigationDatabase.Instance.GetFixPosition("OAK") is null)
        {
            return;
        }
        var leg = new CifpLeg(
            FixIdentifier: "",
            PathTerminator: CifpPathTerminator.VR,
            TurnDirection: null,
            Altitude: null,
            Speed: null,
            FixRole: CifpFixRole.None,
            Sequence: 10,
            OutboundCourse: 120.0,
            LegDistanceNm: null,
            VerticalAngle: null,
            RecommendedNavaidId: "OAK",
            Theta: 165.0
        );
        var legs = ProcedureLegResolver.Resolve([leg]);
        Assert.Single(legs);
        Assert.Equal(ProcedureLegType.HeadingToRadial, legs[0].Type);
        Assert.Equal(165.0, legs[0].TerminationRadialMagnetic);
    }

    private static ProcedureLeg Va() =>
        new()
        {
            Type = ProcedureLegType.HeadingToAltitude,
            CourseMagnetic = 343,
            TargetAltitudeFt = 9100,
        };

    private static ProcedureLeg Vi() =>
        new()
        {
            Type = ProcedureLegType.HeadingToIntercept,
            CourseMagnetic = 273,
            TerminatesOnNextLegIntercept = true,
        };

    private static ProcedureLeg Cf(string fix) =>
        new()
        {
            Type = ProcedureLegType.CourseToFix,
            CourseMagnetic = 303,
            FixName = fix,
            FixPosition = new LatLon(39.3, -107.1),
        };

    private static ProcedureLeg Tf(string fix) =>
        new()
        {
            Type = ProcedureLegType.TrackToFix,
            FixName = fix,
            FixPosition = new LatLon(39.4, -107.2),
        };

    [Fact]
    public void LindzShape_ReturnsAllThreeCodedLegs()
    {
        var prefix = ProcedureLegResolver.ExtractActiveDepartureLegs([Va(), Vi(), Cf("LINDZ"), Tf("SLOLM")]);
        Assert.NotNull(prefix);
        Assert.Equal(3, prefix!.Count);
        Assert.Equal(ProcedureLegType.CourseToFix, prefix[2].Type);
    }

    [Fact]
    public void LeadingCfThenFix_ReturnsOnlyTheLeadingCf()
    {
        var prefix = ProcedureLegResolver.ExtractActiveDepartureLegs([Cf("ABCDE"), Tf("FGHIJ"), Tf("KLMNO")]);
        Assert.NotNull(prefix);
        Assert.Single(prefix!);
        Assert.Equal(ProcedureLegType.CourseToFix, prefix![0].Type);
    }

    [Fact]
    public void LeadingPlainFix_ReturnsNull()
    {
        Assert.Null(ProcedureLegResolver.ExtractActiveDepartureLegs([Tf("ABCDE"), Cf("FGHIJ")]));
    }

    [Fact]
    public void AllPlainFixes_ReturnsNull()
    {
        Assert.Null(ProcedureLegResolver.ExtractActiveDepartureLegs([Tf("ABCDE"), Tf("FGHIJ"), Tf("KLMNO")]));
    }
}
