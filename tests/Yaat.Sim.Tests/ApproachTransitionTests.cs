using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for approach transition selection, fix name resolution, and programmed fix expansion
/// using real NavData.dat + FAACIFP18 data. All tests silently skip if navdata is absent.
/// </summary>
public class ApproachTransitionTests(ITestOutputHelper output)
{
    private static NavigationDatabase? GetNavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb;
    }

    private static AircraftState MakeAircraft(
        string callsign = "N123",
        string aircraftType = "B738",
        string route = "",
        double heading = 280,
        double lat = 37.62,
        double lon = -122.38,
        string destination = "KSFO",
        string? destinationRunway = null
    )
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Heading = heading,
            Altitude = 5000,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
            DestinationRunway = destinationRunway,
            Route = route,
        };
    }

    // --- SelectBestTransition ---

    [Fact]
    public void SelectBestTransition_NoTransitions_ReturnsNull()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO LOC/DME 28R (L28R) — verify it has no transitions (localizer-only approaches
        // at SFO typically don't). If it does, find another approach without transitions.
        var procedure = navDb.GetApproach("KSFO", "L28R");
        if (procedure is null || procedure.Transitions.Count > 0)
        {
            // Fall back to any SFO approach with no transitions
            var approaches = navDb.GetApproaches("KSFO");
            procedure = approaches.FirstOrDefault(a => a.Transitions.Count == 0);
        }

        if (procedure is null)
        {
            output.WriteLine("No SFO approach without transitions found, skipping");
            return;
        }

        output.WriteLine($"Using {procedure.ApproachId} (0 transitions)");

        var aircraft = MakeAircraft();
        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, navDb);

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestTransition_NavRouteContainsApproachFix_ReturnsNull()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO I19L has CCR transition (CCR → UPEND → BERKS) and common legs starting at BERKS.
        // An aircraft with BERKS in its NavigationRoute (from ALWYS3 STAR) should NOT match
        // the CCR transition — it's already heading to the approach.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);
        Assert.True(procedure.Transitions.Count > 0, "I19L should have transitions");

        output.WriteLine($"I19L transitions: {string.Join(", ", procedure.Transitions.Keys)}");

        // Find BERKS in the approach (should be in common legs)
        var berksPos = navDb.GetFixPosition("BERKS");
        Assert.NotNull(berksPos);

        var aircraft = MakeAircraft(route: "COORZ6 VOAXA Q136 RUMPS OAL INYOE ALWYS3", heading: 261, lat: 37.64, lon: -120.94);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "BERKS",
                Latitude = berksPos.Value.Lat,
                Longitude = berksPos.Value.Lon,
            }
        );

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, navDb);

        output.WriteLine($"Selected: {result?.Name ?? "(none)"}");
        Assert.Null(result);
    }

    [Fact]
    public void SelectBestTransition_RouteContainsTransitionIaf_ReturnsTransition()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO I19L has the CCR transition. An aircraft with CCR in its route
        // (not in NavigationRoute — already consumed) should match that transition.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);
        Assert.True(procedure.Transitions.ContainsKey("CCR"), "I19L should have CCR transition");

        var ccrTransition = procedure.Transitions["CCR"];
        var legNames = ccrTransition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier!).ToList();
        output.WriteLine($"CCR transition legs: {string.Join(" → ", legNames)}");

        // Aircraft route includes CCR — should match CCR transition
        var aircraft = MakeAircraft(route: "SJC V334 CCR");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, navDb);

        Assert.NotNull(result);
        Assert.Equal("CCR", result.Name);
    }

    [Fact]
    public void SelectBestTransition_EmptyRouteNoNavRoute_FallsBackToNearestAhead()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO I19L with CCR transition. Aircraft with no route and no NavRoute,
        // heading roughly toward CCR — fallback should pick nearest transition IAF ahead.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        // Position northeast of SFO, heading southwest — CCR should be roughly ahead
        var aircraft = MakeAircraft(route: "", heading: 250, lat: 38.0, lon: -122.0);

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, navDb);

        // Should find some transition via fallback (CCR is the only one and should be ahead)
        output.WriteLine($"Selected: {result?.Name ?? "(none)"}");
        Assert.NotNull(result);
    }

    [Fact]
    public void SelectBestTransition_NoNavDb_NoRouteMatch_ReturnsNull()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        var aircraft = MakeAircraft(route: "");

        // No navDb passed → fallback can't run, no route match → null
        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, null);

        Assert.Null(result);
    }

    // --- GetApproachFixNames ---

    [Fact]
    public void GetApproachFixNames_IncludesTransitionAndCommonFixes()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        output.WriteLine($"Fix names ({names.Count}): {string.Join(", ", names)}");

        // Should include CCR transition fixes
        Assert.Contains("CCR", names);

        // Should include common leg fixes (before MAHP)
        var commonFixNames = procedure
            .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier) && l.FixRole != CifpFixRole.MAHP)
            .Select(l => l.FixIdentifier!)
            .ToList();
        foreach (var fix in commonFixNames)
        {
            Assert.Contains(fix, names);
        }

        // MAHP (RW19L) should be excluded
        var mahp = procedure.CommonLegs.FirstOrDefault(l => l.FixRole == CifpFixRole.MAHP);
        if (mahp is not null && !string.IsNullOrEmpty(mahp.FixIdentifier))
        {
            Assert.DoesNotContain(mahp.FixIdentifier, names);
        }
    }

    [Fact]
    public void GetApproachFixNames_WithoutTransitions_ReturnsCommonOnly()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var approaches = navDb.GetApproaches("KSFO");
        var procedure = approaches.FirstOrDefault(a => a.Transitions.Count == 0);
        if (procedure is null)
        {
            output.WriteLine("No SFO approach without transitions, skipping");
            return;
        }

        output.WriteLine($"Using {procedure.ApproachId}");

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        output.WriteLine($"Fix names ({names.Count}): {string.Join(", ", names)}");
        Assert.True(names.Count > 0, "Should have at least one common leg fix");
    }

    [Fact]
    public void GetApproachFixNames_NoDuplicates()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        // Every fix name should appear exactly once (boundary fix dedup)
        var duplicates = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.Empty(duplicates);
    }

    // --- CAPP with transition ---

    [Fact]
    public void Capp_WithTransitionIafInRoute_BuildsFullFixSequence()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        // Aircraft with CCR in route → should select CCR transition
        var aircraft = MakeAircraft(route: "SJC V334 CCR", destinationRunway: "19L", heading: 320, lat: 37.5, lon: -122.1);
        var cmd = new ClearedApproachCommand("I19L", "KSFO", false, null, null, null, null, null, null, null, null);

        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft, navDb);

        output.WriteLine($"CAPP result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var navPhase = aircraft.Phases.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        Assert.NotNull(navPhase);

        var fixNames = navPhase.Fixes.Select(f => f.Name).ToList();
        output.WriteLine($"Approach fixes: {string.Join(" → ", fixNames)}");

        // Should include CCR (from transition)
        Assert.Contains("CCR", fixNames);

        // Boundary fix should not be duplicated
        var ccrTransition = procedure.Transitions["CCR"];
        var lastTransitionFix = ccrTransition.Legs.LastOrDefault(l => !string.IsNullOrEmpty(l.FixIdentifier))?.FixIdentifier;
        var firstCommonFix = procedure.CommonLegs.FirstOrDefault(l => !string.IsNullOrEmpty(l.FixIdentifier))?.FixIdentifier;
        if (
            lastTransitionFix is not null
            && firstCommonFix is not null
            && lastTransitionFix.Equals(firstCommonFix, StringComparison.OrdinalIgnoreCase)
        )
        {
            int count = fixNames.Count(n => n.Equals(lastTransitionFix, StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"Boundary fix '{lastTransitionFix}' appears {count} time(s)");
            Assert.Equal(1, count);
        }
    }

    // --- ProgrammedFixResolver with STAR runway transitions ---

    [Fact]
    public void ProgrammedFixResolver_WithStarAndRunway_IncludesRunwayTransitionFixes()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // ALWYS3 at KSFO should have runway transitions (e.g. RW19L/RW19B with fixes like BERKS)
        var star = navDb.GetStar("KSFO", "ALWYS3");
        if (star is null)
        {
            output.WriteLine("ALWYS3 not found at KSFO, skipping");
            return;
        }

        output.WriteLine($"ALWYS3 runway transitions: {string.Join(", ", star.RunwayTransitions.Keys)}");

        // Find a runway transition that has fixes
        var rwyTransition = star.RunwayTransitions.FirstOrDefault(t => t.Value.Legs.Any(l => !string.IsNullOrEmpty(l.FixIdentifier)));
        if (rwyTransition.Value is null)
        {
            output.WriteLine("No runway transition with fixes, skipping");
            return;
        }

        var rwyId = rwyTransition.Key.Replace("RW", "");
        output.WriteLine($"Testing runway transition {rwyTransition.Key} (runway {rwyId})");

        var expectedFixes = rwyTransition.Value.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier!).ToList();
        output.WriteLine($"Expected fixes: {string.Join(", ", expectedFixes)}");

        var result = ProgrammedFixResolver.Resolve("ALWYS3", null, "KSFO", null, navDb, null, navDb, "ALWYS3", rwyId);

        foreach (var fix in expectedFixes)
        {
            Assert.Contains(fix, result);
        }
    }

    [Fact]
    public void ProgrammedFixResolver_WithStarNoRunway_DoesNotExpandRunwayTransition()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var star = navDb.GetStar("KSFO", "ALWYS3");
        if (star is null)
        {
            output.WriteLine("ALWYS3 not found at KSFO, skipping");
            return;
        }

        // Find a fix that only appears in a runway transition (not common legs)
        var commonFixNames = star
            .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier))
            .Select(l => l.FixIdentifier!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? rwyOnlyFix = null;
        foreach (var transition in star.RunwayTransitions.Values)
        {
            foreach (var leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && !commonFixNames.Contains(leg.FixIdentifier))
                {
                    rwyOnlyFix = leg.FixIdentifier;
                    break;
                }
            }

            if (rwyOnlyFix is not null)
            {
                break;
            }
        }

        if (rwyOnlyFix is null)
        {
            output.WriteLine("No runway-transition-only fix found, skipping");
            return;
        }

        output.WriteLine($"Runway-transition-only fix: {rwyOnlyFix}");

        // Without a runway, runway transitions should NOT be expanded
        var result = ProgrammedFixResolver.Resolve("ALWYS3", null, "KSFO", null, navDb, null, navDb, "ALWYS3", null);

        Assert.DoesNotContain(rwyOnlyFix, result);
    }

    [Fact]
    public void ProgrammedFixResolver_DeriveRunwayFromExpectedApproach()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var star = navDb.GetStar("KSFO", "ALWYS3");
        if (star is null)
        {
            output.WriteLine("ALWYS3 not found at KSFO, skipping");
            return;
        }

        // Find a runway transition and a corresponding approach
        foreach (var (rwyKey, transition) in star.RunwayTransitions)
        {
            var rwyId = rwyKey.Replace("RW", "");
            var rwyFixes = transition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier!).ToList();

            if (rwyFixes.Count == 0)
            {
                continue;
            }

            // Find an approach for this runway
            var approaches = navDb.GetApproaches("KSFO");
            var approach = approaches.FirstOrDefault(a => a.Runway == rwyId);
            if (approach is null)
            {
                continue;
            }

            output.WriteLine($"STAR runway transition {rwyKey}, approach {approach.ApproachId}");
            output.WriteLine($"Runway transition fixes: {string.Join(", ", rwyFixes)}");

            // No explicit destinationRunway, but expectedApproach should derive it
            var result = ProgrammedFixResolver.Resolve(null, approach.ApproachId, "KSFO", null, navDb, null, navDb, "ALWYS3", null);

            // Should include runway transition fixes (derived from expected approach → runway)
            foreach (var fix in rwyFixes)
            {
                Assert.Contains(fix, result);
            }

            return; // Found and tested one case
        }

        output.WriteLine("No matching STAR runway transition + approach found, skipping");
    }
}
