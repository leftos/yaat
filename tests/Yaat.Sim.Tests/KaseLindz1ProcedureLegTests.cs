using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// The LINDZ ONE departure out of KASE (Aspen) RWY 33 is encoded as three ARINC 424 legs:
/// VA 343°→≥9100, VI 273° (climbing left turn to intercept), CF 303°→LINDZ ≥16000. The flat
/// resolver drops the two fix-less heading legs and discards the CF course; the typed resolver
/// must preserve all three so the aircraft can fly the charted climb-then-turn-then-back-course.
/// </summary>
[Collection("NavDbMutator")]
public class KaseLindz1ProcedureLegTests
{
    public KaseLindz1ProcedureLegTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Resolve_Lindz1Rw33_PreservesHeadingAndCourseLegs()
    {
        var sid = NavigationDatabase.Instance.GetSid("KASE", "LINDZ1");
        if (sid is null || !sid.RunwayTransitions.TryGetValue("RW33", out var rw33))
        {
            return; // LINDZ1 not present in the bundled CIFP cycle — skip offline.
        }

        var legs = ProcedureLegResolver.Resolve(rw33.Legs);

        Assert.Equal(3, legs.Count);

        Assert.Equal(ProcedureLegType.HeadingToAltitude, legs[0].Type);
        Assert.Equal(343.0, legs[0].CourseMagnetic!.Value, 1);
        Assert.Equal(9100.0, legs[0].TargetAltitudeFt!.Value, 0);

        Assert.Equal(ProcedureLegType.HeadingToIntercept, legs[1].Type);
        Assert.Equal(273.0, legs[1].CourseMagnetic!.Value, 1);
        Assert.True(legs[1].TerminatesOnNextLegIntercept);

        Assert.Equal(ProcedureLegType.CourseToFix, legs[2].Type);
        Assert.Equal("LINDZ", legs[2].FixName);
        Assert.NotNull(legs[2].FixPosition);
        Assert.Equal(303.0, legs[2].CourseMagnetic!.Value, 1);
        Assert.NotNull(legs[2].AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, legs[2].AltitudeRestriction!.Type);
        Assert.Equal(16000, legs[2].AltitudeRestriction!.Altitude1Ft);
    }
}
