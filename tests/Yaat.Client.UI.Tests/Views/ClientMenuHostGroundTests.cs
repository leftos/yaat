using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The client menu host's ground members — ground traffic, hold short, route preview, pushback and preset taxi — which
/// answer from the primary ground view model on the real KOAK layout, and the builder's hold-short and follow items built
/// over them. The display items the menu shows are the view's own (see <c>CanvasMenuItemsTests</c>), so the host serves
/// none of them.
/// </summary>
public class ClientMenuHostGroundTests
{
    private const string Callsign = "UAL100";
    private const string OtherCallsign = "SWA200";

    // --- Ground traffic, hold short and route preview ----------------------------------------

    [AvaloniaFact]
    public void GetGroundTrafficRows_ExcludesSelfAndAirborne_NearestFirst_Uncapped()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel self = GroundAircraft(Callsign, "At Parking", at);
        AircraftModel airborne = GroundAircraft("AAL999", "ApproachNav", new LatLon(at.Lat + 0.0005, at.Lon));
        airborne.IsOnGround = false;
        // Added farthest first, so the order must come from the distance, not the list.
        AircraftModel[] traffic =
        [
            .. Enumerable.Range(1, 14).Reverse().Select(i => GroundAircraft($"SWA{i:000}", "Taxiing", new LatLon(at.Lat + (i * 0.001), at.Lon))),
        ];
        MainViewModel main = MainWith(target, [self, airborne, .. traffic]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(Enumerable.Range(1, 14).Select(i => $"SWA{i:000}"), host.GetGroundTrafficRows(Callsign).Select(row => row.Callsign));
    }

    /// <summary>
    /// YAAT-447: delayed spawns parked nearer than a real aircraft pushing back are not in the sim yet, so the list leaves
    /// them out and the pushing-back aircraft is not cut.
    /// </summary>
    [AvaloniaFact]
    public void GetGroundTrafficRows_ExcludesDelayedSpawns()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel[] delayed = [.. Enumerable.Range(1, 12).Select(i => DelayedSpawn($"DLY{i:00}", new LatLon(at.Lat + (i * 0.0001), at.Lon)))];
        AircraftModel pushingBack = GroundAircraft("SWA1182", "Pushback", new LatLon(at.Lat + 0.002, at.Lon));
        MainViewModel main = MainWith(target, [.. delayed, pushingBack]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(["SWA1182"], host.GetGroundTrafficRows(Callsign).Select(row => row.Callsign));
    }

    [AvaloniaFact]
    public void GetGroundTrafficRows_SplitsMovingFromParkedByPhaseAndSpeed()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel pushingBack = GroundAircraft("PSH1", "Pushback", new LatLon(at.Lat + 0.001, at.Lon));
        AircraftModel taxiing = GroundAircraft("TXI1", "Taxiing", new LatLon(at.Lat + 0.002, at.Lon));
        AircraftModel rolling = GroundAircraft("ROL1", "Runway Exit", new LatLon(at.Lat + 0.003, at.Lon));
        rolling.GroundSpeed = 12;
        AircraftModel parked = GroundAircraft("PRK1", "At Parking", new LatLon(at.Lat + 0.004, at.Lon));
        AircraftModel creeping = GroundAircraft("HLD1", "Holding In Position", new LatLon(at.Lat + 0.005, at.Lon));
        creeping.GroundSpeed = 1;
        MainViewModel main = MainWith(target, [creeping, parked, rolling, taxiing, pushingBack]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(
            [("PSH1", true), ("TXI1", true), ("ROL1", true), ("PRK1", false), ("HLD1", false)],
            host.GetGroundTrafficRows(Callsign).Select(row => (row.Callsign, row.IsMoving))
        );
    }

