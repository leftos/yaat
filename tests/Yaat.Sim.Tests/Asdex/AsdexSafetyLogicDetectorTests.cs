using Xunit;
using Yaat.Sim;
using Yaat.Sim.Asdex;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Asdex;

/// <summary>
/// ASDE-X Safety Logic detector — closed runway, occupied runway and taxiway landing, with the
/// alert texts and per-pair alerts measured on vNAS (yaat-server docs/plans/asdex-probe-sfo). Runway "28R" is a
/// rectangle around (37.000..37.001, -122.010..-122.000); aircraft headings drive the
/// aligned/crossing classification (runway heading 280 parsed from the id).
/// </summary>
public class AsdexSafetyLogicDetectorTests
{
    private const double FieldElevationFt = 13; // ~KSFO

    private static readonly IReadOnlyList<LatLon> RunwayArea =
    [
        new(37.0000, -122.0100),
        new(37.0000, -122.0000),
        new(37.0010, -122.0000),
        new(37.0010, -122.0100),
    ];

    // MagneticVariationDeg = 0, so the magnetic runway-id heading (280) equals true heading.
    private static AsdexRunwaySurface Runway(bool closed = false) => new("28R", RunwayArea, closed, 0, FieldElevationFt);

    private static AircraftState Aircraft(
        string callsign,
        double lat,
        double lon,
        double headingDeg,
        bool onGround,
        double altitudeFt,
        double speedKts,
        bool inhibited = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            IsOnGround = onGround,
            Altitude = altitudeFt,
            IndicatedAirspeed = speedKts,
        };
        ac.Stars.AsdexAlertsInhibited = inhibited;
        return ac;
    }

    // On the runway, aligned 280, rolling — an active departure/arrival.
    private static AircraftState OnRunwayAligned(string cs, double speed = 80, bool onGround = true) =>
        Aircraft(cs, 37.0005, -122.0050, 280, onGround, FieldElevationFt, speed);

    [Fact]
    public void ClosedRunway_AlignedUser_Alerts()
    {
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [OnRunwayAligned("AAL1")],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
        Assert.Equal(["AAL1"], alert.Callsigns);
        Assert.Equal(["RWY 28R", "AAL1", "RWY CLOSED"], alert.MessageLines);
        Assert.True(alert.PlayAuralAlert);
    }

    /// <summary>
    /// A departure rolling on a closed runway, and one just lifted off it, read RWY CLOSED; an arrival over the same
    /// runway at once gets an alert of its own reading GO AROUND, since each aircraft's text depends on its role.
    /// </summary>
    [Fact]
    public void ClosedRunway_Departure_Text()
    {
        AircraftState departure = WithPhase(OnRunwayAligned("UAL7433", speed: 60), new TakeoffPhase());
        AircraftState liftedOff = WithPhase(
            Aircraft("UAL5290", 37.0005, -122.0080, 280, onGround: false, FieldElevationFt + 30, speedKts: 150),
            new TakeoffPhase()
        );
        AircraftState arrival = WithPhase(
            Aircraft("UAL1660", 37.0005, -122.0020, 280, onGround: false, FieldElevationFt + 60, speedKts: 130),
            new FinalApproachPhase()
        );

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [departure, liftedOff, arrival],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        Assert.Equal(3, alerts.Count);
        AsdexSafetyAlert departureAlert = Assert.Single(alerts, a => a.Callsigns.SequenceEqual(["UAL7433"]));
        Assert.Equal("ClosedRunway|28R|UAL7433", departureAlert.Id);
        Assert.Equal(AsdexAlertKind.ClosedRunway, departureAlert.Kind);
        Assert.Equal(["RWY 28R", "UAL7433", "RWY CLOSED"], departureAlert.MessageLines);
        Assert.False(departureAlert.GoAround);
        AsdexSafetyAlert liftedOffAlert = Assert.Single(alerts, a => a.Callsigns.SequenceEqual(["UAL5290"]));
        Assert.Equal(["RWY 28R", "UAL5290", "RWY CLOSED"], liftedOffAlert.MessageLines);
        Assert.False(liftedOffAlert.GoAround);
        AsdexSafetyAlert arrivalAlert = Assert.Single(alerts, a => a.Callsigns.SequenceEqual(["UAL1660"]));
        Assert.Equal(["RWY 28R", "UAL1660", "RWY CLOSED, GO AROUND"], arrivalAlert.MessageLines);
        Assert.True(arrivalAlert.GoAround);
    }

    /// <summary>The LIMITED configuration: an aircraft using a closed runway still alerts.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_ClosedRunway_AlignedUser_StillAlerts()
    {
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [OnRunwayAligned("AAL1")],
            FieldElevationFt,
            closedRunwayAlertsOnly: true
        );

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(alerts).Kind);
    }

    /// <summary>The LIMITED configuration drops the occupied-runway alert the same scene raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_ArrivalOverLinedUpDeparture_DoesNotAlert()
    {
        AircraftState arrival = OnRunwayAligned("AAL1", speed: 130, onGround: false);
        AircraftState linedUp = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0);

        Assert.Empty(AsdexSafetyLogicDetector.Detect([Runway()], [], [arrival, linedUp], FieldElevationFt, closedRunwayAlertsOnly: true));
    }

    /// <summary>The LIMITED configuration drops the occupied-runway alert a crosser in front of a departure raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_CrosserWithActiveDeparture_DoesNotAlert()
    {
        AircraftState departure = OnRunwayAligned("AAL1", speed: 90);
        AircraftState crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12);

        Assert.Empty(AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, crosser], FieldElevationFt, closedRunwayAlertsOnly: true));
    }

    /// <summary>The LIMITED configuration drops the taxiway-landing alert the same scene raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_LowArrivalOverTaxiway_DoesNotAlert()
    {
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0020, -122.0100), new LatLon(37.0020, -122.0000));
        AircraftState lander = Aircraft("AAL1", 37.0020, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        Assert.Empty(AsdexSafetyLogicDetector.Detect([Runway()], [taxiway], [lander], FieldElevationFt, closedRunwayAlertsOnly: true));
    }

    [Fact]
    public void ClosedRunway_LoneCrosser_DoesNotAlert()
    {
        // A perpendicular taxiing aircraft is crossing, not landing/departing.
        AircraftState crosser = Aircraft("AAL1", 37.0005, -122.0050, 10, onGround: true, FieldElevationFt, speedKts: 8);
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [crosser],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );
        Assert.Empty(alerts);
    }

    [Fact]
    public void OpenRunway_SingleUser_DoesNotAlert()
    {
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [OnRunwayAligned("AAL1")],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );
        Assert.Empty(alerts);
    }

    [Fact]
    public void OccupiedRunway_ArrivalOverLinedUpDeparture_Alerts()
    {
        AircraftState arrival = OnRunwayAligned("AAL1", speed: 130, onGround: false); // airborne low over the runway
        AircraftState linedUp = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0); // holding, aligned

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [arrival, linedUp],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "UAL2"], alert.Callsigns); // the arrival first
        Assert.Equal(["RWY 28R", "AAL1, UAL2", "RWY OCCUPIED"], alert.MessageLines);
    }

    /// <summary>
    /// An arrival over a runway two aircraft are stopped on raises one alert per conflicting pair, each naming the
    /// arrival first whatever the callsigns' order, and none between the two stopped aircraft.
    /// </summary>
    [Fact]
    public void OccupiedRunway_OneAlertPerConflictingPair_RunwayUserFirst()
    {
        AircraftState arrival = OnRunwayAligned("UAL959", speed: 130, onGround: false);
        AircraftState stoppedA = Aircraft("ASA138", 37.0005, -122.0040, 190, onGround: true, FieldElevationFt, speedKts: 0);
        AircraftState stoppedB = Aircraft("QXE2871", 37.0005, -122.0030, 10, onGround: true, FieldElevationFt, speedKts: 0);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [stoppedA, stoppedB, arrival],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        // The id names the pair in ordinal order; the callsigns and the second line keep the runway user first.
        Assert.Equal(
            ["OccupiedRunway|28R|ASA138,UAL959", "OccupiedRunway|28R|QXE2871,UAL959"],
            alerts.Select(a => a.Id).Order(StringComparer.Ordinal)
        );
        AsdexSafetyAlert first = Assert.Single(alerts, a => a.Id == "OccupiedRunway|28R|ASA138,UAL959");
        Assert.Equal(AsdexAlertKind.OccupiedRunway, first.Kind);
        Assert.Equal(["UAL959", "ASA138"], first.Callsigns);
        Assert.Equal(["RWY 28R", "UAL959, ASA138", "RWY OCCUPIED"], first.MessageLines);
        Assert.Equal(["UAL959", "QXE2871"], Assert.Single(alerts, a => a.Id == "OccupiedRunway|28R|QXE2871,UAL959").Callsigns);
    }

    /// <summary>A crosser in front of a rolling departure is an occupied runway, the departure first.</summary>
    [Fact]
    public void OccupiedRunway_CrosserWithActiveDeparture_Alerts()
    {
        AircraftState departure = OnRunwayAligned("ZZZ1", speed: 90); // rolling, aligned 280
        AircraftState crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12); // crossing N

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [crosser, departure],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal("OccupiedRunway|28R|GND3,ZZZ1", alert.Id);
        Assert.Equal(["RWY 28R", "ZZZ1, GND3", "RWY OCCUPIED"], alert.MessageLines);
    }

    /// <summary>
    /// An aircraft taxiing onto the runway ahead of an arrival raises nothing while the arrival is short of the threshold,
    /// and an occupied-runway alert once the arrival is over the runway.
    /// </summary>
    [Fact]
    public void OccupiedRunway_CrosserAheadOfArrival_RaisedAtThresholdCrossing()
    {
        AircraftState crosser = Aircraft("DAL345", 37.0005, -122.0060, 10, onGround: true, FieldElevationFt, speedKts: 20);
        AircraftState shortFinal = Aircraft("UAL9338", 37.0005, -121.9990, 280, onGround: false, FieldElevationFt + 120, speedKts: 137);

        Assert.Empty(AsdexSafetyLogicDetector.Detect([Runway()], [], [crosser, shortFinal], FieldElevationFt, closedRunwayAlertsOnly: false));

        shortFinal.Position = new LatLon(37.0005, -122.0005);
        shortFinal.Altitude = FieldElevationFt + 70;
        AsdexSafetyAlert alert = Assert.Single(
            AsdexSafetyLogicDetector.Detect([Runway()], [], [crosser, shortFinal], FieldElevationFt, closedRunwayAlertsOnly: false)
        );
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["UAL9338", "DAL345"], alert.Callsigns);
        Assert.Equal(["RWY 28R", "UAL9338, DAL345", "RWY OCCUPIED"], alert.MessageLines);
    }

    [Fact]
    public void OccupiedRunway_StationaryLinedUpDeparture_StillArms()
    {
        // Regression for the LUAW-incursion case: the only "user" is a departure holding in
        // position at GS 0 (aligned). A taxiing crosser must still trigger the alert.
        AircraftState linedUp = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: true, FieldElevationFt, speedKts: 0);
        AircraftState crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [linedUp, crosser],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "GND3"], alert.Callsigns);
    }

    [Fact]
    public void Alignment_UsesTrueHeadingViaMagneticVariation()
    {
        // Runway 28R at a +15 deg-east-variation airport: true runway heading is 295. An aircraft
        // tracking 328 true is 33 deg off the true heading (within the 35 deg budget) but 48 deg
        // off the raw magnetic 280 (outside it) — so it only counts as aligned in the true frame.
        var runway = new AsdexRunwaySurface("28R", RunwayArea, IsClosed: true, MagneticVariationDeg: 15, ElevationFt: FieldElevationFt);
        AircraftState lander = Aircraft("AAL1", 37.0005, -122.0050, 328, onGround: true, FieldElevationFt, speedKts: 20);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [runway],
            [],
            [lander],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }

    [Fact]
    public void BackTaxi_OppositeHeading_IsARunwayUser_NotACrosser()
    {
        // An aircraft back-taxiing on 28R (heading 100, the reciprocal) is using the runway, not
        // crossing it (P/CG BACK-TAXI). With a rolling departure it is an occupied-runway conflict,
        // the faster runway user first.
        AircraftState departure = OnRunwayAligned("AAL1", speed: 90);
        AircraftState backTaxi = Aircraft("N123", 37.0005, -122.0040, 100, onGround: true, FieldElevationFt, speedKts: 15);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [departure, backTaxi],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "N123"], alert.Callsigns);
    }

    [Fact]
    public void ClosedRunway_LoneBackTaxi_Alerts()
    {
        AircraftState backTaxi = Aircraft("N123", 37.0005, -122.0050, 100, onGround: true, FieldElevationFt, speedKts: 15);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [backTaxi],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(alerts).Kind);
    }

    [Fact]
    public void ArrivalAgl_MeasuredFromTheRunwayElevation_NotTheField()
    {
        // A runway 500 ft above the airport reference point (a long sloping field): an arrival 250 ft
        // above the pavement is a low arrival even though it is 737 ft above the field elevation.
        var highRunway = new AsdexRunwaySurface("28R", RunwayArea, IsClosed: true, MagneticVariationDeg: 0, ElevationFt: FieldElevationFt + 500);
        AircraftState arrival = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: false, FieldElevationFt + 750, speedKts: 130);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [highRunway],
            [],
            [arrival],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(alerts).Kind);
    }

    [Fact]
    public void TaxiwayLanding_LowArrivalOverTaxiway_Alerts()
    {
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0020, -122.0100), new LatLon(37.0020, -122.0000));
        AircraftState lander = Aircraft("AAL1", 37.0020, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [taxiway],
            [lander],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.TaxiwayLanding, alert.Kind);
        Assert.Equal(["A", "AAL1", "TAXIWAY LANDING"], alert.MessageLines);
    }

    [Fact]
    public void TaxiwayLanding_ArrivalOverRunway_DoesNotAlert()
    {
        // Over the runway footprint (a normal approach), not a taxiway, even if a taxiway is near.
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0005, -122.0100), new LatLon(37.0005, -122.0000));
        AircraftState lander = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [taxiway],
            [lander],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );
        Assert.Empty(alerts);
    }

    [Fact]
    public void InhibitedAircraft_ExcludedFromAlerts()
    {
        AircraftState inhibited = OnRunwayAligned("AAL1");
        inhibited.Stars.AsdexAlertsInhibited = true;
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect(
            [Runway(closed: true)],
            [],
            [inhibited],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );
        Assert.Empty(alerts);
    }

    [Fact]
    public void StableId_SamePairAcrossTicks_IsIdentical()
    {
        AircraftState a = OnRunwayAligned("AAL1", speed: 130, onGround: false);
        AircraftState b = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0);

        IReadOnlyList<AsdexSafetyAlert> first = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [a, b],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        );
        IReadOnlyList<AsdexSafetyAlert> second = AsdexSafetyLogicDetector.Detect(
            [Runway()],
            [],
            [b, a],
            FieldElevationFt,
            closedRunwayAlertsOnly: false
        ); // order swapped

        Assert.Equal(first[0].Id, second[0].Id);
    }

    /// <summary>
    /// A pair keeps one id however its ranking moves, so a standing alert is never deleted and re-raised (vNAS sends an
    /// alert once): two aircraft rolling on the runway trade the lead in ground speed, and an arrival touches down behind
    /// a rolling departure.
    /// </summary>
    [Fact]
    public void OccupiedRunway_RankChangesAcrossTicks_KeepOneId()
    {
        AircraftState a = Aircraft("UAL2", 37.0005, -122.0050, 280, onGround: true, FieldElevationFt, speedKts: 60);
        AircraftState b = Aircraft("AAL1", 37.0005, -122.0030, 280, onGround: true, FieldElevationFt, speedKts: 40);
        AsdexSafetyAlert before = Assert.Single(
            AsdexSafetyLogicDetector.Detect([Runway()], [], [a, b], FieldElevationFt, closedRunwayAlertsOnly: false)
        );
        a.IndicatedAirspeed = 40;
        b.IndicatedAirspeed = 60;
        AsdexSafetyAlert after = Assert.Single(
            AsdexSafetyLogicDetector.Detect([Runway()], [], [a, b], FieldElevationFt, closedRunwayAlertsOnly: false)
        );

        Assert.Equal(["UAL2", "AAL1"], before.Callsigns);
        Assert.Equal(["AAL1", "UAL2"], after.Callsigns);
        Assert.Equal("OccupiedRunway|28R|AAL1,UAL2", before.Id);
        Assert.Equal(before.Id, after.Id);

        AircraftState departure = WithPhase(OnRunwayAligned("DAL3", speed: 90), new TakeoffPhase());
        AircraftState arrival = WithPhase(
            Aircraft("SKW4", 37.0005, -122.0010, 280, onGround: false, FieldElevationFt + 20, speedKts: 130),
            new FinalApproachPhase()
        );
        AsdexSafetyAlert airborne = Assert.Single(
            AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, arrival], FieldElevationFt, closedRunwayAlertsOnly: false)
        );
        arrival.IsOnGround = true;
        arrival.Altitude = FieldElevationFt;
        arrival.IndicatedAirspeed = 120;
        WithPhase(arrival, new LandingPhase());
        AsdexSafetyAlert touchedDown = Assert.Single(
            AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, arrival], FieldElevationFt, closedRunwayAlertsOnly: false)
        );

        Assert.Equal("OccupiedRunway|28R|DAL3,SKW4", airborne.Id);
        Assert.Equal(airborne.Id, touchedDown.Id);
        Assert.Equal(["SKW4", "DAL3"], touchedDown.Callsigns);
    }

    /// <summary>A stop-and-go is an arrival using the runway, through its stop and its takeoff roll.</summary>
    [Fact]
    public void Role_StopAndGo_IsArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, AsdexRunwayUsers.Role(WithPhase(OnRunwayAligned("UAL1"), new StopAndGoPhase())));

    /// <summary>A go-around stays a runway user (an arrival) until it is past the threshold, as vNAS treats it.</summary>
    [Fact]
    public void Role_GoAround_IsArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, AsdexRunwayUsers.Role(WithPhase(OnRunwayAligned("UAL1", onGround: false), new GoAroundPhase())));

    /// <summary>An aircraft lining up is the same departure as one lined up and waiting, only earlier.</summary>
    [Fact]
    public void Role_LineUp_IsDeparture() =>
        Assert.Equal(AsdexRunwayUserRole.Departure, AsdexRunwayUsers.Role(WithPhase(OnRunwayAligned("UAL1", speed: 5), new LineUpPhase())));

    /// <summary>A land-and-hold-short arrival stopped on the runway is an occupant, not a user.</summary>
    [Fact]
    public void Role_RunwayHolding_IsOther() =>
        Assert.Equal(AsdexRunwayUserRole.Other, AsdexRunwayUsers.Role(WithPhase(OnRunwayAligned("UAL1", speed: 0), new RunwayHoldingPhase("10L"))));

    private static AircraftState WithPhase(AircraftState aircraft, Phase phase)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        return aircraft;
    }
}
