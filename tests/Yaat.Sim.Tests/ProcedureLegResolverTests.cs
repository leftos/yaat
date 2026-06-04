using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="ProcedureLegResolver.ExtractActiveDepartureLegs"/> selects the leading run of coded
/// legs (heading/intercept + course-to-fix) a departure procedure phase flies, stopping at the
/// first plain fix so interior fix-to-fix legs keep FlightPhysics' turn anticipation.
/// </summary>
public class ProcedureLegResolverTests
{
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