    [AvaloniaFact]
    public void GetGroundTrafficRows_FlagsSurfaceShadows()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel shadow = GroundAircraft("SHD1", "Taxiing", new LatLon(at.Lat + 0.001, at.Lon));
        shadow.IsLiveTraffic = true;
        AircraftModel inSim = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(at.Lat + 0.002, at.Lon));
        MainViewModel main = MainWith(target, [shadow, inSim]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(
            [("SHD1", true), (OtherCallsign, false)],
            host.GetGroundTrafficRows(Callsign).Select(row => (row.Callsign, row.IsSurfaceShadow))
        );
    }

    [AvaloniaFact]
    public void IsOnTaxiRoute_TheHoldShortAheadIsOnTheRoute_AStandIsNot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        AircraftModel holding = GroundAircraft(OtherCallsign, $"Holding Short {Runway30HoldShortNode.RunwayId}", PositionOf(Runway30HoldShortNode));
        AircraftModel parked = GroundAircraft("SWA300", "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, [holding, parked]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.True(host.IsOnTaxiRoute(Callsign, OtherCallsign));
        Assert.False(host.IsOnTaxiRoute(Callsign, "SWA300"));
        Assert.False(host.IsOnTaxiRoute(OtherCallsign, Callsign));
        Assert.False(host.IsOnTaxiRoute("NOPE", OtherCallsign));
    }

    [AvaloniaFact]
    public void HighlightAircraft_SetsReplacesAndClears_KeepingAHandHighlight()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        GroundDataBlockViewState state = main.Ground.DataBlockState;
        state.ToggleHighlight("KEEP1");
        int changes = 0;
        state.HighlightsChanged += () => changes++;

        host.HighlightAircraft("SWA1");
        Assert.Equal(["KEEP1", "SWA1"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft("SWA2");
        Assert.Equal(["KEEP1", "SWA2"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft("KEEP1");
        Assert.Equal(["KEEP1"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft(null);
        Assert.Equal(["KEEP1"], state.HighlightedCallsigns.Order());

        // Four calls, three changes: the clear removes nothing (the menu never set KEEP1 as its own highlight), so it raises no event.
        Assert.Equal(3, changes);
    }

    private static AircraftModel DelayedSpawn(string callsign, LatLon position)
    {
        AircraftModel ac = GroundAircraft(callsign, "At Parking", position);
        ac.Status = "Delayed (40:21)";
        return ac;
    }

    [AvaloniaFact]
    public void GroundMembers_AircraftNotInTheList_AnswerNothing()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());

        Assert.Empty(host.GetGroundTrafficRows(Callsign));
        Assert.Equal(HoldShortMenu.Empty, host.GetHoldShortChoices(Callsign));
        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        Assert.Empty(host.GetPushbackToChoices(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));

        host.EnterPushRoute(Callsign);
        Assert.False(main.Ground.IsDrawingRoute);
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_SendHSWithThePreviewRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        HoldShortMenu holdShort = host.GetHoldShortChoices(Callsign);

        // W3's bar is mid-way along 12/30 and the room names no active runway: one row naming both ends, sending the
        // lower-numbered one, which binds the same bar.
        Assert.Equal("route W3 · RWY 30", holdShort.RouteLine);
        HoldShortChoice runway = Assert.Single(holdShort.Rows);
        Assert.Equal((HoldShortChoice.RunwayBadge, "Runway 12/30", "HS 12"), (runway.Label.Badge, runway.Label.Name, runway.Command));
        Assert.Equal(Runway30HoldShortNode.Id, runway.Preview.Segments[^1].ToNodeId);
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_ActiveEnd_NamesTheRunwayRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        HoldShortChoice runway = Assert.Single(host.GetHoldShortChoices(Callsign).Rows);

        Assert.Equal(("Runway 30", "HS 30"), (runway.Label.Name, runway.Command));
    }

    [AvaloniaFact]
    public void SetRoutePreview_SetsAndClearsTheGroundPreview()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        host.SetRoutePreview(route);
        Assert.Same(route, main.Ground.PreviewRoute);

        host.SetRoutePreview(null);
        Assert.Null(main.Ground.PreviewRoute);
    }

    // --- Pushback faces, push back to, push route and preset taxi routes ---------------------

    [AvaloniaFact]
    public void GetPushbackFaceChoices_AtSpotI30_SendPushFaceForEachNonRampTaxiway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetPushbackFaceChoices(Callsign);

        List<(string Label, string Cardinal)> directions = main.Ground.GetPushbackDirections(target);
        Assert.NotEmpty(directions);
        Assert.Equal(
            directions.Select(d => ($"Push back, {d.Label}", (string?)$"PUSH FACE {d.Cardinal}")),
            choices.Select(c => (c.Label, c.Command))
        );
        Assert.All(choices, c => Assert.StartsWith("Push back, face ", c.Label));
        Assert.All(choices, c => Assert.Null(c.Preview));
    }

    [AvaloniaFact]
    public void GetPushbackToChoices_AtSpotI30_ThirtyNearestStands_SpotUsesDollarParkingUsesAt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetPushbackToChoices(Callsign);

        Assert.Equal(30, choices.Count);
        Assert.DoesNotContain(choices, c => c.Label == "I30");
        Assert.Contains(choices, c => (c.Label == "1") && (c.Command == "PUSH $1"));
        Assert.Contains(choices, c => (c.Label == "32") && (c.Command == "PUSH @32"));
        Assert.All(choices, c => Assert.Null(c.Preview));

        List<double> distances = [.. choices.Select(c => DistanceToStand(target, c.Label))];
        Assert.Equal(distances.Order(), distances);
    }

    [AvaloniaFact]
    public void GetPresetTaxiChoices_FromTheOakSidecar_KeepTheWalkableRouteWithItsViaDistanceAndPreview_AndDropTheOther()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        target.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<TaxiRouteRow> rows = host.GetPresetTaxiChoices(Callsign);

        TaxiRouteRow terminal = Assert.Single(rows, r => r.Name == "TERMINAL to 30");
        Assert.Equal(
            (TaxiRouteRow.PresetBadge, (string?)null, "via T U W", "TAXI T U W RWY 30"),
            (terminal.Badge, terminal.Reason, terminal.Via, terminal.Command)
        );
        Assert.Empty(terminal.Variants);

        // The preview is the resolved path, ending at a runway 30 hold short; the aircraft stands on the node it starts
        // at, so the distance is the path's own length, rounded to 50 ft.
        Assert.NotEmpty(terminal.Preview.Segments);
        GroundNodeDto end = Oak.Nodes.First(n => n.Id == terminal.Preview.Segments[^1].ToNodeId);
        Assert.Equal("RunwayHoldShort", end.Type);
        Assert.Contains("30", end.RunwayId);
        Assert.True(terminal.DistanceFt > 0, $"distance {terminal.DistanceFt}");
        Assert.Equal(TugMovePlanner.NoteDistanceFt(terminal.Preview.PrefixDistanceFt(terminal.Preview.Segments.Count)), terminal.DistanceFt);

        Assert.DoesNotContain(rows, r => r.Name == "30 to TERMINAL");
    }

    // --- Taxi to runway --------------------------------------------------------------------

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_AtAStand_ActiveDepartureEndsInline_NearestAndFullLength_RestUnderOther()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["D30", "28L"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        Assert.Equal(["Runway 30 · departure runway", "Runway 28L · departure runway"], menu.Inline.Select(g => g.Title));
        // The other ends are searched only when Other runways opens.
        Assert.Empty(menu.Other);
        int searchesBeforeOther = main.Ground.TaxiToRunwayRouteSearches;
        IReadOnlyList<TaxiToRunwayGroup> other = Assert.IsType<Func<IReadOnlyList<TaxiToRunwayGroup>>>(menu.FindOther)();
        Assert.True(main.Ground.TaxiToRunwayRouteSearches > searchesBeforeOther, "Other runways searched nothing when it opened");
        Assert.DoesNotContain(other, g => g.Title is "Runway 30" or "Runway 28L");
        Assert.Contains(other, g => g.Title == "Runway 28R");

        // Runway 30 from gate 25: the full-length entry at W1 first, then the intersections, one of them the nearest, each
        // opening For departure and Hold short of runway 30 and previewing its route to a runway 30 hold short.
        List<TaxiRouteRow> entries = EntryRows(menu.Inline[0]);
        Assert.Equal(("At W1", "full length"), (entries[0].Name, entries[0].Reason));
        TaxiRouteRow nearest = Assert.Single(entries, r => r.Reason == "nearest");
        Assert.True(nearest.DistanceFt < entries[0].DistanceFt, $"{nearest.DistanceFt} vs {entries[0].DistanceFt}");
        Assert.All(entries, AssertEntryTo("30"));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NearestEntryIsTheFullLengthOne_OneRowNamesBoth()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(NodeBehind(HoldShort30On("W1"), "W1")));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayGroup runway30 = host.GetTaxiToRunwayChoices(Callsign).Inline[0];

        Assert.Equal("Runway 30 · departure runway", runway30.Title);
        List<TaxiRouteRow> entries = EntryRows(runway30);
        Assert.Equal(("At W1", "nearest, full length"), (entries[0].Name, entries[0].Reason));
        Assert.True(entries.Count > 1, "no intersection entry after the full-length one");
        Assert.All(entries.Skip(1), r => Assert.Null(r.Reason));
    }

    /// <summary>
    /// A B738 (7,545 ft takeoff distance) at a KOAK gate: W3 joins 12/30 about 2,900 ft from the 30 threshold and W2 about
    /// 500 ft from it, too little runway ahead for a departure on 12, so neither is offered for 12; both are for 30.
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KoakTwelve_DropsShortIntersectionEntries()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30", "12"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        Assert.Equal(["Runway 30 · departure runway", "Runway 12 · departure runway"], menu.Inline.Select(g => g.Title));
        List<string> twelve = [.. EntryRows(menu.Inline[1]).Select(r => r.Name)];
        Assert.NotEmpty(twelve);
        Assert.DoesNotContain("At W3", twelve);
        Assert.DoesNotContain("At W2", twelve);
        Assert.Contains("At W2", EntryRows(menu.Inline[0]).Select(r => r.Name));
        TaxiRouteRow w3 = Assert.Single(EntryRows(menu.Inline[0]), r => r.Name == "At W3");
        Assert.True(w3.AvailableFt >= 7545, $"W3 leaves {w3.AvailableFt} ft of runway 30");
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_EntriesOrderedFromTheThreshold_FullLengthFirst()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        List<TaxiRouteRow> entries = EntryRows(host.GetTaxiToRunwayChoices(Callsign).Inline[0]);

        Assert.True(entries.Count >= 3, $"{entries.Count} entries");
        Assert.Equal(("At W1", "full length"), (entries[0].Name, entries[0].Reason));
        Assert.DoesNotContain(entries.Skip(1), r => r.Reason?.Contains("full length", StringComparison.Ordinal) == true);
        Assert.Null(entries[0].AvailableFt);
        Assert.All(entries.Skip(1), r => Assert.NotNull(r.AvailableFt));
        List<double> available = [.. entries.Skip(1).Select(r => r.AvailableFt!.Value)];
        Assert.Equal(available.OrderDescending(), available);
        // The full-length row and the nearest one are highlighted, and only they.
        Assert.Equal(2, entries.Count(r => r.IsHighlighted));
        Assert.Equal(entries.Where(r => r.Reason is not null).Select(r => r.Name), entries.Where(r => r.IsHighlighted).Select(r => r.Name));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_RemainingLengthRoundsDownTo50Ft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiRouteRow w3 = Assert.Single(EntryRows(host.GetTaxiToRunwayChoices(Callsign).Inline[0]), r => r.Name == "At W3");

        double? remainingFt = main.Ground.RunwayRemainingFt("30", w3.Preview.Segments[^1].ToNodeId);
        Assert.NotNull(remainingFt);
        Assert.Equal(Math.Floor(remainingFt.Value / 50) * 50, w3.AvailableFt);
        Assert.Equal(7600, GroundViewModel.AvailableRunwayFt(7649.9));
        Assert.Equal(7650, GroundViewModel.AvailableRunwayFt(7650));
        Assert.Equal(7600, GroundViewModel.AvailableRunwayFt(7600.1));
    }

    /// <summary>
    /// From a KOAK gate, W3 leaves about 2,900 ft of runway 12 ahead: enough for a C172 (984 ft takeoff distance), not for a
    /// B738 (7,545 ft).
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_C172_KeepsAnIntersectionTheJetCannotUse()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel jet = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        jet.AssignedRunway = "12";
        AircraftModel cessna = GroundAircraft(OtherCallsign, "At Parking", PositionOf(Gate25Node));
        cessna.AircraftType = "C172";
        cessna.AssignedRunway = "12";

        TaxiToRunwayGroup jet12 = new ClientMenuHost(OakMain(jet, []), jet, new Border()).GetTaxiToRunwayChoices(Callsign).Inline[0];
        TaxiToRunwayGroup cessna12 = new ClientMenuHost(OakMain(cessna, []), cessna, new Border()).GetTaxiToRunwayChoices(OtherCallsign).Inline[0];

        Assert.Equal(("Runway 12 · assigned runway", "Runway 12 · assigned runway"), (jet12.Title, cessna12.Title));
        Assert.DoesNotContain("At W3", EntryRows(jet12).Select(r => r.Name));
        TaxiRouteRow w3 = Assert.Single(EntryRows(cessna12), r => r.Name == "At W3");
        Assert.InRange(w3.AvailableFt!.Value, 984, 7545);
    }

    /// <summary>
    /// An MD81 carries no takeoff distance in its profile, so an intersection needs half the runway ahead: from a KOAK gate
    /// W3 (about 2,900 ft of runway 12 ahead) and W2 (about 500 ft) are dropped for 12, an intersection in 12's departure
    /// half is kept.
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_TypeWithoutATakeoffDistance_KeepsIntersectionsWithHalfTheRunwayAhead()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AircraftType = "MD81";
        target.AssignedRunway = "12";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayGroup runway12 = host.GetTaxiToRunwayChoices(Callsign).Inline[0];

        Assert.Equal(0, AircraftProfileDatabase.Get("MD81")!.TakeoffDistance);
        Assert.Equal("Runway 12 · assigned runway", runway12.Title);
        List<TaxiRouteRow> entries = EntryRows(runway12);
        Assert.DoesNotContain("At W3", entries.Select(r => r.Name));
        Assert.DoesNotContain("At W2", entries.Select(r => r.Name));
        // 12/30 is about 10,500 ft long: every intersection kept leaves at least half of it (less up to 50 ft of rounding).
        List<TaxiRouteRow> intersections = [.. entries.Where(r => r.AvailableFt is not null)];
        Assert.NotEmpty(intersections);
        Assert.All(intersections, r => Assert.True(r.AvailableFt >= 5200, $"{r.Name} leaves {r.AvailableFt} ft"));
    }

    /// <summary>The runway entry rows of <paramref name="group"/>, in order, without its presets.</summary>
    private static List<TaxiRouteRow> EntryRows(TaxiToRunwayGroup group) => [.. group.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NoActiveRunways_NearestThreeEndsInline_RestUnderOther()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        Assert.Equal(3, menu.Inline.Count);
        Assert.NotEmpty(menu.Other);
        Assert.All(menu.Inline.Concat(menu.Other), g => Assert.Matches(@"^Runway \d{1,2}[LCR]?$", g.Title));
        List<double> inlineNearest = [.. menu.Inline.Select(NearestRowFt)];
        Assert.Equal(inlineNearest.Order(), inlineNearest);
        Assert.All(menu.Other, g => Assert.True(NearestRowFt(g) >= inlineNearest[^1], $"{g.Title} is nearer than an inline end"));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_AssignedRunwayNotActive_ComesFirst_WithItsPresets_AndArrivalEndsAreNotDepartureEnds()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        target.AssignedRunway = "30";
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["28L", "A28R"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        Assert.Equal(["Runway 30 · assigned runway", "Runway 28L · departure runway"], menu.Inline.Select(g => g.Title));
        Assert.Contains(Assert.IsType<Func<IReadOnlyList<TaxiToRunwayGroup>>>(menu.FindOther)(), g => g.Title == "Runway 28R");
        TaxiRouteRow preset = menu.Inline[0].Rows[^1];
        Assert.Equal((TaxiRouteRow.PresetBadge, "TERMINAL to 30", "TAXI T U W RWY 30"), (preset.Badge, preset.Name, preset.Command));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_HoldingShortAtABar_HidesThatBarsRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto bar = Runway30HoldShortNode;
        AircraftModel target = GroundAircraft(Callsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        target.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        Assert.Equal("Runway 30 · assigned runway", menu.Inline[0].Title);
        Assert.Contains(menu.Inline[0].Rows, r => r.Badge == TaxiRouteRow.RunwayEntryBadge);
        List<TaxiRouteRow> rows = [.. menu.Inline.Concat(menu.Other).SelectMany(g => g.Rows)];
        Assert.DoesNotContain(rows, r => r.Preview.Segments[^1].ToNodeId == bar.Id);
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_SecondOpenFromTheSameNode_ReusesTheRoutesItFound()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);

        TaxiToRunwayMenu first = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        int searches = main.Ground.TaxiToRunwayRouteSearches;
        TaxiToRunwayMenu second = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.True(searches > 0, "the first open searched no route");
        Assert.Equal(searches, main.Ground.TaxiToRunwayRouteSearches);
        Assert.Equal(Summary(first), Summary(second));
    }

    [AvaloniaFact]
    public void GroupRunwayEnds_ArrivalsOnlyActiveList_FallsBackToNearestThree()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        TaxiToRunwayMenu noList = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["A30", "A28R"] });

        TaxiToRunwayMenu menu = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.Equal(3, menu.Inline.Count);
        Assert.All(menu.Inline, g => Assert.Matches(@"^Runway \d{1,2}[LCR]?$", g.Title));
        Assert.Null(menu.FindOther);
        Assert.NotEmpty(menu.Other);
        Assert.Equal(Summary(noList), Summary(menu));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NewLayout_SearchesTheRoutesAgain()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        _ = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        int firstSearches = main.Ground.TaxiToRunwayRouteSearches;

        main.Ground.SetLayoutForTesting(Oak);
        _ = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.True(firstSearches > 0, "the first open searched no route");
        Assert.Equal(2 * firstSearches, main.Ground.TaxiToRunwayRouteSearches);
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_HoldingShortWhereAPresetEnds_HidesThatPreset()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel atStart = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        TaxiRouteRow terminal = Assert.Single(
            new ClientMenuHost(OakMain(atStart, []), atStart, new Border()).GetPresetTaxiChoices(Callsign),
            r => r.Name == "TERMINAL to 30"
        );
        GroundNodeDto bar = Oak.Nodes.First(n => n.Id == terminal.Preview.Segments[^1].ToNodeId);
        AircraftModel holding = GroundAircraft(OtherCallsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        holding.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(holding, []), holding, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(OtherCallsign);

        Assert.DoesNotContain(host.GetPresetTaxiChoices(OtherCallsign), r => r.Preview.Segments[^1].ToNodeId == bar.Id);
        Assert.DoesNotContain(menu.Inline.Concat(menu.Other).SelectMany(g => g.Rows), r => r.Preview.Segments[^1].ToNodeId == bar.Id);
    }

    // --- Taxi to runway: the runway's own bars, its centreline and bars without a name ------

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KoakSecondNamedEnd_FirstVariantIsForDeparture()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AssignedRunway = "12";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayGroup runway12 = host.GetTaxiToRunwayChoices(Callsign).Inline[0];

        Assert.Equal("Runway 12 · assigned runway", runway12.Title);
        List<TaxiRouteRow> entries = [.. runway12.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];
        Assert.NotEmpty(entries);
        Assert.All(entries, row => AssertNeverCrossesItsOwnRunway(row.Variants, "12", ["12", "30"]));
        Assert.All(entries, row => Assert.Equal(row.Variants[0].Command, row.Command));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KsfoOneLeft_FirstVariantIsForDeparture()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Sfo.Nodes.First(n => (n.Type == "Parking") && (n.Name == "A10"))));
        target.AssignedRunway = "1L";
        MainViewModel main = MainWith(target, []);
        main.Ground.SetLayoutForTesting(Sfo);
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayGroup runway1L = host.GetTaxiToRunwayChoices(Callsign).Inline[0];

        Assert.Equal("Runway 1L · assigned runway", runway1L.Title);
        List<TaxiRouteRow> entries = [.. runway1L.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];
        Assert.NotEmpty(entries);
        Assert.All(entries, row => AssertNeverCrossesItsOwnRunway(row.Variants, "1L", ["1L", "01L", "19R"]));
    }

    /// <summary>The point menu's taxi to a threshold click on the runway's second-named end goes through the same variants.</summary>
    [AvaloniaFact]
    public void TaxiChoices_ThresholdClickOnTheSecondNamedEnd_NeverCrossesItsOwnRunway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        var runway = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!);

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, runway.End2));

        Assert.Equal("12", runway.End2);
        Assert.NotEmpty(routes);
        Assert.All(routes, route => AssertNeverCrossesItsOwnRunway(route.Children, runway.End2, [runway.End1, runway.End2]));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_HoldingShortWithBarAcrossTheRunway_OffersNoRouteOverThatRunway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundNodeDto bar, GroundNodeDto across) = BarsFacingAcrossTheRunway();
        var runway = RunwayIdentifier.Parse(bar.RunwayId!);
        AircraftModel target = GroundAircraft(Callsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayMenu menu = host.GetTaxiToRunwayChoices(Callsign);

        List<TaxiRouteRow> ownRows = [.. menu.Inline.Concat(menu.Other).Where(g => runway.Contains(g.Title.Split(' ')[1])).SelectMany(g => g.Rows)];
        Assert.DoesNotContain(ownRows, r => r.Preview.Segments[^1].ToNodeId == across.Id);
        Assert.All(
            ownRows,
            r =>
                Assert.DoesNotContain(r.Preview.Segments, s => s.Edge.ToNode.Edges.Any(e => (e.IsRunwayCenterline) && (e.MatchesRunway(runway.End1))))
        );

        // Holding at W4, back along W to the full-length bar at W1 is a taxi, not a crossing: still offered.
        GroundNodeDto w4 = HoldShort30On("W4");
        AircraftModel atW4 = GroundAircraft(OtherCallsign, $"Holding Short {w4.RunwayId}", PositionOf(w4));
        atW4.AssignedRunway = "30";
        TaxiToRunwayGroup runway30 = new ClientMenuHost(OakMain(atW4, []), atW4, new Border()).GetTaxiToRunwayChoices(OtherCallsign).Inline[0];
        Assert.Equal("Runway 30 · assigned runway", runway30.Title);
        Assert.Contains(runway30.Rows, r => r.Preview.Segments[^1].ToNodeId == HoldShort30On("W1").Id);
    }

    /// <summary>
    /// No hold short of KOAK or KSFO lies on runway pavement only, and no KOAK node has only centreline edges, so the rule
    /// is shown on real KOAK data: every KOAK hold short is an entry, and a hold short at a runway 30 centreline junction
    /// holding only that junction's two real centreline edges is not.
    /// </summary>
    [AvaloniaFact]
    public void LiesOnRunwayPavementOnly_OnlyABarWhoseEveryEdgeIsCentreline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");
        AirportGroundLayout layout = GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard);
        GroundNode junction = layout.Nodes.Values.First(n => n.Edges.Count(e => (e.IsRunwayCenterline) && (e.MatchesRunway("30"))) >= 2);
        var onPavement = new GroundNode
        {
            Id = junction.Id,
            Position = junction.Position,
            Type = GroundNodeType.RunwayHoldShort,
            Edges = [.. junction.Edges.Where(e => (e.IsRunwayCenterline) && (e.MatchesRunway("30")))],
        };

        Assert.True(GroundViewModel.LiesOnRunwayPavementOnly(onPavement));
        Assert.All(
            layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort),
            bar => Assert.False(GroundViewModel.LiesOnRunwayPavementOnly(bar))
        );
    }

    /// <summary>
    /// No hold short of KOAK or KSFO sits on an unnamed taxiway, so the name fallback is shown on a real KOAK route: without
    /// the bar's taxiway the entry is named by the route's last taxiway, and a route naming none gives no name.
    /// </summary>
    [AvaloniaFact]
    public void EntryName_WithoutTheBarsTaxiway_IsTheRoutesLastTaxiway_AndNoneWithoutOne()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        TaxiRoute route = main.Ground.FindRoutesToNode(
            from,
            HoldShort30On("W1").Id,
            GroundViewModel.CategoryFor(target),
            GroundViewModel.WakeClassFor(target)
        )[0];
        string lastTaxiway = main.Ground.GetTaxiwayDisplayName(route).Split(' ')[^1];

        Assert.Equal("At W1", GroundViewModel.EntryName("W1", route));
        Assert.Equal($"At {lastTaxiway}", GroundViewModel.EntryName(null, route));
        Assert.Null(GroundViewModel.EntryName(null, new TaxiRoute { Segments = [], HoldShortPoints = [] }));
    }

    /// <summary>
    /// <paramref name="variants"/> taxi to <paramref name="end"/>: the first is For departure to it, and none holds short of
    /// or crosses the runway itself under any spelling of its ends (<paramref name="ownSpellings"/>), but the hold short of
    /// <paramref name="end"/> the Hold short variants end at.
    /// </summary>
    private static void AssertNeverCrossesItsOwnRunway(IReadOnlyList<MenuCommandChoice> variants, string end, string[] ownSpellings)
    {
        Assert.StartsWith($"For Departure {end}", variants[0].Label);
        foreach (MenuCommandChoice variant in variants.Where(v => !ReferenceEquals(v, MenuCommandChoice.Separator)))
        {
            string command = variant.Command ?? "";
            foreach (string spelling in ownSpellings)
            {
                Assert.DoesNotContain($"CROSS {spelling}", variant.Label);
                Assert.DoesNotContain($", HS {spelling}", variant.Label);
                Assert.DoesNotContain($"CROSS {spelling}", command);
                Assert.True(
                    (spelling == end) || !command.Contains($" HS {spelling}", StringComparison.Ordinal),
                    $"'{command}' holds short of {spelling}"
                );
            }
        }
    }

    /// <summary>
    /// The first hold short of KOAK, in layout order, with a twin: another hold short of the same runway on a taxiway it
    /// also sits on, under 700 ft away, across the runway.
    /// </summary>
    private static (GroundNodeDto Bar, GroundNodeDto Across) BarsFacingAcrossTheRunway()
    {
        List<GroundNodeDto> bars = [.. Oak.Nodes.Where(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is not null))];
        return bars.SelectMany(bar => bars.Where(across => IsTwin(bar, across)).Select(across => (bar, across))).First();
    }

    private static bool IsTwin(GroundNodeDto bar, GroundNodeDto across) =>
        (across.Id != bar.Id)
        && (RunwayIdentifier.Parse(across.RunwayId!) == RunwayIdentifier.Parse(bar.RunwayId!))
        && (GeoMath.DistanceNm(bar.Latitude, bar.Longitude, across.Latitude, across.Longitude) * GeoMath.FeetPerNm < 700)
        && (TaxiwaysAt(across.Id).Overlaps(TaxiwaysAt(bar.Id)));

    /// <summary>The non-runway taxiway names on the links at <paramref name="nodeId"/> of KOAK.</summary>
    private static HashSet<string> TaxiwaysAt(int nodeId) => [.. LinkNamesOf(nodeId).Where(name => (name.Length > 0) && !IsCentrelineName(name))];

    /// <summary>A runway centreline's name (<c>RWY28L/10R</c>), not a runway crossing link's.</summary>
    private static bool IsCentrelineName(string name) =>
        name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !name.Contains(":link", StringComparison.OrdinalIgnoreCase);

    private static GroundLayoutDto Sfo => MenuGoldenFixtures.SfoLayoutForClient;

    /// <summary>Every group's title and its rows' name, reason, distance and command, in order.</summary>
    private static List<string> Summary(TaxiToRunwayMenu menu) =>
        [.. menu.Inline.Concat(menu.Other).SelectMany(g => g.Rows.Select(r => $"{g.Title}: {r.Name} {r.Reason} {r.DistanceFt} {r.Command}"))];

    /// <summary>The distance to the nearest row of <paramref name="group"/>, which orders the ends shown inline.</summary>
    private static double NearestRowFt(TaxiToRunwayGroup group) => group.Rows.Min(r => r.DistanceFt);

    /// <summary>
    /// A runway entry to <paramref name="runway"/>: named by its hold short's taxiway, routed by a via, a distance away,
    /// showing its first variant's command, opening For departure and Hold short of the runway, and previewing its route
    /// to a hold short of the runway.
    /// </summary>
    private static Action<TaxiRouteRow> AssertEntryTo(string runway) =>
        row =>
        {
            Assert.StartsWith("At ", row.Name);
            Assert.StartsWith("via ", row.Via);
            Assert.True(row.DistanceFt > 0, $"{row.Name}: distance {row.DistanceFt}");
            Assert.Equal(row.Variants[0].Command, row.Command);
            Assert.Equal($"For Departure {runway}", row.Variants[0].Label);
            Assert.EndsWith($" {runway}", row.Command);
            Assert.Contains(row.Variants, v => ReferenceEquals(v, MenuCommandChoice.Separator));
            Assert.Contains(row.Variants, v => v.Command?.EndsWith($" HS {runway}", StringComparison.Ordinal) == true);
            GroundNodeDto end = Oak.Nodes.First(n => n.Id == row.Preview.Segments[^1].ToNodeId);
            Assert.Equal("RunwayHoldShort", end.Type);
            Assert.True(RunwayIdentifier.Parse(end.RunwayId!).Contains(runway), $"{row.Name} ends at a hold short of {end.RunwayId}");
        };

    [AvaloniaFact]
    public void PushbackAndPresetChoices_NoLayout_AreEmpty()
    {
        AircraftModel target = GroundAircraft(Callsign, "At Parking", new LatLon(37.72, -122.22));
        var host = new ClientMenuHost(MainWith(target, []), target, new Border());

        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        Assert.Empty(host.GetPushbackToChoices(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));
    }

    [AvaloniaFact]
    public void EnterPushRoute_StartsPushRouteDrawingForTheRightClickedAircraft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        host.EnterPushRoute(Callsign);

        Assert.True(main.Ground.IsDrawingRoute);
        Assert.Equal(Callsign, main.Ground.PushRouteCallsign);
    }

    // --- Point menu: taxi choices and the custom-taxi seed ----------------------------------

    [AvaloniaFact]
    public void TaxiChoices_NoRoute_IsOneDisabledRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        // The first node, helipads first, that the pathfinder cannot reach from I30 for a B738.
        GroundNodeDto? unreachable = Oak
            .Nodes.OrderBy(n => (n.Type == "Helipad") ? 0 : 1)
            .FirstOrDefault(n =>
                main.Ground.FindRoutesToNode(from, n.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target)).Count == 0
            );
        Assert.NotNull(unreachable);

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(Callsign, unreachable, null);

        MenuCommandChoice row = Assert.Single(choices);
        Assert.Equal("No route found", row.Label);
        Assert.Null(row.Command);
        Assert.Null(row.Preview);
        Assert.Empty(row.Children);
    }

    [AvaloniaFact]
    public void TaxiChoices_HoldShortNode_UsesItsRunwayEnd1()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        string end1 = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End1;

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, null));

        Assert.NotEmpty(routes);
        Assert.All(routes, AssertRoutesToRunway(end1));
    }

    [AvaloniaFact]
    public void TaxiChoices_ThresholdClick_UsesTheClickedEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        // The other end than the node's own End1, so the answer can only come from the click.
        string clickedEnd = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End2;

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, clickedEnd));

        Assert.NotEmpty(routes);
        Assert.All(routes, AssertRoutesToRunway(clickedEnd));
    }

    [AvaloniaFact]
    public void TaxiChoices_TwoOrMoreRoutes_NestUnderTaxiHere()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        List<TaxiRoute> RoutesTo(GroundNodeDto node) =>
            main.Ground.FindRoutesToNode(from, node.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target));
        // The first taxiway intersection, in layout order, the pathfinder reaches from W3 by two or more routes.
        GroundNodeDto destination = Oak.Nodes.Where(n => n.Type == "TaxiwayIntersection").First(n => RoutesTo(n).Count >= 2);
        List<TaxiRoute> found = RoutesTo(destination);

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(Callsign, destination, null);

        MenuCommandChoice taxiHere = Assert.Single(choices);
        Assert.Equal("Taxi here", taxiHere.Label);
        Assert.Null(taxiHere.Command);
        Assert.Equal(found.Count, taxiHere.Children.Count);
        Assert.All(taxiHere.Children, c => Assert.StartsWith("Taxi ", c.Label));
        Assert.All(taxiHere.Children, c => Assert.True((c.Command is not null) || (c.Children.Count > 0), $"'{c.Label}' sends nothing"));
    }

    [AvaloniaFact]
    public void CustomTaxiSeed_FollowsTheNodeType()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        GroundNodeDto parking = Oak.Nodes.First(n => (n.Type == "Parking") && (n.Name is not null));
        string rwy = RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End1);
        string taxiway = main.Ground.GetNodeTaxiwayNames(PresetTaxiNode.Id)[0];

        Assert.Equal(new MenuTextSeed("TAXI  $I30", 5), host.GetCustomTaxiSeed(PushbackFaceNode, null));
        Assert.Equal(new MenuTextSeed($"TAXI  @{parking.Name}", 5), host.GetCustomTaxiSeed(parking, null));
        Assert.Equal(new MenuTextSeed($"RWY {rwy} TAXI ", $"RWY {rwy} TAXI ".Length), host.GetCustomTaxiSeed(Runway30HoldShortNode, null));
        Assert.Equal(new MenuTextSeed($"TAXI  {taxiway}", 5), host.GetCustomTaxiSeed(PresetTaxiNode, null));
    }

    [AvaloniaFact]
    public void CustomTaxiSeed_ThresholdEnd_WinsOverTheHoldShortRunwayEnd1()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        string end2 = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End2;
        string seed = $"RWY {RunwayIdentifier.ToDisplayDesignator(end2)} TAXI ";

        Assert.Equal(new MenuTextSeed(seed, seed.Length), host.GetCustomTaxiSeed(Runway30HoldShortNode, end2));
    }

    /// <summary>The per-route choices: a "Taxi here" submenu's children, else the answer itself.</summary>
    private static IReadOnlyList<MenuCommandChoice> RouteChoices(IReadOnlyList<MenuCommandChoice> choices) =>
        (choices is [{ Label: "Taxi here" } parent]) ? parent.Children : choices;

    /// <summary>
    /// A route to <paramref name="runway"/>: a "Taxi …" submenu previewing the route, whose first item is the departure
    /// taxi to that runway and whose separator divides it from the hold-short variants.
    /// </summary>
    private static Action<MenuCommandChoice> AssertRoutesToRunway(string runway) =>
        route =>
        {
            Assert.StartsWith("Taxi ", route.Label);
            Assert.NotNull(route.Preview);
            Assert.StartsWith($"For Departure {runway}", route.Children[0].Label);
            Assert.Contains(route.Children, c => ReferenceEquals(c, MenuCommandChoice.Separator));
        };

    // --- The builder's ground items over the host's answers ---------------------------------

    // The menu is built over the client host with only its sends captured, since the client host sends to a server a test
    // has none of; the choices and the hover preview are the client host's own.
    [AvaloniaFact]
    public void Builder_Taxiing_HoldShortFollowAndGiveWay_SendAndPreview()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        AircraftModel candidate = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(target.Position.Lat + 0.002, target.Position.Lon));
        MainViewModel main = OakMain(target, [candidate]);
        var host = new SendCapturingHost(new ClientMenuHost(main, target, new Border()), "AB");

        ContextMenu menu = AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []);

        string[] groundItems = ["Hold short of…", "Follow…", "Give way to…"];
        Assert.Equal(groundItems, Headers(CommandTree(menu)).Where(groundItems.Contains));
        Assert.Equal(["route W3 · RWY 30"], Headers(Item(CommandTree(menu), "Hold short of…").Items));
        MenuItem followRow = TrafficRow(Item(CommandTree(menu), "Follow…").Items, OtherCallsign);
        MenuItem giveWayRow = TrafficRow(Item(CommandTree(menu), "Give way to…").Items, OtherCallsign);
        Assert.Equal(["Moving"], Headers(Item(CommandTree(menu), "Follow…").Items));
        Assert.Equal(["Moving"], Headers(Item(CommandTree(menu), "Give way to…").Items));

        MenuItem holdShortRunway = Assert.Single(Item(CommandTree(menu), "Hold short of…").Items.OfType<MenuItem>(), m => m.Header is not string);
        RaisePointerEntered(holdShortRunway);
        Assert.NotNull(main.Ground.PreviewRoute);
        Assert.Equal(Runway30HoldShortNode.Id, main.Ground.PreviewRoute.Segments[^1].ToNodeId);

        Click(holdShortRunway);
        Click(followRow);
        Click(giveWayRow);

        Assert.Equal([(Callsign, "HS 12", "AB"), (Callsign, $"FOLLOWG {OtherCallsign}", "AB"), (Callsign, $"GW {OtherCallsign}", "AB")], host.Sent);
    }

    /// <summary>The traffic row in <paramref name="items"/> whose one-line header starts with <paramref name="callsign"/>.</summary>
    private static MenuItem TrafficRow(ItemCollection items, string callsign) =>
        items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith($"{callsign} ·", StringComparison.Ordinal) == true);

    // --- Fixtures ---------------------------------------------------------------------------

    private static AircraftModel GroundAircraft(string callsign, string phase, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = phase,
            Position = position,
        };

    /// <summary>A taxiing aircraft on the W3 node behind runway 30's hold-short, routed along W3 to runway 30.</summary>
    private static AircraftModel TaxiingOnW3()
    {
        GroundNodeDto node = TaxiingNode;
        AircraftModel ac = GroundAircraft(Callsign, "Taxiing", new LatLon(node.Latitude, node.Longitude));
        ac.TaxiRoute = "W3";
        ac.CurrentTaxiway = "W3";
        ac.AssignedRunway = "30";
        ac.HasActiveTaxiRoute = true;
        return ac;
    }

    private static MainViewModel MainWith(AircraftModel target, AircraftModel[] others)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(target);
        foreach (AircraftModel other in others)
        {
            main.Aircraft.Add(other);
        }

        return main;
    }

    /// <summary>
    /// A main view model holding <paramref name="target"/> and <paramref name="others"/>, its primary ground view model
    /// over the committed KOAK layout.
    /// </summary>
    private static MainViewModel OakMain(AircraftModel target, AircraftModel[] others)
    {
        MainViewModel main = MainWith(target, others);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        return main;
    }

    private static GroundLayoutDto Oak => MenuGoldenFixtures.OakLayoutForClient;

    private static LatLon PositionOf(GroundNodeDto node) => new(node.Latitude, node.Longitude);

    /// <summary>The named Spot "I30", the pushback fixture of <c>GroundSubmenuCharacterizationTests</c>: two non-RAMP W1 edges.</summary>
    private static GroundNodeDto PushbackFaceNode => Oak.Nodes.First(n => (n.Type == "Spot") && (n.Name == "I30"));

    /// <summary>The only intersection on both taxiway T and taxiway U, where the sidecar's "TERMINAL to 30" route begins.</summary>
    private static GroundNodeDto PresetTaxiNode =>
        Oak.Nodes.First(n => (n.Type == "TaxiwayIntersection") && LinkNamesOf(n.Id).Contains("T") && LinkNamesOf(n.Id).Contains("U"));

    /// <summary>Gate 25, a named parking node at the terminal.</summary>
    private static GroundNodeDto Gate25Node => Oak.Nodes.First(n => (n.Type == "Parking") && (n.Name == "25"));

    /// <summary>Runway 30's hold short on <paramref name="taxiway"/>.</summary>
    private static GroundNodeDto HoldShort30On(string taxiway) =>
        Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains(taxiway));

    /// <summary>The <paramref name="taxiway"/> node just behind <paramref name="holdShort"/>, away from the runway.</summary>
    private static GroundNodeDto NodeBehind(GroundNodeDto holdShort, string taxiway) =>
        Oak
            .Edges.Where(e => (e.TaxiwayName == taxiway) && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
            .Select(e => Oak.Nodes.First(n => n.Id == ((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId)))
            .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The distance from <paramref name="aircraft"/> to the named stand <paramref name="name"/>.</summary>
    private static double DistanceToStand(AircraftModel aircraft, string name)
    {
        GroundNodeDto stand = Oak.Nodes.First(n => (n.Name == name) && (n.Type is "Spot" or "Parking" or "Helipad"));
        return GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, stand.Latitude, stand.Longitude);
    }

    /// <summary>Runway 30's hold-short node on taxiway W3.</summary>
    private static GroundNodeDto Runway30HoldShortNode =>
        Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains("W3"));

    /// <summary>
    /// The W3 node just behind runway 30's hold-short at W3: of the hold-short's two W3 neighbours, the one with no link
    /// onto the runway (the hold-short fixture of <c>GroundSubmenuCharacterizationTests</c>).
    /// </summary>
    private static GroundNodeDto TaxiingNode
    {
        get
        {
            GroundNodeDto holdShort = Runway30HoldShortNode;
            return Oak
                .Edges.Where(e => (e.TaxiwayName == "W3") && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
                .Select(e => Oak.Nodes.First(n => n.Id == ((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId)))
                .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>The taxiway names on every link at <paramref name="nodeId"/>, plain edges and fillet arcs alike.</summary>
    private static IEnumerable<string> LinkNamesOf(int nodeId) =>
        Oak
            .Edges.Where(e => (e.FromNodeId == nodeId) || (e.ToNodeId == nodeId))
            .Select(e => e.TaxiwayName)
            .Concat((Oak.Arcs ?? []).Where(a => (a.FromNodeId == nodeId) || (a.ToNodeId == nodeId)).SelectMany(a => a.TaxiwayNames));

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>The items of the menu's All Commands submenu, where the ground block lives.</summary>
    private static ItemCollection CommandTree(ContextMenu menu) => Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items;

    private static MenuItem Item(ItemCollection items, string header)
    {
        MenuItem? item = items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string s && s == header);
        Assert.NotNull(item);
        return item;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the typed
    /// <see cref="InputElement.PointerEntered"/> handler requires (see <c>GroundSubmenuCharacterizationTests.RaisePointer</c>).
    /// </summary>
    private static void RaisePointerEntered(MenuItem item) =>
        item.RaiseEvent(
            new PointerEventArgs(
                InputElement.PointerEnteredEvent,
                item,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
