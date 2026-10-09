using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>The three views whose aircraft context menus the goldens pin; the value's lower-case name is the golden folder.</summary>
internal enum MenuView
{
    Radar,
    Ground,
    List,
}

/// <summary>
/// One golden fixture: the right-clicked aircraft and, for a relative selection, the aircraft selected before the
/// right-click. <see cref="Name"/> is the golden file's name.
/// </summary>
internal sealed record MenuFixture(string Name, AircraftModel Aircraft, AircraftModel? Selected);

/// <summary>
/// The fixed aircraft the menu goldens right-click, with the committed NavData, CIFP and KOAK layout they are built
/// against. Every call returns fresh <see cref="AircraftModel"/>s built from constants (no clock, no randomness), one per
/// <see cref="AircraftSituation"/> except <see cref="AircraftSituation.Unknown"/> (an IFR and a VFR one where the
/// situation allows both), plus live traffic, a delayed spawn and a relative selection (every view; the list shares the ground's).
/// IFR fixtures fly a B738 to KOAK runway 30 and VFR fixtures a C172 to 28R.
/// </summary>
internal static class MenuGoldenFixtures
{
    public const string Initials = "AB";

    private static readonly Lazy<AirportGroundLayout> OakLayout = new(ParseOakLayout);
    private static readonly Lazy<GroundLayoutDto> OakLayoutDto = new(() => ToGroundLayoutDto(OakLayout.Value));

