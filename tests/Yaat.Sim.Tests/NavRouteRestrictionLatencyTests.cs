using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation;
using OakesHarness = Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness;

namespace Yaat.Sim.Tests;

/// <summary>
/// The nav-route overlay's restriction tokens: <see cref="CrossingRestrictionLabel.BuildTokenLines"/> lays a fix's
/// altitude and speed out as the label lines do, marking the altitude or the speed tokens latent on request,
/// <see cref="NavRouteSpeedLatency.LatentAlongRoute"/> decides which published speeds the overlay dims, fix by fix, and
/// <see cref="AltitudeRestrictionInForce.IsShownLatent"/> decides which published altitudes the overlay dims: those not in
/// force by their source, and a published restriction above a climb via's "except maintain" top or below a descend via's
/// bottom (AIM 5-2-9.h.8 Example 4 NOTE; 7110.65 4-5-7.h.11 Example NOTE).
/// </summary>
public class NavRouteRestrictionLatencyTests
{
    private readonly ITestOutputHelper _output;

    public NavRouteRestrictionLatencyTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // --- BuildTokenLines ---

    [Fact]
    public void BuildTokenLines_SingleAltitudeAndSpeed_OneLineOfTwoTokens()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 6000),
            new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: false,
            speedLatent: false
        );

        Assert.Equal("≥6000 250", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_WindowAndSpeed_ThreeLines()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 17000, 11000),
            new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: false,
            speedLatent: false
        );

        Assert.Equal("≤17000 / ≥11000 / 250", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_SpeedOnly_OneToken()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            null,
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrAbove),
            altitudeLatent: true,
            speedLatent: false
        );

        Assert.Equal("≥210", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_AltitudeLatent_MarksOnlyAltitudeTokens()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> single = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000),
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: true,
            speedLatent: false
        );
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> window = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 19000, 17000),
            new CifpSpeedRestriction(280, CifpSpeedRestrictionType.Mandatory),
            altitudeLatent: true,
            speedLatent: false
        );

        Assert.Equal("~≥8000~ 210", Render(single));
        Assert.Equal("~≤FL190~ / ~≥17000~ / 280", Render(window));
    }

    [Fact]
    public void BuildTokenLines_SpeedLatent_MarksOnlySpeedTokens()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> single = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000),
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: false,
            speedLatent: true
        );
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> window = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 19000, 17000),
            new CifpSpeedRestriction(280, CifpSpeedRestrictionType.Mandatory),
            altitudeLatent: false,
            speedLatent: true
        );

        Assert.Equal("≥8000 ~210~", Render(single));
        Assert.Equal("≤FL190 / ≥17000 / ~280~", Render(window));
    }

    // --- IsShownLatent case table ---

    /// <summary>(a) "DM 110" on OAKES3 with no descend via: the STAR's published altitudes are not in force.</summary>
    [Fact]
    public void IsShownLatent_StarProcedure_AfterDmWithoutDvia_Latent()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DM 110");

        CifpAltitudeRestriction published = FirstProcedureAltitude(harness.Aircraft);
        Assert.True(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, published), $"{published} shown active after DM 110");
    }

    /// <summary>(b) "DVIA" on OAKES3: the STAR's published altitudes bind.</summary>
    [Fact]
    public void IsShownLatent_StarProcedure_AfterDvia_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA");

        CifpAltitudeRestriction published = FirstProcedureAltitude(harness.Aircraft);
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, published), $"{published} shown latent after DVIA");
    }

    /// <summary>(c) "CFIX GRUDG A110" on OAKES3: a controller's crossing is always in force.</summary>
    [Fact]
    public void IsShownLatent_AtcCrossing_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "CFIX GRUDG A110");

        CifpAltitudeRestriction crossing = Assert.IsType<CifpAltitudeRestriction>(
            Assert.Single(harness.Aircraft.Targets.NavigationRoute, f => f.Name == "GRUDG").AltitudeRestriction
        );
        Assert.Equal(CifpAltitudeRestrictionSource.Atc, crossing.Source);
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, crossing), $"{crossing} shown latent");
    }

    /// <summary>
    /// (d) A published H12-Z approach altitude is latent for an aircraft not cleared for the approach, and active once the
    /// aircraft is cleared and past HIRMO, the fix the approach joins.
    /// </summary>
    [Fact]
    public void IsShownLatent_ApproachAltitude_LatentBeforeClearance_ActiveAfter()
    {
        AircraftState cleared = DeferredH12Z();
        CifpAltitudeRestriction approachAltitude = cleared
            .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Approach);
        cleared.Targets.NavigationRoute.RemoveRange(0, cleared.Targets.NavigationRoute.FindIndex(t => t.Name == "HIRMO") + 1);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(ApproachAircraft(), approachAltitude), "shown active before the clearance");
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(cleared, approachAltitude), "shown latent cleared and past HIRMO");
    }

    /// <summary>
    /// (e) "CVIA 180" on CNDEL5: a published floor above the 18,000 top is latent; a floor below it, and a ceiling (no
    /// floor), are active.
    /// </summary>
    [Fact]
    public void IsShownLatent_ClimbViaExceptMaintain_FloorAboveTopLatent()
    {
        AircraftState aircraft = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchLine(aircraft, "CVIA 180");
        Assert.Equal(18000, aircraft.Procedure.SidViaCeiling);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 19000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 15000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrBelow, 23000)));
    }

    /// <summary>
    /// (f) "DVIA 100" on OAKES3: a published ceiling below the 10,000 bottom is latent; a ceiling above it, and a floor (no
    /// ceiling), are active.
    /// </summary>
    [Fact]
    public void IsShownLatent_DescendViaBottom_CeilingBelowBottomLatent()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA 100");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(10000, aircraft.Procedure.StarViaFloor);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrBelow, 9000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.At, 11000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 8000)));
    }

    /// <summary>(g) "DVIA 100" on OAKES3: a published 11,000/9,000 window's ceiling is not below the bottom, so it is active.</summary>
    [Fact]
    public void IsShownLatent_DescendViaBottom_WindowCeilingNotBelowBottom_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA 100");
        Assert.Equal(10000, harness.Aircraft.Procedure.StarViaFloor);

        Assert.False(
            AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 11000, 9000))
        );
    }

    // --- LatentAlongRoute case table: OAKES3's published speeds, fix by fix ---
    //
    // B = the published speeds before the end fix E, E = the first fix with no published speed between two that have one
    // (found on the route at run time and written to the test output), which a "CFIX E 160 230" makes controller-set,
    // A = the published speeds after it. A row with no end point judges every published speed alike.

    /// <summary>On the procedure with no ATC speed and no DSR every published speed binds (7110.65 5-7-2.e NOTE 1).</summary>
    [Fact]
    public void LatentAlongRoute_NoAtcSpeed_AllActive() => AssertRow("none", before: false, after: false);

    /// <summary>A descend via changes nothing: published speeds bind with or without it (7110.65 5-7-2.e NOTE 1).</summary>
    [Fact]
    public void LatentAlongRoute_DescendVia_AllActive() => AssertRow("dvia", before: false, after: false);

    /// <summary>An open-ended "SPD 210" supersedes every published speed (7110.65 5-7-2.e Ex 2 NOTE).</summary>
    [Fact]
    public void LatentAlongRoute_OpenEndedIas_AllLatent() => AssertRow("spd", before: true, after: true);

    /// <summary>An assigned Mach is an ATC speed and supersedes them as an IAS does (7110.65 5-7-1.g, 5-7-2.a).</summary>
    [Fact]
    public void LatentAlongRoute_OpenEndedAtcMach_AllLatent() => AssertRow("mach", before: true, after: true);

    /// <summary>A cruise Mach nobody assigned is no ATC modification: published speeds bind (AIM 5-4-1.a.1).</summary>
    [Fact]
    public void LatentAlongRoute_UnassignedCruiseMach_AllActive() => AssertRow("cruise-mach", before: false, after: false);

    /// <summary>An assigned Mach handed over as an IAS below the crossover is still the controller's assignment.</summary>
    [Fact]
    public void LatentAlongRoute_AtcMachHandedOverAsIas_AllLatent() => AssertRow("mach-as-ias", before: true, after: true);

    /// <summary>An open-ended SMAX is an ATC speed instruction (7110.65 5-7-2.a.3).</summary>
    [Fact]
    public void LatentAlongRoute_OpenEndedSpeedMax_AllLatent() => AssertRow("smax", before: true, after: true);

    /// <summary>An open-ended bound ("SPD 280+") is an ATC speed and supersedes them.</summary>
    [Fact]
    public void LatentAlongRoute_OpenEndedBound_AllLatent() => AssertRow("bound", before: true, after: true);

    /// <summary>An assigned speed until E, then "resume published speed": latent before E, active after (5-7-2.e Ex 3).</summary>
    [Fact]
    public void LatentAlongRoute_AssignedUntilEndThenResumePublished_ActiveAfter() => AssertRow("until-rps", before: true, after: false);

    /// <summary>An assigned speed until E, then "resume normal speed": RNS deletes no published speed (7110.65 5-7-4.a NOTE).</summary>
    [Fact]
    public void LatentAlongRoute_AssignedUntilEndThenResumeNormal_ActiveAfter() => AssertRow("until-rns", before: true, after: false);

    /// <summary>The same RNS with DSR already in force: the published speeds after E stay deleted.</summary>
    [Fact]
    public void LatentAlongRoute_AssignedUntilEndThenResumeNormal_UnderDsr_LatentAfter() => AssertRow("until-rns-dsr", before: true, after: true);

    /// <summary>An assigned speed until E, then "delete speed restrictions": latent on both sides of E (7110.65 5-7-4.d).</summary>
    [Fact]
    public void LatentAlongRoute_AssignedUntilEndThenDeleteSpeedRestrictions_LatentAfter() => AssertRow("until-dsr", before: true, after: true);

    /// <summary>A crossing restated under SMAX re-arms SMAX past E: latent on both sides.</summary>
    [Fact]
    public void LatentAlongRoute_CrossingUnderSpeedMax_LatentAfter() => AssertRow("cfix-under-smax", before: true, after: true);

    /// <summary>A crossing under an assigned Mach brings the assigned Mach back past E: latent on both sides.</summary>
    [Fact]
    public void LatentAlongRoute_CrossingUnderAtcMach_LatentAfter() => AssertRow("cfix-under-mach", before: true, after: true);

    /// <summary>A crossing under a Mach nobody assigned brings no assignment back: active on both sides (AIM 5-4-1.a.1).</summary>
    [Fact]
    public void LatentAlongRoute_CrossingUnderUnassignedMach_AllActive() => AssertRow("cfix-under-cruise-mach", before: false, after: false);

    /// <summary>A crossing under an earlier "SPD 210" restores that assignment past E: latent on both sides.</summary>
    [Fact]
    public void LatentAlongRoute_CrossingUnderEarlierSpeed_LatentAfter() => AssertRow("cfix-under-spd", before: true, after: true);

    /// <summary>A bare crossing speed with no ATC speed in force bounds the speed at E only (AIM 5-4-1.a.1).</summary>
    [Fact]
    public void LatentAlongRoute_BareCrossing_AllActive() => AssertRow("bare-cfix", before: false, after: false);

    /// <summary>"DSR" deletes every published speed (7110.65 5-7-4.d).</summary>
    [Fact]
    public void LatentAlongRoute_DeleteSpeedRestrictions_AllLatent() => AssertRow("dsr", before: true, after: true);

    /// <summary>"RPS" after "DSR" gives the published speeds back (7110.65 5-7-4.c; AIM 4-4-12.f.3).</summary>
    [Fact]
    public void LatentAlongRoute_ResumePublishedAfterDsr_AllActive() => AssertRow("rps-after-dsr", before: false, after: false);

    /// <summary>A descend via after "SPD 210", the speed not restated, cancels the assignment (7110.65 5-7-1.d).</summary>
    [Fact]
    public void LatentAlongRoute_NewDescendViaAfterAssignment_AllActive() => AssertRow("via-after-spd", before: false, after: false);

    /// <summary>A controller's crossing speed is active in any state, here under DSR, around published speeds that are not.</summary>
    [Fact]
    public void LatentAlongRoute_ControllerCrossingUnderDsr_Active() => AssertRow("dsr-cfix", before: true, after: true);

    /// <summary>
    /// The overlay and the physics judge published speeds with one rule: in every case-table state the walk's verdict for
    /// the next published speed is latent exactly when
    /// <see cref="FlightPhysics.ApplyFixConstraints(AircraftState, NavigationTarget)"/> does not apply it; and where the
    /// state has an end fix E, the same holds for the first published speed after E once
    /// <see cref="FlightPhysics.RestoreSpeedPastFix"/> has sequenced E, so the walk's state past E is the physics' own.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpeedStateNames))]
    public void LatentAlongRoute_NextSpeedFix_AgreesWithThePhysics(string state)
    {
        if (SpeedState(state) is not { } row)
        {
            return;
        }

        AircraftState aircraft = row.Harness.Aircraft;
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int next = route.FindIndex(f => f.SpeedRestriction is not null);
        bool latent = NavRouteSpeedLatency.LatentAlongRoute(aircraft, route)[next];
        bool applied = PhysicsApplies(aircraft, route[next]);
        Assert.True(latent != applied, $"{state}: {route[next].Name} shown {(latent ? "latent" : "active")}, physics applied it: {applied}");

        if ((row.End is not null) && (SpeedState(state) is { } past))
        {
            AssertAgreesPastEnd(state, past.Harness.Aircraft, row.End);
        }
    }

    /// <summary>
    /// On a fresh copy of the state: sequences the aircraft past E as the physics does, then compares the walk's verdict
    /// for the first published speed after E, taken before E was sequenced, with whether the physics applies it.
    /// </summary>
    private static void AssertAgreesPastEnd(string state, AircraftState aircraft, string end)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        bool[] latent = NavRouteSpeedLatency.LatentAlongRoute(aircraft, route);
        int endIndex = route.FindIndex(f => f.Name == end);
        int afterIndex = route.FindIndex(endIndex + 1, f => f.SpeedRestriction is not null);
        Assert.True((endIndex >= 0) && (afterIndex > endIndex), $"{state}: no published speed after {end}");
        NavigationTarget endFix = route[endIndex];
        NavigationTarget afterFix = route[afterIndex];

        route.RemoveRange(0, endIndex + 1);
        FlightPhysics.RestoreSpeedPastFix(aircraft, endFix, route);
        bool applied = PhysicsApplies(aircraft, afterFix);

        Assert.True(
            latent[afterIndex] != applied,
            $"{state}: past {end}, {afterFix.Name} shown {(latent[afterIndex] ? "latent" : "active")}, physics applied it: {applied}"
        );
    }

    /// <summary>Whether <see cref="FlightPhysics.ApplyFixConstraints(AircraftState, NavigationTarget)"/> applies <paramref name="fix"/>'s speed.</summary>
    private static bool PhysicsApplies(AircraftState aircraft, NavigationTarget fix)
    {
        aircraft.Procedure.LastProcedureSpeedKts = null;
        aircraft.Targets.SpeedFloor = null;
        FlightPhysics.ApplyFixConstraints(aircraft, fix);
        return (aircraft.Procedure.LastProcedureSpeedKts is not null) || (aircraft.Targets.SpeedFloor is not null);
    }

    public static TheoryData<string> SpeedStateNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in SpeedStates.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Each case-table state, set up on the joined OAKES3 jet: returns the end fix E, or null for a state without one.</summary>
    private static readonly Dictionary<string, Func<OakesHarness, string, string?>> SpeedStates = new()
    {
        ["none"] = (_, _) => null,
        ["dvia"] = (h, _) => Send(h, null, "DVIA"),
        ["spd"] = (h, _) => Send(h, null, "SPD 210"),
        ["mach"] = (h, _) => AssignedMach(h),
        ["cruise-mach"] = (h, _) => UnassignedMach(h),
        ["mach-as-ias"] = (h, _) => AssignedMachHandedOver(h),
        ["smax"] = (h, _) => Send(h, null, "SMAX"),
        ["bound"] = (h, _) => Send(h, null, "SPD 280+"),
        ["until-rps"] = (h, end) => AssignedUntil(h, end, SpeedResumeKind.Published),
        ["until-rns"] = (h, end) => AssignedUntil(h, end, SpeedResumeKind.Normal),
        ["until-rns-dsr"] = (h, end) => AssignedUntilUnderDsr(h, end),
        ["until-dsr"] = (h, end) => AssignedUntil(h, end, SpeedResumeKind.Deleted),
        ["cfix-under-smax"] = (h, end) => CrossingUnder(h, end, "SMAX", f => f.RevertSpeedMax),
        ["cfix-under-mach"] = (h, end) => CrossingUnder(h, end, "MACH 62", f => (f.RevertMach is not null) && (f.RevertMachExplicit != false)),
        ["cfix-under-cruise-mach"] = (h, end) => UnassignedMachCrossing(h, end),
        ["cfix-under-spd"] = (h, end) => CrossingUnder(h, end, "SPD 210", NavigationCommandHandler.HasPlainRevert),
        ["bare-cfix"] = (h, end) => Send(h, end, Crossing(end)),
        ["dsr"] = (h, _) => Send(h, null, "DSR"),
        ["rps-after-dsr"] = (h, _) => Send(h, null, "DSR", "RPS"),
        ["via-after-spd"] = (h, _) => Send(h, null, "SPD 210", "DVIA"),
        ["dsr-cfix"] = (h, end) => Send(h, end, "DSR", Crossing(end)),
    };

    private void AssertRow(string state, bool before, bool after)
    {
        if (SpeedState(state) is not { } row)
        {
            return;
        }

        AssertSpeedVerdicts(row.Harness.Aircraft, row.End, before, after);
    }

    /// <summary>The joined OAKES3 jet put in <paramref name="state"/>, with its end fix E (the second published speed ahead).</summary>
    private (OakesHarness Harness, string? End)? SpeedState(string state)
    {
        if (OakesJoined() is not { } harness)
        {
            return null;
        }

        List<NavigationTarget> route = harness.Aircraft.Targets.NavigationRoute;
        _output.WriteLine($"Route: {string.Join(" ", route.Select(f => $"{f.Name}{(f.SpeedRestriction is { } s ? $"/{s.SpeedKts}" : "")}"))}");
        int first = route.FindIndex(f => f.SpeedRestriction is not null);
        int end = route.FindIndex(first + 1, f => f.SpeedRestriction is null);
        int last = route.FindLastIndex(f => f.SpeedRestriction is not null);
        Assert.True((first >= 0) && (end > first) && (last > end), "OAKES3 needs a fix with no published speed between two that have one");
        _output.WriteLine($"B={route[first].Name} E={route[end].Name} A={route[last].Name}");
        return (harness, SpeedStates[state](harness, route[end].Name));
    }

    /// <summary>
    /// Asserts the walk's verdict for every route fix: a fix with no speed is never latent, E is active, the published
    /// speeds before E are <paramref name="before"/> and after it <paramref name="after"/>; with no E every published
    /// speed is <paramref name="before"/>.
    /// </summary>
    private void AssertSpeedVerdicts(AircraftState aircraft, string? end, bool before, bool after)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        bool[] latent = NavRouteSpeedLatency.LatentAlongRoute(aircraft, route);
        Assert.Equal(route.Count, latent.Length);
        int endIndex = end is null ? -1 : route.FindIndex(f => f.Name == end);
        Assert.True((end is null) || (endIndex >= 0), $"{end} is not on the route");

        var expected = new List<string>(route.Count);
        var actual = new List<string>(route.Count);
        for (int i = 0; i < route.Count; i++)
        {
            bool sideLatent = ((endIndex < 0) || (i < endIndex)) ? before : after;
            expected.Add($"{route[i].Name}:{ExpectedMark(route[i], i == endIndex, sideLatent)}");
            actual.Add($"{route[i].Name}:{ActualMark(route[i], latent[i])}");
        }

        _output.WriteLine($"explicit={aircraft.Targets.HasExplicitSpeedCommand} dsr={aircraft.Procedure.SpeedRestrictionsDeleted}");
        Assert.Equal(string.Join(" ", expected), string.Join(" ", actual));
    }

    /// <summary>"-" for a fix with no speed, "A" for E and an active speed, "L" for a latent one.</summary>
    private static string ExpectedMark(NavigationTarget fix, bool isEnd, bool sideLatent) =>
        (fix.SpeedRestriction is null) ? "-"
        : isEnd ? "A"
        : (sideLatent ? "L" : "A");

    /// <summary>The walk's mark for a fix, "L!" where it marks a fix with no speed latent.</summary>
    private static string ActualMark(NavigationTarget fix, bool latent) =>
        (fix.SpeedRestriction is null) ? (latent ? "L!" : "-") : (latent ? "L" : "A");

    private static string Crossing(string end) => $"CFIX {end} 160 230";

    private static string? Send(OakesHarness harness, string? end, params string[] commands)
    {
        foreach (string command in commands)
        {
            SendOk(harness, command);
        }

        return end;
    }

    private static string? AssignedMach(OakesHarness harness)
    {
        SendOk(harness, "MACH 62");
        Assert.NotNull(harness.Aircraft.Targets.TargetMach);
        Assert.True(harness.Aircraft.Targets.HasExplicitSpeedCommand);
        return null;
    }

    /// <summary>A cruise Mach nobody assigned, as live traffic is seeded with its present Mach.</summary>
    private static string? UnassignedMach(OakesHarness harness)
    {
        harness.Aircraft.Targets.TargetMach = 0.62;
        harness.Aircraft.Targets.HasExplicitSpeedCommand = false;
        return null;
    }

    /// <summary>"MACH 62" below the crossover, ticked until the Mach is handed over as an IAS.</summary>
    private static string? AssignedMachHandedOver(OakesHarness harness)
    {
        AssignedMach(harness);
        for (int t = 1; harness.Aircraft.Targets.TargetMach is not null; t++)
        {
            Assert.True(t <= 30, "The Mach was not handed over within 30 s");
            harness.Engine.TickOneSecond();
        }

        Assert.True(harness.Aircraft.Targets.HasExplicitSpeedCommand);
        return null;
    }

    // AssignedUntil and AssignedUntilUnderDsr (rows until-rps, until-rns, until-rns-dsr, until-dsr) build a state no command
    // sequence produces today: a resume given with a crossing applies at once (NavigationCommandHandler.CancelEarlierCrossingSpeeds),
    // so the assignment is never still in force before E. They test the state model, the walk's reading of a resume on E;
    // whether the resume belongs at E is YAAT-1920's to rule.

    /// <summary>
    /// "SPD 210", then a crossing at E carrying the speed's end instruction: the resume on E with the assignment still in
    /// force before it.
    /// </summary>
    private static string AssignedUntil(OakesHarness harness, string end, SpeedResumeKind resume)
    {
        Send(harness, end, "SPD 210", Crossing(end));
        NavigationCommandHandler.PlaceResumeRevert(harness.Aircraft.Targets.NavigationRoute, resume);
        Assert.True(harness.Aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Equal(resume, Assert.Single(harness.Aircraft.Targets.NavigationRoute, f => f.Name == end).RevertResume);
        return end;
    }

    private static string AssignedUntilUnderDsr(OakesHarness harness, string end)
    {
        AssignedUntil(harness, end, SpeedResumeKind.Normal);
        harness.Aircraft.Procedure.SpeedRestrictionsDeleted = true;
        return end;
    }

    /// <summary><paramref name="speed"/>, then a crossing at E, which leaves on E the revert <paramref name="revert"/> tests.</summary>
    private static string CrossingUnder(OakesHarness harness, string end, string speed, Func<NavigationTarget, bool> revert)
    {
        Send(harness, end, speed, Crossing(end));
        NavigationTarget fix = Assert.Single(harness.Aircraft.Targets.NavigationRoute, f => f.Name == end);
        Assert.True(revert(fix), $"{end} carries no revert of the expected kind after \"{speed}\"");
        return end;
    }

    private static string UnassignedMachCrossing(OakesHarness harness, string end)
    {
        UnassignedMach(harness);
        Send(harness, end, Crossing(end));
        NavigationTarget fix = Assert.Single(harness.Aircraft.Targets.NavigationRoute, f => f.Name == end);
        Assert.NotNull(fix.RevertMach);
        Assert.False(fix.RevertMachExplicit);
        return end;
    }

    private static CifpAltitudeRestriction Published(CifpAltitudeRestrictionType type, int altitudeFt) => new(type, altitudeFt);

    /// <summary>Renders token lines as "a b / c": lines split by " / ", tokens by a space, a latent token as "~text~".</summary>
    private static string Render(IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines) =>
        string.Join(" / ", lines.Select(line => string.Join(" ", line.Select(t => t.Latent ? $"~{t.Text}~" : t.Text))));

    private static CifpAltitudeRestriction FirstProcedureAltitude(AircraftState aircraft) =>
        aircraft
            .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Procedure);

    /// <summary>A jet on OAKES3 past its entry fix, re-joined by "JARR OAKES3", as the source tests build it.</summary>
    private DescendViaSupersedesEarlierAltitudeTests.Harness? OakesJoined()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return null;
        }

        string first = harness.Aircraft.Targets.NavigationRoute[0].Name;
        for (int t = 1; harness.Aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(first, StringComparison.OrdinalIgnoreCase)); t++)
        {
            Assert.True(t <= 1800, $"The aircraft did not pass {first} within 1800 s");
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "JARR OAKES3");
        return harness;
    }

    /// <summary>A jet on EMZOH4 with EMZOH and HIRMO ahead, cleared H12-Z, which defers to HIRMO.</summary>
    private static AircraftState DeferredH12Z()
    {
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        (double Lat, double Lon) emzoh = Assert.IsType<(double, double)>(navDb.GetFixPosition("EMZOH"));
        (double Lat, double Lon) hirmo = Assert.IsType<(double, double)>(navDb.GetFixPosition("HIRMO"));

        AircraftState aircraft = ApproachAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzoh.Lat, emzoh.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmo.Lat, hirmo.Lon) });
        aircraft.Approach.Expected = "H12-Z";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Approach.PendingClearance);
        return aircraft;
    }

    private static AircraftState ApproachAircraft() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(320),
            Altitude = 5000,
            Position = new LatLon(37.5, -121.8),
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", Route = "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK" },
            Procedure = new AircraftProcedure { DestinationRunway = "12" },
        };

    private static void SendOk(DescendViaSupersedesEarlierAltitudeTests.Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void DispatchLine(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
