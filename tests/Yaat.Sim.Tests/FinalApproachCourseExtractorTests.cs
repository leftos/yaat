using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="FinalApproachCourseExtractor"/>.
///
/// Each approach was researched via tools/Yaat.CifpInspector against the bundled FAACIFP18.gz.
/// The expected courses below come from the published CIFP records, not from Yaat's runtime.
/// </summary>
public class FinalApproachCourseExtractorTests
{
    private static (NavigationDatabase NavDb, CifpApproachProcedure Procedure, RunwayInfo Runway)? Load(
        string airport,
        string approachId,
        string runwayDesignator
    )
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        NavigationDatabase.SetInstance(navDb);

        var procedure = navDb.GetApproach(airport, approachId);
        var runway = navDb.GetRunway(airport, runwayDesignator);
        if (procedure is null || runway is null)
        {
            return null;
        }

        return (navDb, procedure, runway);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Offset approaches — these MUST diverge from the runway heading
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_KccrS19R_VorOffset_ReturnsOffsetCourse()
    {
        // KCCR VOR Rwy 19R: published final approach course is 171.7° magnetic via the CCR VOR.
        // Runway 19R magnetic heading is ~190°. The approach is offset by ~18°.
        var loaded = Load("CCR", "S19R", "19R");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        // The offset should be at least 10° relative to the runway heading
        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff > 10.0,
            $"KCCR S19R is offset; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected >10°"
        );

        // RW19R is a runway pseudo-fix, so no parallel-offset anchor
        Assert.Null(result.AnchorLat);
    }

    [Fact]
    public void Extract_KsfoR10L_RnavTfLegs_UsesComputedBearing()
    {
        // KSFO R10L is the user-reported bug: RNAV(GPS) approach with TF legs and no published
        // OutboundCourse. The extractor must compute the bearing between fix endpoints.
        // Runway 10L magnetic heading ~096°. Whether this approach is genuinely offset depends
        // on the leg endpoints — the test asserts the extractor returns a sensible value (not
        // the runway heading via fallback) and is in the general "easterly" sector.
        var loaded = Load("SFO", "R10L", "10L");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        // Course is in the easterly sector (50°-150° true).
        Assert.InRange(result.Course.Degrees, 50.0, 150.0);

        // The extractor must NOT silently fall back to runway heading. They may match within a
        // few degrees if the TF legs happen to be aligned, but the path used must be the bearing
        // computation, not the runway-heading fallback. This test catches the regression where
        // the extractor fails to resolve fix coordinates and falls back.
        // (We can't directly observe which path was taken, but we can verify the result is not
        // numerically *exactly* equal to the runway heading.)
        bool exactRunwayMatch = Math.Abs(result.Course.Degrees - runway.TrueHeading.Degrees) < 0.001;
        Assert.False(exactRunwayMatch, "Computed bearing should differ from runway heading by at least floating-point noise");
    }

    [Fact]
    public void Extract_KdcaX19Z_AngularOffsetLda_ReturnsHeavilyOffsetCourse()
    {
        // KDCA LDA-X RWY 19 publishes a final approach course of 147° magnetic vs runway 19's
        // ~190° magnetic — a ~43° angular offset. The MAP fix (ZAXEB) terminates at the runway
        // threshold (within ~300 ft of the runway-19 extended centerline), so this is an
        // ANGULAR offset, not a parallel offset: the FAC line passes through the threshold
        // at an angle, and FinalApproachPhase uses the threshold as the cross-track anchor.
        //
        // True parallel-offset LDAs (where the FAS is laterally displaced from the threshold
        // and pilots execute a visual sidestep) are rare in published CIFP data; if such an
        // approach is added to the test corpus, expand DetermineAnchor coverage at that point.
        var loaded = Load("DCA", "X19-Z", "19");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff > 30.0,
            $"KDCA X19-Z is heavily offset; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected >30°"
        );

        // Anchor stays null because the MAP fix sits on the runway extended centerline.
        Assert.Null(result.AnchorLat);
        Assert.Null(result.AnchorLon);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Aligned approaches — these must match runway heading within mag-variation tolerance
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_KsfoI28R_AlignedIls_ReturnsCourseMatchingRunway()
    {
        var loaded = Load("SFO", "I28R", "28R");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff < 3.0,
            $"KSFO I28R is aligned; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected <3°"
        );

        Assert.Null(result.AnchorLat);
    }

    [Fact]
    public void Extract_KoakI12_AlignedIls_ReturnsCourseMatchingRunway()
    {
        // KOAK ILS 12 is the Issue #101 precedent: ~10° magnetic-vs-true variation between
        // runway designator and CIFP course. Both should still resolve close together once
        // the extractor converts mag→true.
        var loaded = Load("OAK", "I12", "12");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff < 3.0,
            $"KOAK I12 is aligned; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected <3°"
        );

        Assert.Null(result.AnchorLat);
    }

    [Fact]
    public void Extract_KmceB12_LocBackCourse_ReturnsCourseMatchingRunway()
    {
        // KMCE LOC BC RWY 12: back-course approach. The CIFP CF leg publishes the inbound
        // course to the runway directly (123° magnetic), so the extractor's CF path should
        // give a result that aligns with runway 12's heading without any special back-course
        // handling.
        var loaded = Load("MCE", "B12", "12");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff < 5.0,
            $"KMCE B12 LOC BC is aligned; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected <5°"
        );
    }

    [Fact]
    public void Extract_KiahI26R_AlignedIls_MatchesRunwayCenterline()
    {
        // Issue #187: KIAH ILS 26R publishes a 267° MAGNETIC final course. The runway's magnetic
        // bearing (CIFP PG record) is also 267.0° and its geometric true heading is ~269.95°, so the
        // localizer is aligned with the runway and the variation of record is ~+3.0°E (CIFP PA
        // record E0030). Converting 267° with the current WMM declination (~+1.7°) instead yields
        // ~268.7° true — ~1.24° north of the centerline. With the correct (CIFP) variation the FAC
        // lands on the runway centerline.
        var loaded = Load("IAH", "I26R", "26R");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff < 0.7,
            $"KIAH I26R is an aligned ILS; FAC {result.Course.Degrees:F2}° should match runway {runway.TrueHeading.Degrees:F2}° (diff {diff:F2}°)"
        );
        Assert.Null(result.AnchorLat);
    }

    [Fact]
    public void Extract_KccrR19R_AlignedRnav_ReturnsCourseFromBearing()
    {
        // KCCR RNAV (GPS) RWY 19R: aligned RNAV with TF legs (no OutboundCourse). The extractor
        // must compute bearing from the FAF→MAP segment. Result should be close to runway 19R heading.
        var loaded = Load("CCR", "R19R", "19R");
        if (loaded is null)
        {
            return;
        }
        var (navDb, procedure, runway) = loaded.Value;

        var result = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);

        double diff = Math.Abs(GeoMath.SignedBearingDifference(result.Course.Degrees, runway.TrueHeading.Degrees));
        Assert.True(
            diff < 5.0,
            $"KCCR R19R aligned RNAV; FAC {result.Course.Degrees:F1}° vs runway {runway.TrueHeading.Degrees:F1}° (diff {diff:F1}°) — expected <5°"
        );
    }
}
