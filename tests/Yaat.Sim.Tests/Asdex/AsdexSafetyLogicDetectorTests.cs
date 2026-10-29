using System.Collections.Immutable;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Simulation.Asdex;

namespace Yaat.Sim.Tests.Asdex;

/// <summary>
/// ASDE-X Safety Logic detector — closed runway, occupied runway and taxiway landing, with the alert texts, per-pair alerts
/// and runway-user triggers measured on vNAS (yaat-server docs/plans/asdex-probe-sfo), on SFO's real runways and seeded
/// footprints. The runway the tests put aircraft on is 28R, configured under the id "28R"; aircraft are placed along its
/// centreline from the 28R threshold, aligned by track.
/// </summary>
public class AsdexSafetyLogicDetectorTests
{
    private const string Id28R = "28R";

    private readonly IReadOnlyList<AsdexHoldBarRunway> _runways;
    private readonly RunwayInfo _r28R;
    private readonly double _fieldElevationFt;
    private readonly double _runwayElevationFt;

    public AsdexSafetyLogicDetectorTests()
    {
        TestVnasData.EnsureInitialized();
        _runways =
        [
            .. AsdexHoldBarBuilderTests
                .SfoRunways()
                .Select(runway => (runway.Runway.Id.End1 == "28R") || (runway.Runway.Id.End2 == "28R") ? runway with { Id = Id28R } : runway),
        ];
        _r28R = Runway28R().Runway.ForApproach("28R");
        _fieldElevationFt = NavigationDatabase.Instance.GetAirportElevation("SFO") ?? throw new InvalidOperationException("SFO has no elevation");
        _runwayElevationFt = (_r28R.Elevation1Ft + _r28R.Elevation2Ft) / 2;
    }

    private AsdexHoldBarRunway Runway28R() => _runways.Single(runway => runway.Id == Id28R);

    /// <summary>Every SFO runway, 28R closed when <paramref name="closed"/>, each with its own footprint.</summary>
    private AsdexSafetyLogicRunways Runways(bool closed) => Runways(closed, _runways);

    private static AsdexSafetyLogicRunways Runways(bool closed, IReadOnlyList<AsdexHoldBarRunway> runways) =>
        new(
            [.. runways.Select(runway => new AsdexRunwaySurface(runway, new AsdexRunwayAxis(runway.Runway), closed && (runway.Id == Id28R)))],
            [.. runways.Select(runway => runway.Area)]
        );

    private IReadOnlyList<AsdexSafetyAlert> Detect(IReadOnlyList<AircraftState> aircraft, bool closed, bool closedRunwayAlertsOnly) =>
        DetectAlerts(Runways(closed), [], aircraft, _fieldElevationFt, closedRunwayAlertsOnly);

    /// <summary>One tick on the open runways with <paramref name="oppDirPairs"/> as the previous tick's pair memory.</summary>
    private AsdexDetection DetectWith(IReadOnlyList<AircraftState> aircraft, bool closedRunwayAlertsOnly, ImmutableSortedSet<string> oppDirPairs) =>
        AsdexSafetyLogicDetector.Detect(
            Runways(closed: false),
            [],
            aircraft,
            _fieldElevationFt,
            new AsdexDetectionInputs(closedRunwayAlertsOnly, oppDirPairs)
        );

    /// <summary>One tick's alerts with no pair remembered from a previous tick.</summary>
    private static IReadOnlyList<AsdexSafetyAlert> DetectAlerts(
        AsdexSafetyLogicRunways runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt,
        bool closedRunwayAlertsOnly
    ) =>
        AsdexSafetyLogicDetector
            .Detect(runways, taxiways, aircraft, fieldElevationFt, new AsdexDetectionInputs(closedRunwayAlertsOnly, AsdexDetection.NoOppDirPairs))
            .Alerts;

    /// <summary>The aircraft's role on 28R.</summary>
    private AsdexRunwayUserRole RoleOf(AircraftState aircraft)
    {
        AsdexRunwayUses uses = AsdexRunwayUsers.UsesOf(
            aircraft,
            _runways,
            [.. _runways.Select(runway => new AsdexRunwayAxis(runway.Runway))],
            _fieldElevationFt
        );
        return AsdexRunwayUsers.Role(aircraft, AsdexRunwayUsers.ArrivesOn(uses, _runways, _runways.ToList().IndexOf(Runway28R())));
    }

    /// <summary>
    /// The point <paramref name="alongFt"/> past the 28R threshold on its centreline (negative before it),
    /// <paramref name="lateralFt"/> to its right.
    /// </summary>
    private LatLon On28R(double alongFt, double lateralFt)
    {
        var threshold = new LatLon(_r28R.ThresholdLatitude, _r28R.ThresholdLongitude);
        LatLon centre =
            alongFt >= 0
                ? GeoMath.ProjectPoint(threshold, _r28R.TrueHeading, alongFt / GeoMath.FeetPerNm)
                : GeoMath.ProjectPoint(threshold, new TrueHeading(_r28R.TrueHeading.Degrees + 180), -alongFt / GeoMath.FeetPerNm);
        return lateralFt == 0 ? centre : GeoMath.ProjectPoint(centre, new TrueHeading(_r28R.TrueHeading.Degrees + 90), lateralFt / GeoMath.FeetPerNm);
    }

