using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Phase-wiring tests for the SOFIA TWO VOR/DME RWY 19R approach at KCCR (S19R) when an
/// aircraft has just passed FAWNE (the common-leg IAF) and CAPP S19R is issued with an
/// empty NavigationRoute.
///
/// Bug context: N182AK was given <c>DCT FAWNE</c>, the DCT completed (NavigationRoute
/// emptied), then <c>CAPP S19R</c> was issued. <see cref="ApproachCommandHandler.SelectBestTransition"/>
/// fell into its position-based fallback, which only considers transition IAFs. REJOY
/// (a transition IAF 9.2 nm east) was selected even though FAWNE — a common-leg IAF —
/// was 0.33 nm away. The aircraft turned ~64° right and started flying back to REJOY.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/n182ak-ccr-s19r-fawne-iaf-recording.yaat-bug-report-bundle.zip
///
/// CIFP shape (verified via Yaat.CifpInspector):
///   Common: FAWNE (IAF, IF) → HUKVI (CF, 190.6°) → CCR (FAF, CF, 190.6°) → RW19R (MAP, 171.7°)
///   Transition COLLI: COLLI → CCR (TF, ≥4000) → CCR (PI, IAF, ≥2600, 55.6°, 10 nm, .L)
///   Transition REJOY: REJOY (IAF) → FAWNE (IF, ≥2500)  [NoPT]
/// </summary>
public class N182akCcrS19RFawneIafTests(ITestOutputHelper output)
{
    private static NavigationDatabase? GetNavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb;
    }

    private static AircraftState MakeC182NearFawne(NavigationDatabase navDb, double trueHeading)
    {
        // Position SSE of FAWNE matches recording snapshot at t=1490 (38.16606, -121.96502).
        // Hard-coded literal so the test geometry is explicit; FAWNE position itself comes from navdata.
        return new AircraftState
        {
            Callsign = "N182AK",
            AircraftType = "C182/G",
            TrueHeading = new TrueHeading(trueHeading),
            Position = new LatLon(38.16606, -121.96502),
            Altitude = 6000,
            IndicatedAirspeed = 80,
            Declination = 12.876, // matches snapshot
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Destination = "KCCR",
                Route = "OAK ENI KEARN",
                FlightRules = "IFR",
            },
            Procedure = new AircraftProcedure { DestinationRunway = null },
        };
    }

    /// <summary>
    /// Targeted regression: with empty NavigationRoute and the aircraft 0.33 nm SSE of FAWNE
    /// heading NNE, SelectBestTransition's position-based fallback must NOT pick REJOY just
    /// because it's the nearest transition IAF within ±90°. FAWNE — a common-leg IAF — is
    /// closer; the aircraft is essentially on top of the IAF that starts the common segment.
    /// Returning the REJOY transition would route the aircraft 9.2 nm east, the wrong way.
    /// </summary>
    [Fact]
    public void SelectBestTransition_AtFawneWithEmptyNavRoute_ReturnsNullNotRejoy()
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

        var aircraft = MakeC182NearFawne(navDb, trueHeading: 28.0);
        Assert.Empty(aircraft.Targets.NavigationRoute);

        var selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);
        output.WriteLine($"Selected transition: {selected?.Name ?? "(none)"}");

        Assert.Null(selected);
    }

    /// <summary>
    /// Full CAPP S19R via the handler. The selected transition must be null (per the unit
    /// test above), so the approach loads from the common legs only — FAWNE → HUKVI → CCR.
    /// No procedure turn engages: even though the aircraft's instantaneous heading (28° true)
    /// would intercept the FAC at 156°, the published common-leg sequence delivers the
    /// aircraft to CCR on the 191° magnetic feeder course (~178° true), which intercepts
    /// the 184.55° true FAC at ~6° — clean alignment, no course reversal needed.
    /// </summary>
    [Fact]
    public void Capp_AtFawneWithEmptyNavRoute_S19RDoesNotRouteBackToRejoy()
    {
        var navDb = GetNavDb();
        if (navDb is null || navDb.GetApproach("KCCR", "S19R") is null)
        {
            output.WriteLine("NavData/S19R not available, skipping");
            return;
        }

        var aircraft = MakeC182NearFawne(navDb, trueHeading: 28.0);

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

        output.WriteLine($"CAPP result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        foreach (var phase in aircraft.Phases.Phases)
        {
            output.WriteLine($"Phase: {phase.GetType().Name}");
        }

        var navPhase = aircraft.Phases.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        Assert.NotNull(navPhase);
        var fixNames = navPhase.Fixes.Select(f => f.Name).ToList();
        output.WriteLine($"Approach fixes: {string.Join(" → ", fixNames)}");

        Assert.DoesNotContain("REJOY", fixNames);
        Assert.Equal("FAWNE", fixNames[0]);
        Assert.Contains("HUKVI", fixNames);
        Assert.Contains("CCR", fixNames);

        // No procedure turn — the published feeder delivers alignment.
        var pt = aircraft.Phases.Phases.OfType<ProcedureTurnPhase>().FirstOrDefault();
        Assert.Null(pt);
    }
}