    /// <summary>
    /// Installs the committed navigation database (other UI tests replace the process-wide one with synthetic data) and
    /// returns it for the render's scoped override.
    /// </summary>
    public static NavigationDatabase EnsureNavData()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb
            ?? throw new InvalidOperationException($"NavData or CIFP missing under {Path.Combine(AppContext.BaseDirectory, "TestData")}");
    }

    /// <summary>The KOAK layout as the server sends it, for <c>GroundViewModel.SetLayoutForTesting</c>.</summary>
    public static GroundLayoutDto OakLayoutForClient => OakLayoutDto.Value;

    /// <summary>The fixtures one view's goldens cover, in golden-file order.</summary>
    public static IReadOnlyList<MenuFixture> For(MenuView view)
    {
        AirportGroundLayout layout = OakLayout.Value;
        List<MenuFixture> fixtures =
        [
            .. GroundFixtures(layout),
            .. AirborneIfrFixtures(),
            .. TrackStateFixtures(),
            .. AirborneVfrFixtures(),
            .. LiveTrafficFixtures(layout),
            DelayedSpawn(),
        ];
        switch (view)
        {
            case MenuView.Radar:
                fixtures.Add(RadarRelativeSelection());
                break;
            case MenuView.Ground:
            case MenuView.List:
                fixtures.Add(GroundRelativeSelection(layout));
                break;
        }

        return fixtures;
    }

    private static AirportGroundLayout ParseOakLayout()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");
        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard);
    }

    /// <summary>Gate 25, a named parking node.</summary>
    private static GroundNode Gate25(AirportGroundLayout layout) =>
        layout.Nodes.Values.Single(n => (n.Type == GroundNodeType.Parking) && (n.Name == "25"));

    /// <summary>
    /// The runway 30/12 hold-short on taxiway W3 (hold-short nodes carry no name; each of the seven runway 30 hold-shorts
    /// sits on its own taxiway, W1 to W7, so the runway and taxiway pair picks exactly one).
    /// </summary>
    public static GroundNode HoldShort30AtW3(AirportGroundLayout layout) =>
        layout.Nodes.Values.Single(n =>
            (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } rwy) && rwy.Contains("30") && n.Edges.Any(e => e.MatchesTaxiway("W3"))
        );

    /// <summary>
    /// The W3 node just behind <see cref="HoldShort30AtW3"/> on the side away from the runway, where an aircraft taxis up
    /// to it: of the hold-short's two W3 neighbours, the one with no edge onto the runway (the other joins RWY30/12).
    /// </summary>
    public static GroundNode W3NodeBeforeHoldShort30(AirportGroundLayout layout)
    {
        GroundNode holdShort = HoldShort30AtW3(layout);
        return holdShort
            .Edges.Where(e => e.MatchesTaxiway("W3"))
            .Select(e => (e.Nodes[0].Id == holdShort.Id) ? e.Nodes[1] : e.Nodes[0])
            .Single(n => !n.Edges.Any(e => e.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<MenuFixture> GroundFixtures(AirportGroundLayout layout)
    {
        LatLon gate = Gate25(layout).Position;
        GroundNode holdShort = HoldShort30AtW3(layout);
        LatLon taxiway = W3NodeBeforeHoldShort30(layout).Position;
        return
        [
            new("at-parking", GroundJet("SWA101", "At Parking", AircraftSituation.AtParking, gate, ""), null),
            new("pushing-back", GroundJet("SWA102", "Pushback", AircraftSituation.PushingBack, gate, ""), null),
            new("holding-on-ground", GroundJet("SWA103", "Holding In Position", AircraftSituation.HoldingOnGround, taxiway, "30"), null),
            new("taxiing", GroundJet("SWA104", "Taxiing", AircraftSituation.Taxiing, taxiway, "30"), null),
            new(
                "holding-short",
                GroundJet("SWA105", $"Holding Short {holdShort.RunwayId}", AircraftSituation.HoldingShort, holdShort.Position, "30"),
                null
            ),
            new("lined-up", GroundJet("SWA106", "LinedUpAndWaiting", AircraftSituation.LinedUp, holdShort.Position, "30"), null),
            new("rollout-exit", RolloutJet(holdShort.Position), null),
            new("rollout-exit-listed", RolloutJetWithExitsAhead(holdShort.Position), null),
            new("held-for-release", HeldForRelease(taxiway), null),
            new("cfr-window", CfrWindow(taxiway), null),
        ];
    }

    /// <summary>A B738 taxiing under an armed hold-for-release, which the header's Release (HFR) item clears.</summary>
    private static AircraftModel HeldForRelease(LatLon position)
    {
        AircraftModel ac = GroundJet("SWA108", "Taxiing", AircraftSituation.Taxiing, position, "30");
        ac.IsHeldForRelease = true;
        return ac;
    }

    /// <summary>A B738 taxiing with a call-for-release window, which the header's Check release window item reports on.</summary>
    private static AircraftModel CfrWindow(LatLon position)
    {
        AircraftModel ac = GroundJet("SWA109", "Taxiing", AircraftSituation.Taxiing, position, "30");
        ac.CfrWindowStartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        return ac;
    }

    /// <summary>A B738 departing KOAK, on the ground in the given phase.</summary>
    private static AircraftModel GroundJet(string callsign, string phase, AircraftSituation situation, LatLon position, string runway) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = phase,
            Situation = situation,
            IsOnGround = true,
            Position = position,
            Heading = new TrueHeading(294),
            Departure = "KOAK",
            Destination = "KLAX",
            Route = "SUNOL Q126 ALTAM",
            AssignedRunway = runway,
        };

    /// <summary>A B738 that has landed on runway 30 and is rolling out (a hold-short node near the runway stands in for the rollout).</summary>
    private static AircraftModel RolloutJet(LatLon position) =>
        new()
        {
            Callsign = "SWA107",
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "Landing",
            Situation = AircraftSituation.RolloutExit,
            IsOnGround = true,
            Position = position,
            Heading = new TrueHeading(294),
            GroundSpeed = 60,
            IndicatedAirspeed = 60,
            Departure = "KLAX",
            Destination = "KOAK",
            AssignedRunway = "30",
        };

    /// <summary>
    /// The rolling-out B738 decelerating, with the named exits ahead listed on both sides (W2 the planned one), so the strip
    /// carries Exit left and Exit right and All Commands shows both flyouts' rows.
    /// </summary>
    private static AircraftModel RolloutJetWithExitsAhead(LatLon position)
    {
        AircraftModel ac = RolloutJet(position);
        ac.Callsign = "SWA110";
        ac.SituationFlags = SituationFlags.RolloutDecelerating;
        ac.ExitsAhead =
        [
            new ExitAheadDto("W1", ExitSide.Left, 900, false),
            new ExitAheadDto("W2", ExitSide.Right, 1800, true),
            new ExitAheadDto("W3", ExitSide.Right, 3600, false),
        ];
        return ac;
    }

    private static List<MenuFixture> AirborneIfrFixtures() =>
        [
            new("departing-ifr", AirborneJet("AAL201", "InitialClimb", AircraftSituation.Departing, new LatLon(37.7500, -122.2800), 3000), null),
            new("ifr-enroute", IfrEnroute(), null),
            new("ifr-arrival", IfrArrival(), null),
            new("approach-ifr", AirborneJet("AAL204", "ApproachNav", AircraftSituation.Approach, new LatLon(37.6200, -122.0500), 3000), null),
            new("holding-ifr", AirborneJet("AAL205", "HoldingPattern", AircraftSituation.Holding, new LatLon(37.5500, -122.0000), 5000), null),
            new("pattern-ifr", AirborneJet("AAL206", "Downwind", AircraftSituation.Pattern, new LatLon(37.7000, -122.1900), 1500), null),
            new("final-ifr", AirborneJet("AAL207", "FinalApproach", AircraftSituation.Final, new LatLon(37.6800, -122.1500), 1500), null),
            new("go-around-ifr", AirborneJet("AAL208", "GoAround", AircraftSituation.GoAround, new LatLon(37.7250, -122.2300), 1000), null),
        ];

    /// <summary>An airborne B738 in the KOAK terminal area, runway 30, KLAX to KOAK.</summary>
    private static AircraftModel AirborneJet(string callsign, string phase, AircraftSituation situation, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = phase,
            Situation = situation,
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(294),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            GroundSpeed = 180,
            Departure = "KLAX",
            Destination = "KOAK",
            AssignedRunway = "30",
        };

    private static AircraftModel IfrEnroute() =>
        new()
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.IfrEnroute,
            IsOnGround = false,
            Position = new LatLon(37.5000, -121.7000),
            Heading = new TrueHeading(120),
            Altitude = 33000,
            IndicatedAirspeed = 280,
            GroundSpeed = 460,
            Departure = "KOAK",
            Destination = "KLAX",
            Route = "SUNOL Q126 ALTAM",
        };

    /// <summary>An IFR arrival to KOAK on the EMZOH4 STAR, filed via J6.</summary>
    private static AircraftModel IfrArrival() =>
        new()
        {
            Callsign = "AAL203",
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.IfrArrival,
            IsOnGround = false,
            Position = new LatLon(37.3000, -121.8000),
            Heading = new TrueHeading(330),
            Altitude = 16000,
            IndicatedAirspeed = 280,
            GroundSpeed = 380,
            Departure = "KLAX",
            Destination = "KOAK",
            Route = "LANDO J6 AVE RGOOD EMZOH4",
        };

    /// <summary>
    /// The track states the Track submenu filters on: an owned track, an owned track with a handoff in progress and an
    /// owned track with a pointout pending, each an otherwise plain enroute B738. Untracked is every other fixture.
    /// </summary>
    private static List<MenuFixture> TrackStateFixtures() =>
        [
            new("track-owned", TrackState("AAL211", owner: "NCT", handoffPeer: null, pointoutStatus: null), null),
            new("track-handoff", TrackState("AAL212", owner: "NCT", handoffPeer: "OAK_3", pointoutStatus: null), null),
            new("track-pointout", TrackState("AAL213", owner: "NCT", handoffPeer: null, pointoutStatus: "Pending"), null),
        ];

    /// <summary>An enroute B738 carrying the track state the fixture pins.</summary>
    private static AircraftModel TrackState(string callsign, string? owner, string? handoffPeer, string? pointoutStatus)
    {
        AircraftModel ac = AirborneJet(callsign, "", AircraftSituation.IfrEnroute, new LatLon(37.5000, -121.7000), 33000);
        ac.OwnerSectorCode = owner;
        ac.HandoffPeer = handoffPeer;
        ac.PointoutStatus = pointoutStatus;
        return ac;
    }

    private static List<MenuFixture> AirborneVfrFixtures() =>
        [
            new("departing-vfr", AirborneCessna("N301AB", "InitialClimb", AircraftSituation.Departing, new LatLon(37.7450, -122.2500), 1500), null),
            new("vfr-flight-following", VfrCessnaNoPhase("N302AB", AircraftSituation.VfrFlightFollowing, new LatLon(37.9500, -122.3500), 5500), null),
            new("vfr-arrival-inbound", VfrCessnaNoPhase("N303AB", AircraftSituation.VfrArrivalInbound, new LatLon(37.9000, -122.2500), 3500), null),
            new("vfr-departing", AirborneCessna("N304AB", "PatternExit", AircraftSituation.VfrDeparting, new LatLon(37.7400, -122.2500), 1500), null),
            new("approach-vfr", AirborneCessna("N305AB", "ApproachNav", AircraftSituation.Approach, new LatLon(37.6500, -122.1000), 2500), null),
            new("holding-vfr", AirborneCessna("N306AB", "HoldingPattern", AircraftSituation.Holding, new LatLon(37.6000, -122.0500), 4000), null),
            new("pattern-vfr", AirborneCessna("N307AB", "Downwind", AircraftSituation.Pattern, new LatLon(37.7100, -122.2000), 1000), null),
            new("final-vfr", AirborneCessna("N308AB", "FinalApproach", AircraftSituation.Final, new LatLon(37.7000, -122.1800), 800), null),
            new("go-around-vfr", AirborneCessna("N309AB", "GoAround", AircraftSituation.GoAround, new LatLon(37.7200, -122.2200), 600), null),
        ];

    /// <summary>An airborne C172 on a VFR flight from KOAK back to KOAK, runway 28R.</summary>
    private static AircraftModel AirborneCessna(string callsign, string phase, AircraftSituation situation, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightRules = "VFR",
            CurrentPhase = phase,
            Situation = situation,
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(280),
            Altitude = altitude,
            IndicatedAirspeed = 90,
            GroundSpeed = 90,
            Departure = "KOAK",
            Destination = "KOAK",
            AssignedRunway = "28R",
        };

    /// <summary>An airborne C172 with no phase, classified by its flight rules alone: KSTS to KOAK.</summary>
    private static AircraftModel VfrCessnaNoPhase(string callsign, AircraftSituation situation, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightRules = "VFR",
            CurrentPhase = "",
            Situation = situation,
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(170),
            Altitude = altitude,
            IndicatedAirspeed = 110,
            GroundSpeed = 110,
            Departure = "KSTS",
            Destination = "KOAK",
        };

    private static List<MenuFixture> LiveTrafficFixtures(AirportGroundLayout layout) =>
        [
            new("live-traffic-assumable", LiveTraffic("UAL401", isOnGround: false, new LatLon(37.4500, -121.9500), 11000), null),
            new("live-traffic-surface", LiveTraffic("UAL402", isOnGround: true, Gate25(layout).Position, 0), null),
        ];

    /// <summary>A live-traffic shadow: no phase, KLAX to KOAK.</summary>
    private static AircraftModel LiveTraffic(string callsign, bool isOnGround, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.LiveTraffic,
            IsLiveTraffic = true,
            IsOnGround = isOnGround,
            Position = position,
            Heading = new TrueHeading(330),
            Altitude = altitude,
            IndicatedAirspeed = isOnGround ? 0 : 250,
            GroundSpeed = isOnGround ? 0 : 250,
            Departure = "KLAX",
            Destination = "KOAK",
        };

    /// <summary>An IFR arrival whose spawn is still delayed; the server classifies the pending aircraft's state.</summary>
    private static MenuFixture DelayedSpawn() =>
        new(
            "delayed-spawn",
            new AircraftModel
            {
                Callsign = "AAL501",
                AircraftType = "B738",
                FlightRules = "IFR",
                CurrentPhase = "",
                Situation = AircraftSituation.IfrArrival,
                Status = "Delayed (120s)",
                IsOnGround = false,
                Position = new LatLon(37.3000, -121.8000),
                Heading = new TrueHeading(330),
                Altitude = 16000,
                Departure = "KLAX",
                Destination = "KOAK",
                Route = "LANDO J6 AVE RGOOD EMZOH4",
            },
            null
        );

    /// <summary>
    /// Radar: the selected arrival has reported the right-clicked one in sight, so both the report-in-sight and the
    /// follow items appear.
    /// </summary>
    private static MenuFixture RadarRelativeSelection()
    {
        AircraftModel rightClicked = AirborneJet("AAL601", "FinalApproach", AircraftSituation.Final, new LatLon(37.6800, -122.1500), 1500);
        AircraftModel selected = AirborneJet("AAL602", "ApproachNav", AircraftSituation.Approach, new LatLon(37.6200, -122.0500), 3000);
        selected.LastReportedTrafficCallsign = rightClicked.Callsign;
        return new MenuFixture("relative-selection", rightClicked, selected);
    }

    /// <summary>Ground and list: both aircraft on the ground, so the give-way and follow items appear.</summary>
    private static MenuFixture GroundRelativeSelection(AirportGroundLayout layout)
    {
        GroundNode holdShort = HoldShort30AtW3(layout);
        AircraftModel rightClicked = GroundJet(
            "SWA601",
            $"Holding Short {holdShort.RunwayId}",
            AircraftSituation.HoldingShort,
            holdShort.Position,
            "30"
        );
        AircraftModel selected = GroundJet("SWA602", "Taxiing", AircraftSituation.Taxiing, W3NodeBeforeHoldShort30(layout).Position, "30");
        return new MenuFixture("relative-selection", rightClicked, selected);
    }

    /// <summary>
    /// The layout as a <see cref="GroundLayoutDto"/>, covering only what <c>GroundViewModel.ReconstructLayout</c> and the
    /// menus read: nodes, edges, arc geometry and runways. ADW marks are left null and no arc is hidden; the server's
    /// <c>DtoConverter.ToGroundLayoutDto</c> is the full conversion.
    /// </summary>
    public static GroundLayoutDto ToGroundLayoutDto(AirportGroundLayout layout)
    {
        var nodes = new List<GroundNodeDto>(layout.Nodes.Count);
        foreach (GroundNode node in layout.Nodes.Values)
        {
            nodes.Add(
                new GroundNodeDto(
                    node.Id,
                    node.Position.Lat,
                    node.Position.Lon,
                    node.Type.ToString(),
                    node.Name,
                    node.TrueHeading?.Degrees,
                    node.RunwayId?.ToString()
                )
            );
        }

        var edges = new List<GroundEdgeDto>(layout.Edges.Count);
        foreach (GroundEdge edge in layout.Edges)
        {
            List<double[]>? intermediatePoints =
                (edge.IntermediatePoints.Count > 0) ? [.. edge.IntermediatePoints.Select(p => new[] { p.Lat, p.Lon })] : null;
            edges.Add(new GroundEdgeDto(edge.Nodes[0].Id, edge.Nodes[1].Id, edge.TaxiwayName, edge.DistanceNm, intermediatePoints));
        }

        var arcs = new List<GroundArcDto>(layout.Arcs.Count);
        foreach (GroundArc arc in layout.Arcs)
        {
            arcs.Add(
                new GroundArcDto(
                    arc.Nodes[0].Id,
                    arc.Nodes[1].Id,
                    arc.TaxiwayNames,
                    arc.P1Lat,
                    arc.P1Lon,
                    arc.P2Lat,
                    arc.P2Lon,
                    arc.MinRadiusOfCurvatureFt,
                    arc.DistanceNm,
                    arc.TurnAngleDeg
                )
            );
        }

        return new GroundLayoutDto(layout.AirportId, nodes, edges, arcs, ToRunwayDtos(layout), AdwMarks: null);
    }

    /// <summary>The layout's runways as the server's <c>DtoConverter.ToGroundLayoutDto</c> sends them: name, coordinates and width.</summary>
    private static List<GroundRunwayDto> ToRunwayDtos(AirportGroundLayout layout)
    {
        var runways = new List<GroundRunwayDto>(layout.Runways.Count);
        foreach (GroundRunway rwy in layout.Runways)
        {
            var coords = new List<double[]>(rwy.Coordinates.Count);
            foreach ((double lat, double lon) in rwy.Coordinates)
            {
                coords.Add([lat, lon]);
            }

            runways.Add(new GroundRunwayDto(rwy.Name, coords, rwy.WidthFt));
        }

        return runways;
    }

    /// <summary>The KOAK layout as parsed from the committed GeoJSON, for tests that read its domain graph.</summary>
    public static AirportGroundLayout OakDomainLayout => OakLayout.Value;
}
