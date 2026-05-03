using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Phase-wiring tests for the SOFIA TWO VOR/DME RWY 19R approach at KCCR (S19R),
/// which has a published procedure turn at CCR (PI leg in the COLLI transition).
///
/// Bug context: SWA5131 was south of CCR and given DCT CCR; CAPP S19R. The expected
/// behavior is to cross CCR, fly outbound 011 (FAC reciprocal), climb to ≥2600,
/// execute the published 45°/180° procedure turn, and intercept inbound on the FAC
/// at 191° magnetic. Today, ApproachCommandHandler.BuildFixesFromLegs silently
/// drops PI legs and SelectBestTransition refuses to engage COLLI when CCR is also
/// in CommonLegs (it is — CCR is the FAF). Result: the aircraft flies straight to
/// FAWNE (NE of CCR) and bypasses the course reversal entirely.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/swa5131-ccr-s19r-pt-recording.yaat-bug-report-bundle.zip
///
/// CIFP shape (verified via Yaat.CifpInspector):
///   Common: FAWNE → HUKVI → CCR (FAF) → RW19R (MAP)
///   Transition COLLI: COLLI → CCR (TF, ≥4000) → CCR (PI, IAF, ≥2600, course 55.6°, 10 nm, .L)
///   Transition REJOY: REJOY → FAWNE
///
/// PI leg semantics (per ARINC 424 + cifparse reference):
///   - course 55.6° = 45°-offset PT outbound heading (magnetic) — implies right turn from outbound radial
///   - LegDistanceNm 10.0 = max PT outbound distance from fix
///   - TurnDirection 'L' = direction of the 180° turn back to inbound
///   - Altitude ≥2600 = minimum altitude during the PT
/// </summary>
public class IssueSwa5131PtKccrS19RTests(ITestOutputHelper output)
{
    private static NavigationDatabase? GetNavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb;
    }

    /// <summary>
    /// CCR (Concord VOR) — anchor for the procedure turn on the S19R approach.
    /// Position from real navdata; do not hardcode if it ever changes.
    /// </summary>
    private static (double Lat, double Lon) CcrPosition(NavigationDatabase navDb)
    {
        var pos = navDb.GetFixPosition("CCR");
        Assert.NotNull(pos);
        return pos.Value;
    }

    private static AircraftState MakeB738(double lat, double lon, double trueHeading, double altitude = 6000)
    {
        return new AircraftState
        {
            Callsign = "SWA5131",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(trueHeading),
            Position = new LatLon(lat, lon),
            Altitude = altitude,
            IndicatedAirspeed = 200,
            Declination = 13.0, // Approx for KCCR area; matches snapshot at t=1605.
            FlightPlan = new AircraftFlightPlan { Destination = "KCCR", Route = "" },
            Procedure = new AircraftProcedure { DestinationRunway = null },
        };
    }

    [Fact]
    public void Capp_S19R_FromSouth_InsertsProcedureTurnPhaseAtCcr()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            output.WriteLine("NavData not available, skipping");
            return;
        }

        var procedure = navDb.GetApproach("KCCR", "S19R");
        if (procedure is null)
        {
            output.WriteLine("KCCR S19R not found, skipping");
            return;
        }

        // Aircraft south of CCR, heading NE — needs course reversal at CCR to fly inbound on 191°.
        var (ccrLat, ccrLon) = CcrPosition(navDb);
        var aircraft = MakeB738(lat: 37.88, lon: -122.28, trueHeading: 41.0);

        // DCT CCR was issued before CAPP — put CCR in the nav route.
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "CCR", Position = new LatLon(ccrLat, ccrLon) });

        var cmd = new ClearedApproachCommand(
            "S19R",
            "KCCR",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: "CCR",
            DctFixLat: ccrLat,
            DctFixLon: ccrLon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        output.WriteLine($"CAPP result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        foreach (var phase in aircraft.Phases.Phases)
        {
            output.WriteLine($"Phase: {phase.GetType().Name}");
        }

        var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
        Assert.NotNull(pt);
        Assert.Equal("CCR", pt.FixName);
        Assert.Equal(2600, pt.MinAltitudeFt);
        Assert.Equal(TurnDirection.Left, pt.OneEightyTurnDirection);

        // Inbound course (FAC) ≈ 191° magnetic ≈ 178° true at this declination. Extractor stores true.
        // Allow ±15° to absorb declination + extractor variance across navdata revisions.
        Assert.InRange(pt.InboundCourseDeg, 170.0, 200.0);
    }

    [Fact]
    public void Cappsi_S19R_FromSouth_RejectsBecauseInterceptAngleExceeds90()
    {
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        // Heading 041° vs FAC ~178° true → ~137° intercept, well above 90°.
        var aircraft = MakeB738(lat: 37.88, lon: -122.28, trueHeading: 41.0);

        var result = ApproachCommandHandler.TryJoinApproach("S19R", "KCCR", force: false, straightIn: true, aircraft);

        output.WriteLine($"CAPPSI result: {result.Success} — {result.Message}");
        Assert.False(result.Success);
        Assert.Contains("intercept angle", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unable straight-in", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capp_S19R_FromNorthAlignedWithFac_DoesNotInsertProcedureTurn()
    {
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        // North of CCR, heading south on the FAC (190° true ≈ 203° magnetic, but in true
        // we just need to be roughly aligned with 178° true inbound).
        var aircraft = MakeB738(lat: 38.20, lon: -122.05, trueHeading: 178.0);

        var cmd = new ClearedApproachCommand(
            "S19R",
            "KCCR",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: null,
            DctFixLat: null,
            DctFixLon: null,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
        Assert.Null(pt);
    }

    [Fact]
    public void Capp_S19R_NaturalArrivalAtCcr_InsertsProcedureTurn_EvenWhenAligned()
    {
        // Trigger #3 from AIM 5-4-9.1: aircraft has CCR in its NavigationRoute (e.g. arrived
        // via a STAR or earlier DCT) and is now roughly aligned with the FAC. SelectBestTransition
        // picks COLLI (CCR is the PI fix in COLLI). Even though the geometry would technically
        // permit straight-in, the published transition requires the course reversal — the chart
        // is the source of truth, not the aircraft's instantaneous heading.
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        var (ccrLat, ccrLon) = CcrPosition(navDb);

        // North of CCR, heading roughly inbound (178° true). Without DCT in the CAPP itself —
        // a separate prior DCT CCR put CCR in the route.
        var aircraft = MakeB738(lat: 38.20, lon: -122.05, trueHeading: 178.0);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "CCR", Position = new LatLon(ccrLat, ccrLon) });

        var cmd = new ClearedApproachCommand(
            "S19R",
            "KCCR",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: null,
            DctFixLat: null,
            DctFixLon: null,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
        Assert.NotNull(pt);
        Assert.Equal("CCR", pt.FixName);
    }

    [Fact]
    public void Capp_S19R_OnNoPtFeederViaRejoy_DoesNotInsertProcedureTurn()
    {
        // AIM 5-4-9.1 NoPT exclusion: aircraft entering via REJOY (a NoPT transition that
        // delivers the aircraft to FAWNE on the inbound side without requiring a course
        // reversal) must NOT trigger the PT, even if the aircraft's instantaneous heading
        // would otherwise look like a steep intercept.
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        var rejoyPos = navDb.GetFixPosition("REJOY");
        if (rejoyPos is null)
        {
            output.WriteLine("REJOY position not available, skipping");
            return;
        }

        // Aircraft east of REJOY, heading west toward REJOY — REJOY is in the route.
        var aircraft = MakeB738(lat: 38.17, lon: -121.50, trueHeading: 270.0);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "REJOY", Position = new LatLon(rejoyPos.Value.Lat, rejoyPos.Value.Lon) });

        var cmd = new ClearedApproachCommand(
            "S19R",
            "KCCR",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: null,
            DctFixLat: null,
            DctFixLon: null,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        // Result might be deferred or immediate depending on transition rules; either way,
        // no ProcedureTurnPhase should be inserted because REJOY is a NoPT transition.
        Assert.True(result.Success, result.Message);
        if (aircraft.Phases is not null)
        {
            var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
            Assert.Null(pt);
        }
    }

    [Fact]
    public void Capp_S19R_DctCcrFromAlignedHeading_StillInsertsPtBecauseDctOverridesGeometry()
    {
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        var (ccrLat, ccrLon) = CcrPosition(navDb);

        // North of CCR, aligned with FAC (≈178° true). Without DCT CCR, no PT.
        // With DCT CCR (controller intent: enter at CCR, fly the published procedure),
        // the PT should still be engaged.
        var aircraft = MakeB738(lat: 38.20, lon: -122.05, trueHeading: 178.0);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "CCR", Position = new LatLon(ccrLat, ccrLon) });

        var cmd = new ClearedApproachCommand(
            "S19R",
            "KCCR",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: "CCR",
            DctFixLat: ccrLat,
            DctFixLon: ccrLon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
        Assert.NotNull(pt);
        Assert.Equal("CCR", pt.FixName);
    }
}