    /// <summary>
    /// An aircraft <paramref name="alongFt"/> down 28R, <paramref name="heightFt"/> above the runway when airborne, heading and
    /// tracking <paramref name="offsetDeg"/> off 28R's direction (90 crossing it, 180 back-taxiing).
    /// </summary>
    private AircraftState Aircraft(string callsign, double alongFt, bool onGround, double speedKts, double offsetDeg, double heightFt)
    {
        double trackDeg = _r28R.TrueHeading.Degrees + offsetDeg;
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = On28R(alongFt, 0),
            TrueHeading = new TrueHeading(trackDeg),
            TrueTrack = new TrueHeading(trackDeg),
            IsOnGround = onGround,
            Altitude = _runwayElevationFt + (onGround ? 0 : heightFt),
            IndicatedAirspeed = speedKts,
        };
    }

    /// <summary>An aircraft stopped or taxiing on 28R's centreline, <paramref name="offsetDeg"/> off its direction.</summary>
    private AircraftState OnGround(string callsign, double alongFt, double speedKts, double offsetDeg) =>
        Aircraft(callsign, alongFt, onGround: true, speedKts, offsetDeg, heightFt: 0);

    /// <summary>An aircraft airborne over (or before) 28R's centreline, aligned with it, <paramref name="heightFt"/> above the runway.</summary>
    private AircraftState Airborne(string callsign, double alongFt, double speedKts, double heightFt) =>
        Aircraft(callsign, alongFt, onGround: false, speedKts, offsetDeg: 0, heightFt);

    /// <summary>Rolling down 28R 3,000 ft from its threshold at <paramref name="speed"/> (a runway user from 50 kt).</summary>
    private AircraftState Rolling(string cs, double speed) => OnGround(cs, 3000, speed, offsetDeg: 0);

    [Fact]
    public void ClosedRunway_AlignedUser_Alerts()
    {
        AsdexSafetyAlert alert = Assert.Single(Detect([Rolling("AAL1", 80)], closed: true, closedRunwayAlertsOnly: false));

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
        AircraftState departure = WithPhase(Rolling("UAL7433", 60), new TakeoffPhase());
        AircraftState liftedOff = WithPhase(Airborne("UAL5290", 7000, speedKts: 150, heightFt: 30), new TakeoffPhase());
        AircraftState arrival = WithPhase(Airborne("UAL1660", 500, speedKts: 130, heightFt: 60), new FinalApproachPhase());

        IReadOnlyList<AsdexSafetyAlert> alerts = Detect([departure, liftedOff, arrival], closed: true, closedRunwayAlertsOnly: false);

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

    /// <summary>
    /// A closed runway whose configuration carries no footprint still alerts for an arrival on final: the arrival box is
    /// measured from navdata's threshold, not the footprint.
    /// </summary>
    [Fact]
    public void ClosedRunway_WithNoFootprint_AlertsForAnArrivalOnFinal()
    {
        List<AsdexHoldBarRunway> runways = [.. _runways.Select(runway => runway.Id == Id28R ? runway with { Area = [] } : runway)];
        AircraftState arrival = WithPhase(Airborne("UAL1660", -3000, speedKts: 140, heightFt: 160), new FinalApproachPhase());

        AsdexSafetyAlert alert = Assert.Single(
            DetectAlerts(Runways(closed: true, runways), [], [arrival], _fieldElevationFt, closedRunwayAlertsOnly: false)
        );
        Assert.Equal(["RWY 28R", "UAL1660", "RWY CLOSED, GO AROUND"], alert.MessageLines);
    }

    /// <summary>The LIMITED configuration: an aircraft using a closed runway still alerts.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_ClosedRunway_AlignedUser_StillAlerts() =>
        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(Detect([Rolling("AAL1", 80)], closed: true, closedRunwayAlertsOnly: true)).Kind);

    /// <summary>The LIMITED configuration drops the occupied-runway alert the same scene raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_ArrivalOverLinedUpDeparture_DoesNotAlert()
    {
        AircraftState arrival = Airborne("AAL1", 500, speedKts: 130, heightFt: 60);
        AircraftState linedUp = OnGround("UAL2", 1500, speedKts: 0, offsetDeg: 0);

        Assert.Empty(Detect([arrival, linedUp], closed: false, closedRunwayAlertsOnly: true));
    }

    /// <summary>The LIMITED configuration drops the occupied-runway alert a crosser in front of a departure raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_CrosserWithActiveDeparture_DoesNotAlert()
    {
        AircraftState departure = Rolling("AAL1", 90);
        AircraftState crosser = OnGround("GND3", 4000, speedKts: 12, offsetDeg: 90);

        Assert.Empty(Detect([departure, crosser], closed: false, closedRunwayAlertsOnly: true));
    }

    /// <summary>The LIMITED configuration drops the taxiway-landing alert the same scene raises otherwise.</summary>
    [Fact]
    public void ClosedRunwayAlertsOnly_LowArrivalOverTaxiway_DoesNotAlert()
    {
        (AsdexTaxiwaySegment taxiway, AircraftState lander) = LanderOverATaxiwayOffTheRunways();

        Assert.Empty(DetectAlerts(Runways(closed: false), [taxiway], [lander], _fieldElevationFt, closedRunwayAlertsOnly: true));
    }

    [Fact]
    public void ClosedRunway_LoneCrosser_DoesNotAlert() =>
        // A perpendicular taxiing aircraft is crossing, not landing/departing.
        Assert.Empty(Detect([OnGround("AAL1", 3000, speedKts: 8, offsetDeg: 90)], closed: true, closedRunwayAlertsOnly: false));

    [Fact]
    public void OpenRunway_SingleUser_DoesNotAlert() => Assert.Empty(Detect([Rolling("AAL1", 80)], closed: false, closedRunwayAlertsOnly: false));

    [Fact]
    public void OccupiedRunway_ArrivalOverLinedUpDeparture_Alerts()
    {
        AircraftState arrival = Airborne("AAL1", 500, speedKts: 130, heightFt: 60); // airborne low over the runway
        AircraftState linedUp = OnGround("UAL2", 1500, speedKts: 0, offsetDeg: 0); // holding, aligned

        AsdexSafetyAlert alert = Assert.Single(Detect([arrival, linedUp], closed: false, closedRunwayAlertsOnly: false));
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
        AircraftState arrival = Airborne("UAL959", 500, speedKts: 130, heightFt: 60);
        AircraftState stoppedA = OnGround("ASA138", 1500, speedKts: 0, offsetDeg: 180);
        AircraftState stoppedB = OnGround("QXE2871", 2500, speedKts: 0, offsetDeg: 90);

        IReadOnlyList<AsdexSafetyAlert> alerts = Detect([stoppedA, stoppedB, arrival], closed: false, closedRunwayAlertsOnly: false);

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
        AircraftState departure = Rolling("ZZZ1", 90); // rolling, aligned
        AircraftState crosser = OnGround("GND3", 4000, speedKts: 12, offsetDeg: 90); // crossing

        AsdexSafetyAlert alert = Assert.Single(Detect([crosser, departure], closed: false, closedRunwayAlertsOnly: false));
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
        AircraftState crosser = OnGround("DAL345", 3000, speedKts: 20, offsetDeg: 90);
        AircraftState shortFinal = Airborne("UAL9338", -1000, speedKts: 137, heightFt: 120);

        Assert.Empty(Detect([crosser, shortFinal], closed: false, closedRunwayAlertsOnly: false));

        shortFinal.Position = On28R(300, 0);
        shortFinal.Altitude = _runwayElevationFt + 70;
        AsdexSafetyAlert alert = Assert.Single(Detect([crosser, shortFinal], closed: false, closedRunwayAlertsOnly: false));
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["UAL9338", "DAL345"], alert.Callsigns);
        Assert.Equal(["RWY 28R", "UAL9338, DAL345", "RWY OCCUPIED"], alert.MessageLines);
    }

    /// <summary>
    /// An airborne aircraft over the footprint that is not a runway user (a hover at 5 kt) occupies the runway below 200 ft
    /// above it only: a rolling departure raises an occupied-runway alert against it at 150 ft and none at 250 ft.
    /// </summary>
    [Fact]
    public void OccupiedRunway_AnAirborneNonUserOverTheFootprint_ConflictsAt150Ft_NotAt250Ft()
    {
        AircraftState departure = Rolling("AAL1", 90);
        AircraftState low = Airborne("N1H", 5000, speedKts: 5, heightFt: 150);
        AircraftState high = Airborne("N2H", 5000, speedKts: 5, heightFt: 250);

        Assert.Equal(["AAL1", "N1H"], Assert.Single(Detect([departure, low], closed: false, closedRunwayAlertsOnly: false)).Callsigns);
        Assert.Empty(Detect([departure, high], closed: false, closedRunwayAlertsOnly: false));
    }

    /// <summary>
    /// A departure lined up and waiting at 0 kt is not a runway user (vNAS raises nothing for a lined-up aircraft), so a
    /// crosser taxiing across in front of it raises nothing either.
    /// </summary>
    [Fact]
    public void OccupiedRunway_StationaryLinedUpDepartureAndACrosser_RaiseNothing()
    {
        AircraftState linedUp = WithPhase(OnGround("AAL1", 300, speedKts: 0, offsetDeg: 0), new LinedUpAndWaitingPhase());
        AircraftState crosser = OnGround("GND3", 3000, speedKts: 12, offsetDeg: 90);

        Assert.Empty(Detect([linedUp, crosser], closed: false, closedRunwayAlertsOnly: false));
    }

    /// <summary>
    /// Alignment is by true track against the runway's true bearing from its navdata ends, the heading playing no part: a
    /// rolling aircraft tracking 33° off 28R uses it whatever its nose points at, and one tracking 40° off does not.
    /// </summary>
    [Fact]
    public void Alignment_IsByTrueTrack_AgainstTheNavdataRunwayBearing()
    {
        AsdexRunwayAxis axis = new(Runway28R().Runway);
        double runwayBearingDeg =
            GeoMath.AbsBearingDifference(axis.BearingDeg, _r28R.TrueHeading.Degrees) < 90 ? axis.BearingDeg : axis.BearingDeg + 180;
        AircraftState aligned = Rolling("AAL1", 60);
        aligned.TrueTrack = new TrueHeading(runwayBearingDeg + 33);
        aligned.TrueHeading = new TrueHeading(runwayBearingDeg + 90);
        AircraftState offAxis = Rolling("AAL2", 60);
        offAxis.TrueTrack = new TrueHeading(runwayBearingDeg + 40);
        offAxis.TrueHeading = new TrueHeading(runwayBearingDeg);

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(Detect([aligned], closed: true, closedRunwayAlertsOnly: false)).Kind);
        Assert.Empty(Detect([offAxis], closed: true, closedRunwayAlertsOnly: false));
    }

    /// <summary>
    /// An aircraft back-taxiing on 28R at 15 kt is below the rolling speed, so it is not a runway user but an occupant of
    /// the runway: ahead of a rolling departure and moving toward it at 5 kt or more, the pair is an opposite-direction
    /// taxi, the departure first.
    /// </summary>
    [Fact]
    public void BackTaxiAt15Kt_IsAnOccupant_OfARollingDeparturesRunway()
    {
        AircraftState departure = Rolling("AAL1", 90);
        AircraftState backTaxi = OnGround("N123", 4000, speedKts: 15, offsetDeg: 180);

        AsdexSafetyAlert alert = Assert.Single(Detect([departure, backTaxi], closed: false, closedRunwayAlertsOnly: false));
        Assert.Equal(AsdexAlertKind.OppDirTaxi, alert.Kind);
        Assert.Equal(["AAL1", "N123"], alert.Callsigns);
    }

    /// <summary>A back-taxi at 15 kt alone on a closed runway is not a runway user, so it raises nothing.</summary>
    [Fact]
    public void ClosedRunway_LoneBackTaxiAt15Kt_RaisesNothing() =>
        Assert.Empty(Detect([OnGround("N123", 3000, speedKts: 15, offsetDeg: 180)], closed: true, closedRunwayAlertsOnly: false));

    /// <summary>
    /// An airborne aircraft over the runway is a user below 200 ft above the runway's own elevation, whatever the field
    /// elevation the detector is given: here one 500 ft above the runway, which cannot change the result.
    /// </summary>
    [Fact]
    public void OverRunwayHeight_IsMeasuredFromTheRunway_NotTheField_Below200Ft()
    {
        AircraftState low = Airborne("AAL1", 3000, speedKts: 130, heightFt: 190);
        AircraftState high = Airborne("AAL2", 3000, speedKts: 130, heightFt: 210);

        Assert.Equal(
            AsdexAlertKind.ClosedRunway,
            Assert.Single(DetectAlerts(Runways(closed: true), [], [low], _runwayElevationFt + 500, closedRunwayAlertsOnly: false)).Kind
        );
        Assert.Empty(DetectAlerts(Runways(closed: true), [], [high], _runwayElevationFt + 500, closedRunwayAlertsOnly: false));
    }

    [Fact]
    public void TaxiwayLanding_LowArrivalOverTaxiway_Alerts()
    {
        (AsdexTaxiwaySegment taxiway, AircraftState lander) = LanderOverATaxiwayOffTheRunways();

        AsdexSafetyAlert alert = Assert.Single(
            DetectAlerts(Runways(closed: false), [taxiway], [lander], _fieldElevationFt, closedRunwayAlertsOnly: false)
        );
        Assert.Equal(AsdexAlertKind.TaxiwayLanding, alert.Kind);
        Assert.Equal(["A", "AAL1", "TAXIWAY LANDING"], alert.MessageLines);
    }

    [Fact]
    public void TaxiwayLanding_ArrivalOverRunway_DoesNotAlert()
    {
        // Over the runway footprint (a normal approach), not a taxiway, even if a taxiway is near.
        var taxiway = new AsdexTaxiwaySegment("A", On28R(1000, 0), On28R(5000, 0));
        AircraftState lander = Airborne("AAL1", 3000, speedKts: 120, heightFt: 60);

        Assert.Empty(DetectAlerts(Runways(closed: false), [taxiway], [lander], _fieldElevationFt, closedRunwayAlertsOnly: false));
    }

    [Fact]
    public void InhibitedAircraft_ExcludedFromAlerts()
    {
        AircraftState inhibited = Rolling("AAL1", 80);
        inhibited.Stars.AsdexAlertsInhibited = true;

        Assert.Empty(Detect([inhibited], closed: true, closedRunwayAlertsOnly: false));
    }

    [Fact]
    public void StableId_SamePairAcrossTicks_IsIdentical()
    {
        AircraftState a = Airborne("AAL1", 500, speedKts: 130, heightFt: 60);
        AircraftState b = OnGround("UAL2", 1500, speedKts: 0, offsetDeg: 0);

        IReadOnlyList<AsdexSafetyAlert> first = Detect([a, b], closed: false, closedRunwayAlertsOnly: false);
        IReadOnlyList<AsdexSafetyAlert> second = Detect([b, a], closed: false, closedRunwayAlertsOnly: false); // order swapped

        Assert.Equal(first[0].Id, second[0].Id);
    }

    /// <summary>
    /// A pair keeps one id however its ranking moves, so a standing alert is never deleted and re-raised (vNAS sends an
    /// alert once): two aircraft on the runway trade the lead in ground speed (the one under 50 kt occupying it), and an
    /// arrival touches down behind a rolling departure.
    /// </summary>
    [Fact]
    public void OccupiedRunway_RankChangesAcrossTicks_KeepOneId()
    {
        AircraftState a = OnGround("UAL2", 3000, speedKts: 60, offsetDeg: 0);
        AircraftState b = OnGround("AAL1", 5000, speedKts: 40, offsetDeg: 0);
        AsdexSafetyAlert before = Assert.Single(Detect([a, b], closed: false, closedRunwayAlertsOnly: false));
        a.IndicatedAirspeed = 40;
        b.IndicatedAirspeed = 60;
        AsdexSafetyAlert after = Assert.Single(Detect([a, b], closed: false, closedRunwayAlertsOnly: false));

        Assert.Equal(["UAL2", "AAL1"], before.Callsigns);
        Assert.Equal(["AAL1", "UAL2"], after.Callsigns);
        Assert.Equal("OccupiedRunway|28R|AAL1,UAL2", before.Id);
        Assert.Equal(before.Id, after.Id);

        AircraftState departure = WithPhase(Rolling("DAL3", 90), new TakeoffPhase());
        AircraftState arrival = WithPhase(Airborne("SKW4", 300, speedKts: 130, heightFt: 20), new FinalApproachPhase());
        AsdexSafetyAlert airborne = Assert.Single(Detect([departure, arrival], closed: false, closedRunwayAlertsOnly: false));
        arrival.IsOnGround = true;
        arrival.Altitude = _runwayElevationFt;
        arrival.IndicatedAirspeed = 120;
        WithPhase(arrival, new LandingPhase());
        AsdexSafetyAlert touchedDown = Assert.Single(Detect([departure, arrival], closed: false, closedRunwayAlertsOnly: false));

        Assert.Equal("OccupiedRunway|28R|DAL3,SKW4", airborne.Id);
        Assert.Equal(airborne.Id, touchedDown.Id);
        Assert.Equal(["SKW4", "DAL3"], touchedDown.Callsigns);
    }

    /// <summary>
    /// A departure rolling toward 10L and an arrival crossing the 28R threshold head on raise one OPP DIR DEPARTURE alert in
    /// place of RWY OCCUPIED, the departure first though the arrival ranks first (as vNAS sends it, README:333).
    /// </summary>
    [Fact]
    public void OppDirDeparture_RollingDepartureAndLandingArrivalOnTheSameRunway_AtArrivalThreshold()
    {
        AircraftState departure = WithPhase(OnGround("DAL1414", 8000, speedKts: 85, offsetDeg: 180), new TakeoffPhase());
        AircraftState arrival = WithPhase(Airborne("SKW2912", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());

        AsdexSafetyAlert alert = Assert.Single(Detect([arrival, departure], closed: false, closedRunwayAlertsOnly: false));

        Assert.Equal(AsdexAlertKind.OppDirDeparture, alert.Kind);
        Assert.Equal(["DAL1414", "SKW2912"], alert.Callsigns);
        Assert.Equal([Id28R], alert.RunwayIds);
        Assert.Equal(["RWY 28R", "DAL1414, SKW2912", "OPP DIR DEPARTURE"], alert.MessageLines);
    }

    /// <summary>
    /// A lander rolling out toward 10L at 110 kt and a second crossing the 28R threshold at 137 kt, both runway users, raise
    /// OPP DIR LANDER with the rolled-out lander first though the second is faster; when the first slows below 50 kt still
    /// rolling toward the other, it is an occupant moving toward the remaining user, and the alert turns into OPP DIR TAXI
    /// under a new id, the remaining user first (README:345-350).
    /// </summary>
    [Fact]
    public void OppDirLander_TwoLandersHeadOn_BothUsers()
    {
        AircraftState firstLander = WithPhase(OnGround("DAL2957", 8000, speedKts: 110, offsetDeg: 180), new LandingPhase());
        AircraftState secondLander = WithPhase(Airborne("JBU9021", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());

        AsdexSafetyAlert lander = Assert.Single(Detect([secondLander, firstLander], closed: false, closedRunwayAlertsOnly: false));
        Assert.Equal(AsdexAlertKind.OppDirLander, lander.Kind);
        Assert.Equal(["DAL2957", "JBU9021"], lander.Callsigns);
        Assert.Equal(["RWY 28R", "DAL2957, JBU9021", "OPP DIR LANDER"], lander.MessageLines);

        firstLander.IndicatedAirspeed = 45;
        IReadOnlyList<AsdexSafetyAlert> slowed = Detect([secondLander, firstLander], closed: false, closedRunwayAlertsOnly: false);

        Assert.DoesNotContain(slowed, a => a.Id == lander.Id);
        AsdexSafetyAlert taxi = Assert.Single(slowed);
        Assert.Equal(AsdexAlertKind.OppDirTaxi, taxi.Kind);
        Assert.Equal(["JBU9021", "DAL2957"], taxi.Callsigns);
        Assert.Equal(["RWY 28R", "JBU9021, DAL2957", "OPP DIR TAXI"], taxi.MessageLines);
    }

    /// <summary>
    /// An arrival crossing the threshold with an aircraft taxiing toward it on the runway raises OPP DIR TAXI, the arrival
    /// first (README:357-359); once the taxiing aircraft turns 90° to turn off, the pair raises nothing, since a pair that
    /// has had an OPP DIR alert raises no RWY OCCUPIED while both are on the runway (vNAS raised nothing after OPP DIR TAXI
    /// cleared at the turn-off, README:351).
    /// </summary>
    [Fact]
    public void OppDirTaxi_ArrivalVsTaxiingAircraftHeadedAtIt()
    {
        AircraftState arrival = WithPhase(Airborne("DAL530", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());
        AircraftState taxiing = OnGround("DAL2988", 8000, speedKts: 20, offsetDeg: 180);

        AsdexDetection headOn = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, AsdexDetection.NoOppDirPairs);
        AsdexSafetyAlert taxi = Assert.Single(headOn.Alerts);
        Assert.Equal(AsdexAlertKind.OppDirTaxi, taxi.Kind);
        Assert.Equal(["DAL530", "DAL2988"], taxi.Callsigns);
        Assert.Equal(["RWY 28R", "DAL530, DAL2988", "OPP DIR TAXI"], taxi.MessageLines);
        Assert.Equal(["28R|DAL2988,DAL530"], headOn.OppDirPairs);

        TurnOff(taxiing);
        AsdexDetection turned = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, headOn.OppDirPairs);

        Assert.Empty(turned.Alerts);
        Assert.Equal(headOn.OppDirPairs, turned.OppDirPairs);
    }

    /// <summary>
    /// A judgement call: an aircraft stopped lined up at the far end, facing the arrival, is an occupied runway, not OPP DIR
    /// TAXI. vNAS raised OPP DIR TAXI for a 20 kt taxi; its one stationary sample read RWY OCCUPIED but faced off the
    /// runway axis (UAL9154, README:376-378).
    /// </summary>
    [Fact]
    public void OppDirTaxi_StoppedLinedUpOccupant()
    {
        AircraftState arrival = WithPhase(Airborne("UAL1954", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());
        AircraftState linedUp = WithPhase(OnGround("UAL9154", 10000, speedKts: 0, offsetDeg: 180), new LinedUpAndWaitingPhase());

        AsdexSafetyAlert alert = Assert.Single(Detect([arrival, linedUp], closed: false, closedRunwayAlertsOnly: false));
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["RWY 28R", "UAL1954, UAL9154", "RWY OCCUPIED"], alert.MessageLines);
    }

    /// <summary>An aircraft taxiing toward the arrival at 4 kt is an occupied runway; at the 5 kt floor it is OPP DIR TAXI.</summary>
    [Fact]
    public void OppDirTaxi_SlowOccupantBelowFloor()
    {
        AircraftState arrival = WithPhase(Airborne("DAL530", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());
        AircraftState taxiing = OnGround("DAL2988", 8000, speedKts: 4, offsetDeg: 180);

        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(Detect([taxiing, arrival], closed: false, closedRunwayAlertsOnly: false)).Kind);

        taxiing.IndicatedAirspeed = AsdexRunwayUsers.OppDirTaxiMinGroundSpeedKts;
        Assert.Equal(AsdexAlertKind.OppDirTaxi, Assert.Single(Detect([taxiing, arrival], closed: false, closedRunwayAlertsOnly: false)).Kind);
    }

    /// <summary>
    /// An aircraft back-taxiing behind a rolling departure faces the other way but moves away from it, so the pair raises
    /// RWY OCCUPIED, not OPP DIR TAXI: opposite direction means toward each other.
    /// </summary>
    [Fact]
    public void OppDir_PairMovingApart_RaisesNoOppDir()
    {
        AircraftState departure = WithPhase(Rolling("AAL1", 90), new TakeoffPhase());
        AircraftState behind = OnGround("N123", 1000, speedKts: 15, offsetDeg: 180);

        AsdexSafetyAlert alert = Assert.Single(Detect([departure, behind], closed: false, closedRunwayAlertsOnly: false));
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "N123"], alert.Callsigns);
    }

    /// <summary>The LIMITED configuration drops the opposite-direction alerts as it drops RWY OCCUPIED.</summary>
    [Fact]
    public void LimitedConfiguration_OppDir_RaisesNothing()
    {
        AircraftState departure = WithPhase(OnGround("DAL1414", 8000, speedKts: 85, offsetDeg: 180), new TakeoffPhase());
        AircraftState arrival = WithPhase(Airborne("SKW2912", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());
        AircraftState taxiing = OnGround("DAL2988", 6000, speedKts: 5, offsetDeg: 180);

        Assert.Empty(Detect([arrival, departure, taxiing], closed: false, closedRunwayAlertsOnly: true));
    }

    /// <summary>
    /// A remembered pair is forgotten once the taxiing aircraft leaves the runway footprint: back on the runway later, turned
    /// across it, the same pair raises RWY OCCUPIED again.
    /// </summary>
    [Fact]
    public void OppDirPairMemory_DropsWhenTheOccupantLeavesTheFootprint()
    {
        (AircraftState arrival, AircraftState taxiing, AsdexDetection turnedOff) = ArrivalAndTurnedOffTaxi();

        taxiing.Position = On28R(8000, 1500);
        AsdexDetection left = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, turnedOff.OppDirPairs);
        Assert.Empty(left.Alerts);
        Assert.Empty(left.OppDirPairs);

        taxiing.Position = On28R(8000, 0);
        AsdexDetection back = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, left.OppDirPairs);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(back.Alerts).Kind);
        Assert.Empty(back.OppDirPairs);
    }

    /// <summary>
    /// A remembered pair is kept while both aircraft occupy the runway, even with neither a runway user: the arrival slowed
    /// below 50 kt and the turned-off taxier keep the key; the arrival re-accelerating, not heading at the taxier, raises no
    /// RWY OCCUPIED; once the taxier leaves the footprint the key is dropped.
    /// </summary>
    [Fact]
    public void OppDirPairMemory_BothSlowOnTheRunway_KeepsTheKey()
    {
        (AircraftState arrival, AircraftState taxiing, AsdexDetection turnedOff) = ArrivalAndTurnedOffTaxi();
        arrival.IsOnGround = true;
        arrival.Altitude = _runwayElevationFt;
        arrival.IndicatedAirspeed = 40;

        AsdexDetection bothSlow = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, turnedOff.OppDirPairs);
        Assert.Empty(bothSlow.Alerts);
        Assert.Equal(turnedOff.OppDirPairs, bothSlow.OppDirPairs);

        arrival.IndicatedAirspeed = 60;
        AsdexDetection reaccelerated = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, bothSlow.OppDirPairs);
        Assert.Empty(reaccelerated.Alerts);
        Assert.Equal(turnedOff.OppDirPairs, reaccelerated.OppDirPairs);

        taxiing.Position = On28R(8000, 1500);
        AsdexDetection left = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, reaccelerated.OppDirPairs);
        Assert.Empty(left.Alerts);
        Assert.Empty(left.OppDirPairs);
    }

    /// <summary>An OPP DIR LANDER pair that turns into OPP DIR TAXI is one remembered pair: the key carries no kind.</summary>
    [Fact]
    public void OppDirPairMemory_LanderToTaxiFlip_KeepsOneKey()
    {
        AircraftState firstLander = WithPhase(OnGround("DAL2957", 8000, speedKts: 110, offsetDeg: 180), new LandingPhase());
        AircraftState secondLander = WithPhase(Airborne("JBU9021", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());

        AsdexDetection lander = DetectWith([secondLander, firstLander], closedRunwayAlertsOnly: false, AsdexDetection.NoOppDirPairs);
        firstLander.IndicatedAirspeed = 45;
        AsdexDetection taxi = DetectWith([secondLander, firstLander], closedRunwayAlertsOnly: false, lander.OppDirPairs);

        Assert.Equal(AsdexAlertKind.OppDirLander, Assert.Single(lander.Alerts).Kind);
        Assert.Equal(AsdexAlertKind.OppDirTaxi, Assert.Single(taxi.Alerts).Kind);
        Assert.Equal(["28R|DAL2957,JBU9021"], lander.OppDirPairs);
        Assert.Equal(lander.OppDirPairs, taxi.OppDirPairs);
    }

    /// <summary>
    /// A switch to the LIMITED configuration, which evaluates no open runway, forgets the pair: back on a named
    /// configuration the turned-off pair raises RWY OCCUPIED.
    /// </summary>
    [Fact]
    public void OppDirPairMemory_LimitedSwitch_DropsTheKey()
    {
        (AircraftState arrival, AircraftState taxiing, AsdexDetection turnedOff) = ArrivalAndTurnedOffTaxi();

        AsdexDetection limited = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: true, turnedOff.OppDirPairs);
        Assert.Empty(limited.OppDirPairs);

        AsdexDetection named = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, limited.OppDirPairs);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(named.Alerts).Kind);
    }

    /// <summary>Inhibiting one of the pair takes it out of every alert, which forgets the pair; un-inhibited, it reads RWY OCCUPIED.</summary>
    [Fact]
    public void OppDirPairMemory_Inhibit_DropsTheKey()
    {
        (AircraftState arrival, AircraftState taxiing, AsdexDetection turnedOff) = ArrivalAndTurnedOffTaxi();

        taxiing.Stars.AsdexAlertsInhibited = true;
        AsdexDetection inhibited = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, turnedOff.OppDirPairs);
        Assert.Empty(inhibited.OppDirPairs);

        taxiing.Stars.AsdexAlertsInhibited = false;
        AsdexDetection released = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, inhibited.OppDirPairs);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(released.Alerts).Kind);
    }

    /// <summary>
    /// An arrival over the threshold and an aircraft taxiing toward it that raised OPP DIR TAXI and has since turned 90° to
    /// turn off: the detection after the turn, which raises nothing and remembers the pair.
    /// </summary>
    private (AircraftState Arrival, AircraftState Taxiing, AsdexDetection TurnedOff) ArrivalAndTurnedOffTaxi()
    {
        AircraftState arrival = WithPhase(Airborne("DAL530", 150, speedKts: 137, heightFt: 60), new FinalApproachPhase());
        AircraftState taxiing = OnGround("DAL2988", 8000, speedKts: 20, offsetDeg: 180);
        AsdexDetection headOn = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, AsdexDetection.NoOppDirPairs);
        Assert.Equal(AsdexAlertKind.OppDirTaxi, Assert.Single(headOn.Alerts).Kind);

        TurnOff(taxiing);
        AsdexDetection turnedOff = DetectWith([taxiing, arrival], closedRunwayAlertsOnly: false, headOn.OppDirPairs);
        Assert.Empty(turnedOff.Alerts);
        Assert.Single(turnedOff.OppDirPairs);
        return (arrival, taxiing, turnedOff);
    }

    /// <summary>Turns an aircraft on 28R 90° off the runway direction, heading and track, as it starts a turn-off.</summary>
    private void TurnOff(AircraftState aircraft)
    {
        aircraft.TrueHeading = new TrueHeading(_r28R.TrueHeading.Degrees + 270);
        aircraft.TrueTrack = new TrueHeading(_r28R.TrueHeading.Degrees + 270);
    }

    /// <summary>A stop-and-go braking toward its stop is an arrival using the runway (7110.65 §3-8-2).</summary>
    [Fact]
    public void Role_StopAndGoBeforeItsStop_IsArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, RoleOf(WithPhase(Rolling("UAL1", 80), new StopAndGoPhase())));

    /// <summary>A go-around stays a runway user (an arrival) until it is past the threshold, as vNAS treats it.</summary>
    [Fact]
    public void Role_GoAround_IsArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, RoleOf(WithPhase(Airborne("UAL1", 3000, speedKts: 80, heightFt: 60), new GoAroundPhase())));

    /// <summary>An aircraft lining up at 5 kt is on the ground below the rolling speed: an occupant, whatever its phase.</summary>
    [Fact]
    public void Role_LineUpBelow50Kt_IsOther() => Assert.Equal(AsdexRunwayUserRole.Other, RoleOf(WithPhase(Rolling("UAL1", 5), new LineUpPhase())));

    /// <summary>A land-and-hold-short arrival stopped on the runway is an occupant, not a user.</summary>
    [Fact]
    public void Role_RunwayHolding_IsOther() =>
        Assert.Equal(AsdexRunwayUserRole.Other, RoleOf(WithPhase(Rolling("UAL1", 0), new RunwayHoldingPhase("10L"))));

    /// <summary>A taxiway 1,500 ft right of the 28R centreline, off every runway footprint, and an arrival 60 ft above the field over it.</summary>
    private (AsdexTaxiwaySegment Taxiway, AircraftState Lander) LanderOverATaxiwayOffTheRunways()
    {
        var taxiway = new AsdexTaxiwaySegment("A", On28R(1000, 1500), On28R(5000, 1500));
        AircraftState lander = Airborne("AAL1", 3000, speedKts: 120, heightFt: 0);
        lander.Position = On28R(3000, 1500);
        lander.Altitude = _fieldElevationFt + 60;
        Assert.DoesNotContain(_runways, runway => AsdexRunwayUsers.IsOverFootprint(lander, runway));
        return (taxiway, lander);
    }

    private static AircraftState WithPhase(AircraftState aircraft, Phase phase)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        return aircraft;
    }
}
