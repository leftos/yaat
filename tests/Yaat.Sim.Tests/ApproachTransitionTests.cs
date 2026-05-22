using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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
            TrueHeading = new TrueHeading(heading),
            Altitude = 5000,
            Position = new LatLon(lat, lon),
            FlightPlan = new AircraftFlightPlan { Destination = destination, Route = route },
            Procedure = new AircraftProcedure { DestinationRunway = destinationRunway },
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
        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

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

        // SFO I19L has UPEND transition (UPEND → BERKS) and common legs starting at BERKS.
        // An aircraft with BERKS in its NavigationRoute (from ALWYS3 STAR) should NOT match
        // the UPEND transition — it's already heading to the approach.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);
        Assert.True(procedure.Transitions.Count > 0, "I19L should have transitions");

        output.WriteLine($"I19L transitions: {string.Join(", ", procedure.Transitions.Keys)}");

        // Find BERKS in the approach (should be in common legs)
        var berksPos = navDb.GetFixPosition("BERKS");
        Assert.NotNull(berksPos);

        var aircraft = MakeAircraft(route: "COORZ6 VOAXA Q136 RUMPS OAL INYOE ALWYS3", heading: 261, lat: 37.64, lon: -120.94);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "BERKS", Position = new LatLon(berksPos.Value.Lat, berksPos.Value.Lon) });

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

        output.WriteLine($"Selected: {result?.Name ?? "(none)"}");
        Assert.Null(result);
    }

    [Fact]
    public void SelectBestTransition_NavRouteContainsTransitionOnlyFix_ReturnsTransition()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // OAK H12-Z has a HIRMO transition. HIRMO is a transition-only fix (not in CommonLegs).
        // An aircraft with HIRMO in its NavigationRoute (from EMZOH4 STAR) should match
        // the HIRMO transition so the full fix sequence is built.
        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        Assert.True(procedure.Transitions.Count > 0, "H12-Z should have transitions");
        output.WriteLine($"H12-Z transitions: {string.Join(", ", procedure.Transitions.Keys)}");

        var commonFixNames = procedure
            .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier))
            .Select(l => l.FixIdentifier!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"Common legs: {string.Join(", ", commonFixNames)}");

        // Find HIRMO — should be in a transition but NOT in common legs
        string transitionName = "HIRMO";
        Assert.True(procedure.Transitions.ContainsKey(transitionName), $"H12-Z should have {transitionName} transition");
        Assert.DoesNotContain(transitionName, commonFixNames);

        var hirmoPos = navDb.GetFixPosition(transitionName);
        Assert.NotNull(hirmoPos);

        // Aircraft on EMZOH4 STAR with HIRMO in nav route
        var aircraft = MakeAircraft(route: "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK", destination: "KOAK", heading: 320, lat: 37.5, lon: -121.8);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget { Name = transitionName, Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) }
        );

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

        output.WriteLine($"Selected: {result?.Name ?? "(none)"}");
        Assert.NotNull(result);
        Assert.Equal(transitionName, result.Name);
    }

    [Fact]
    public void Capp_WithNavRouteTransitionFix_DefersApproachAndAppendsFixesToRoute()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        // Resolve positions for STAR fixes preceding HIRMO
        var emzohPos = navDb.GetFixPosition("EMZOH");
        var hirmoPos = navDb.GetFixPosition("HIRMO");
        Assert.NotNull(emzohPos);
        Assert.NotNull(hirmoPos);

        // Aircraft on EMZOH4 STAR with remaining STAR fixes + HIRMO in nav route
        var aircraft = MakeAircraft(
            route: "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK",
            destination: "KOAK",
            destinationRunway: "12",
            heading: 320,
            lat: 37.5,
            lon: -121.8
        );
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzohPos.Value.Lat, emzohPos.Value.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });
        aircraft.Approach.Expected = "H12-Z";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        output.WriteLine($"CAPP result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        // Phases should NOT be started — approach is deferred
        Assert.Null(aircraft.Phases);

        // PendingApproachClearance should be set
        Assert.NotNull(aircraft.Approach.PendingClearance);
        Assert.Equal("H12-Z", aircraft.Approach.PendingClearance.Clearance.ApproachId);

        // NavigationRoute should contain STAR fixes + approach fixes after HIRMO
        var routeNames = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        output.WriteLine($"Nav route: {string.Join(" → ", routeNames)}");

        // STAR fix EMZOH should still be there
        Assert.Contains("EMZOH", routeNames);
        // HIRMO should still be there (connecting fix)
        Assert.Contains("HIRMO", routeNames);

        // Approach fixes should be appended after HIRMO
        int hirmoIdx = routeNames.IndexOf("HIRMO");
        Assert.True(routeNames.Count > hirmoIdx + 1, "Expected approach fixes after HIRMO in nav route");

        // DestinationRunway should be set
        Assert.Equal("12", aircraft.Procedure.DestinationRunway);
    }

    [Fact]
    public void Capp_SecondDeferred_DifferentRunwayApproach_ReplacesPriorTailAtAllxx()
    {
        // STAR to HIRMO (28B path), deferred CAPP H12-Z, then EAPP I30 + deferred CAPP H30-Z at ALLXX.
        // The second clearance must replace the H12-Z approach tail after ALLXX, not stack on it.
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        if (navDb.GetApproach("KOAK", "H12-Z") is null || navDb.GetApproach("KOAK", "H30-Z") is null)
        {
            output.WriteLine("KOAK H12-Z/H30-Z not available, skipping");
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        var allxxPos = navDb.GetFixPosition("ALLXX");
        Assert.NotNull(hirmoPos);
        Assert.NotNull(allxxPos);

        var aircraft = MakeAircraft(destination: "KOAK", destinationRunway: "12", heading: 320, lat: 37.5, lon: -121.8);
        aircraft.Procedure.ActiveStarId = "WNDSR2";
        foreach (var name in new[] { "WEBRR", "BOYYS", "HOPTA", "HIRMO" })
        {
            var pos = navDb.GetFixPosition(name)!.Value;
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = name, Position = new LatLon(pos.Lat, pos.Lon) });
        }

        var cappH12 = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(cappH12, aircraft).Success, "H12-Z deferred");
        Assert.Equal("H12-Z", aircraft.Approach.PendingClearance!.Clearance.ApproachId);

        int hirmoIdx = aircraft.Targets.NavigationRoute.ToList().FindIndex(t => t.Name == "HIRMO");
        var h12Tail = aircraft.Targets.NavigationRoute.Skip(hirmoIdx + 1).Select(t => t.Name).ToList();
        Assert.NotEmpty(h12Tail);

        var eapp = new ExpectApproachCommand("I30", null);
        Assert.True(CommandDispatcher.Dispatch(eapp, aircraft, TestDispatch.Context(Random.Shared)).Success);
        aircraft.Procedure.DestinationRunway = "30";

        int allxxIdxBeforeH30 = aircraft.Targets.NavigationRoute.ToList().FindIndex(t => t.Name == "ALLXX");
        Assert.True(allxxIdxBeforeH30 >= 0);
        foreach (NavigationTarget staleTarget in aircraft.Targets.NavigationRoute.Skip(hirmoIdx + 1).ToList())
        {
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = staleTarget.Name, Position = staleTarget.Position });
        }

        var cappH30 = new ClearedApproachCommand("H30-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(cappH30, aircraft).Success, "H30-Z deferred");
        Assert.Equal("H30-Z", aircraft.Approach.PendingClearance!.Clearance.ApproachId);

        var reference = MakeAircraft(destination: "KOAK", destinationRunway: "30", heading: 280, lat: 37.75, lon: -122.35);
        foreach (var name in new[] { "WEBRR", "BOYYS", "HOPTA", "ALLXX" })
        {
            var pos = navDb.GetFixPosition(name)!.Value;
            reference.Targets.NavigationRoute.Add(new NavigationTarget { Name = name, Position = new LatLon(pos.Lat, pos.Lon) });
        }

        Assert.True(ApproachCommandHandler.TryClearedApproach(cappH30, reference).Success);
        var expectedTail = reference
            .Targets.NavigationRoute.Skip(reference.Targets.NavigationRoute.ToList().FindIndex(t => t.Name == "ALLXX") + 1)
            .Select(t => t.Name)
            .ToList();
        var actualTail = aircraft.Targets.NavigationRoute.Skip(allxxIdxBeforeH30 + 1).Select(t => t.Name).ToList();
        Assert.Equal(expectedTail, actualTail);
    }

    [Fact]
    public void Capp_SecondDeferred_RemovesInjectedStaleTailAfterConnectingFix()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        if (hirmoPos is null || navDb.GetApproach("KOAK", "H12-Z") is null)
        {
            output.WriteLine("KOAK H12-Z / HIRMO not available, skipping");
            return;
        }

        var aircraft = MakeAircraft(destination: "KOAK", destinationRunway: "12", heading: 320, lat: 37.5, lon: -121.8);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });

        var first = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(first, aircraft).Success);

        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget { Name = "STALE_TAIL", Position = new LatLon(hirmoPos.Value.Lat + 0.01, hirmoPos.Value.Lon) }
        );

        var second = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(second, aircraft).Success);

        var names = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        Assert.DoesNotContain("STALE_TAIL", names);
        int hirmoIdx = names.IndexOf("HIRMO");
        Assert.True(hirmoIdx >= 0);
        Assert.True(names.Count > hirmoIdx + 1, "Expected a fresh approach tail after HIRMO");
    }

    [Fact]
    public void Capp_ImmediateAfterDeferred_ClearsPendingClearance()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        if (hirmoPos is null || navDb.GetApproach("KOAK", "H12-Z") is null)
        {
            return;
        }

        var aircraft = MakeAircraft(destination: "KOAK", destinationRunway: "12", heading: 320, lat: 37.5, lon: -121.8);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });

        var deferred = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(deferred, aircraft).Success);
        Assert.NotNull(aircraft.Approach.PendingClearance);

        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(320);
        var immediate = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        Assert.True(ApproachCommandHandler.TryClearedApproach(immediate, aircraft).Success);

        Assert.Null(aircraft.Approach.PendingClearance);
        Assert.NotNull(aircraft.Phases);
    }

    [Fact]
    public void Capp_OnAssignedHeading_ActivatesImmediately()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        Assert.NotNull(hirmoPos);

        // Aircraft on assigned heading with HIRMO in nav route
        var aircraft = MakeAircraft(
            route: "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK",
            destination: "KOAK",
            destinationRunway: "12",
            heading: 320,
            lat: 37.5,
            lon: -121.8
        );
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(320);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        // On assigned heading → immediate activation via intercept
        Assert.NotNull(aircraft.Phases);
        Assert.Null(aircraft.Approach.PendingClearance);
    }

    [Fact]
    public void Capp_WithAtConnectingFix_DefersLikeBareCapp()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        Assert.NotNull(hirmoPos);

        var aircraft = MakeAircraft(
            route: "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK",
            destination: "KOAK",
            destinationRunway: "12",
            heading: 320,
            lat: 37.5,
            lon: -121.8
        );
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });

        // AT HIRMO CAPP H12-Z — AT fix matches the connecting fix in the nav route,
        // so it defers just like a bare CAPP (the AT is redundant).
        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, "HIRMO", hirmoPos.Value.Lat, hirmoPos.Value.Lon, null, null, null, null, null);
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        // AT fix matches connecting fix → deferred, same as bare CAPP
        Assert.Null(aircraft.Phases);
        Assert.NotNull(aircraft.Approach.PendingClearance);
        Assert.Equal("H12-Z", aircraft.Approach.PendingClearance.Clearance.ApproachId);
    }

    [Fact]
    public void Capp_WithDctFix_ActivatesImmediately()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        var hirmoPos = navDb.GetFixPosition("HIRMO");
        Assert.NotNull(hirmoPos);

        var aircraft = MakeAircraft(
            route: "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK",
            destination: "KOAK",
            destinationRunway: "12",
            heading: 320,
            lat: 37.5,
            lon: -121.8
        );
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmoPos.Value.Lat, hirmoPos.Value.Lon) });

        // DCT HIRMO CAPP → immediate (DCT implies leaving the STAR route)
        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, "HIRMO", hirmoPos.Value.Lat, hirmoPos.Value.Lon, null, null);
        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);
        Assert.Null(aircraft.Approach.PendingClearance);
    }

    [Fact]
    public void PendingApproach_ActivatesWhenRouteEmpties()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        var procedure = navDb.GetApproach("KOAK", "H12-Z");
        if (procedure is null)
        {
            output.WriteLine("H12-Z not found at KOAK, skipping");
            return;
        }

        var runway = navDb.GetRunway("KOAK", "12");
        Assert.NotNull(runway);

        var clearance = new ApproachClearance
        {
            ApproachId = "H12-Z",
            AirportCode = "KOAK",
            RunwayId = "12",
            FinalApproachCourse = runway.TrueHeading,
            Procedure = procedure,
        };

        var aircraft = MakeAircraft(destination: "KOAK", heading: 120, lat: 37.73, lon: -122.22);
        aircraft.Approach.PendingClearance = new PendingApproachInfo { Clearance = clearance, AssignedRunway = runway };

        // Simulate route emptying by calling Update with an empty route
        Assert.Empty(aircraft.Targets.NavigationRoute);
        FlightPhysics.Update(aircraft, 1.0);

        // Pending approach should be activated
        Assert.Null(aircraft.Approach.PendingClearance);
        Assert.NotNull(aircraft.Phases);
        Assert.Equal("H12-Z", aircraft.Phases.ActiveApproach?.ApproachId);

        // Should have FinalApproachPhase + LandingPhase
        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType().Name).ToList();
        output.WriteLine($"Phases: {string.Join(" → ", phaseTypes)}");
        Assert.Contains("FinalApproachPhase", phaseTypes);
        Assert.Contains("LandingPhase", phaseTypes);
    }

    [Fact]
    public void SelectBestTransition_RouteContainsTransitionIaf_ReturnsTransition()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO I19L has the UPEND transition. An aircraft with UPEND in its route
        // (not in NavigationRoute — already consumed) should match that transition.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);
        Assert.True(procedure.Transitions.ContainsKey("UPEND"), "I19L should have UPEND transition");

        var upendTransition = procedure.Transitions["UPEND"];
        var legNames = upendTransition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier).ToList();
        output.WriteLine($"UPEND transition legs: {string.Join(" → ", legNames)}");

        // Aircraft route includes UPEND — should match UPEND transition
        var aircraft = MakeAircraft(route: "SJC V334 UPEND");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

        Assert.NotNull(result);
        Assert.Equal("UPEND", result.Name);
    }

    [Fact]
    public void SelectBestTransition_EmptyRouteNoNavRoute_FallsBackToNearestAhead()
    {
        var navDb = GetNavDb();
        if (navDb is null)
        {
            return;
        }

        // SFO I19L with UPEND transition. Aircraft with no route and no NavRoute,
        // heading roughly toward UPEND — fallback should pick nearest transition IAF ahead.
        var procedure = navDb.GetApproach("KSFO", "I19L");
        Assert.NotNull(procedure);

        // Position northeast of SFO, heading southwest — UPEND should be roughly ahead
        var aircraft = MakeAircraft(route: "", heading: 250, lat: 38.0, lon: -122.0);

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

        // Should find some transition via fallback (UPEND is the only one and should be ahead)
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
        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);

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

        // Should include UPEND transition fixes
        Assert.Contains("UPEND", names);

        // Should include common leg fixes (before MAP)
        var commonFixNames = procedure
            .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier) && l.FixRole != CifpFixRole.MAP)
            .Select(l => l.FixIdentifier)
            .ToList();
        foreach (var fix in commonFixNames)
        {
            Assert.Contains(fix, names);
        }

        // MAP (RW19L) should be excluded
        var map = procedure.CommonLegs.FirstOrDefault(l => l.FixRole == CifpFixRole.MAP);
        if (map is not null && !string.IsNullOrEmpty(map.FixIdentifier))
        {
            Assert.DoesNotContain(map.FixIdentifier, names);
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

        // Aircraft with UPEND in route → should select UPEND transition
        var aircraft = MakeAircraft(route: "SJC V334 UPEND", destinationRunway: "19L", heading: 320, lat: 37.5, lon: -122.1);
        var cmd = new ClearedApproachCommand("I19L", "KSFO", false, null, null, null, null, null, null, null, null);

        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);

        output.WriteLine($"CAPP result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var navPhase = aircraft.Phases.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        Assert.NotNull(navPhase);

        var fixNames = navPhase.Fixes.Select(f => f.Name).ToList();
        output.WriteLine($"Approach fixes: {string.Join(" → ", fixNames)}");

        // Should include UPEND (from transition)
        Assert.Contains("UPEND", fixNames);

        // Boundary fix should not be duplicated
        var upendTransition = procedure.Transitions["UPEND"];
        var lastTransitionFix = upendTransition.Legs.LastOrDefault(l => !string.IsNullOrEmpty(l.FixIdentifier))?.FixIdentifier;
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

        var expectedFixes = rwyTransition.Value.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier).ToList();
        output.WriteLine($"Expected fixes: {string.Join(", ", expectedFixes)}");

        var result = ProgrammedFixResolver.Resolve("ALWYS3", null, "KSFO", null, null, "ALWYS3", rwyId);

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

        // Find a fix that only appears in a CIFP runway transition
        // (not in CIFP common legs AND not in the NavData body which is used for route expansion)
        var commonFixNames = star
            .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier))
            .Select(l => l.FixIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // NavData body fixes are always included via route expansion, so exclude them too
        var navDataBody = navDb.GetStarBody("ALWYS3");
        var navDataBodySet = navDataBody is not null
            ? new HashSet<string>(navDataBody, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? rwyOnlyFix = null;
        foreach (var transition in star.RunwayTransitions.Values)
        {
            foreach (var leg in transition.Legs)
            {
                if (
                    !string.IsNullOrEmpty(leg.FixIdentifier)
                    && !commonFixNames.Contains(leg.FixIdentifier)
                    && !navDataBodySet.Contains(leg.FixIdentifier)
                )
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
            output.WriteLine("No runway-transition-only fix found (all fixes also in NavData body or CIFP common legs), skipping");
            return;
        }

        output.WriteLine($"Runway-transition-only fix: {rwyOnlyFix}");

        // Without a runway, runway transitions should NOT be expanded
        var result = ProgrammedFixResolver.Resolve("ALWYS3", null, "KSFO", null, null, "ALWYS3", null);

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
            var rwyFixes = transition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)).Select(l => l.FixIdentifier).ToList();

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
            var result = ProgrammedFixResolver.Resolve(null, approach.ApproachId, "KSFO", null, null, "ALWYS3", null);

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
