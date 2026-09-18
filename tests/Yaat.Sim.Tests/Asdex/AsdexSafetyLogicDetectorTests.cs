using Xunit;
using Yaat.Sim;
using Yaat.Sim.Asdex;

namespace Yaat.Sim.Tests.Asdex;

/// <summary>
/// ASDE-X Safety Logic detector — conditions modelled from the CRC ASDE-X manual: closed
/// runway, occupied runway, taxi-onto-active-runway, and taxiway landing. Runway "28R" is a
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
            FieldElevationFt
        );

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
        Assert.Equal(["AAL1"], alert.Callsigns);
        Assert.Equal(["28R", "AAL1", "CLOSED RWY"], alert.MessageLines);
        Assert.True(alert.PlayAuralAlert);
    }

    [Fact]
    public void ClosedRunway_LoneCrosser_DoesNotAlert()
    {
        // A perpendicular taxiing aircraft is crossing, not landing/departing.
        AircraftState crosser = Aircraft("AAL1", 37.0005, -122.0050, 10, onGround: true, FieldElevationFt, speedKts: 8);
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [crosser], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void OpenRunway_SingleUser_DoesNotAlert()
    {
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [OnRunwayAligned("AAL1")], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void OccupiedRunway_ArrivalOverLinedUpDeparture_Alerts()
    {
        AircraftState arrival = OnRunwayAligned("AAL1", speed: 130, onGround: false); // airborne low over the runway
        AircraftState linedUp = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0); // holding, aligned

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [arrival, linedUp], FieldElevationFt);

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "UAL2"], alert.Callsigns); // ordinal-sorted, stable id
        Assert.Equal("OCCUPIED RWY", alert.MessageLines[2]);
    }

    [Fact]
    public void TaxiOntoActiveRunway_CrosserWithActiveDeparture_Alerts()
    {
        AircraftState departure = OnRunwayAligned("AAL1", speed: 90); // rolling, aligned 280
        AircraftState crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12); // crossing N

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, crosser], FieldElevationFt);

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.TaxiOntoActiveRunway, alert.Kind);
        Assert.Equal("RWY INCURSION", alert.MessageLines[2]);
        Assert.Contains("GND3", alert.Callsigns);
        Assert.Contains("AAL1", alert.Callsigns);
    }

    [Fact]
    public void TaxiOntoActiveRunway_StationaryLinedUpDeparture_StillArms()
    {
        // Regression for the LUAW-incursion case: the only "user" is a departure holding in
        // position at GS 0 (aligned). A taxiing crosser must still trigger the alert.
        AircraftState linedUp = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: true, FieldElevationFt, speedKts: 0);
        AircraftState crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [linedUp, crosser], FieldElevationFt);

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.TaxiOntoActiveRunway, alert.Kind);
        Assert.Contains("AAL1", alert.Callsigns);
        Assert.Contains("GND3", alert.Callsigns);
    }

    [Fact]
    public void Alignment_UsesTrueHeadingViaMagneticVariation()
    {
        // Runway 28R at a +15 deg-east-variation airport: true runway heading is 295. An aircraft
        // tracking 328 true is 33 deg off the true heading (within the 35 deg budget) but 48 deg
        // off the raw magnetic 280 (outside it) — so it only counts as aligned in the true frame.
        var runway = new AsdexRunwaySurface("28R", RunwayArea, IsClosed: true, MagneticVariationDeg: 15, ElevationFt: FieldElevationFt);
        AircraftState lander = Aircraft("AAL1", 37.0005, -122.0050, 328, onGround: true, FieldElevationFt, speedKts: 20);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([runway], [], [lander], FieldElevationFt);

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }

    [Fact]
    public void BackTaxi_OppositeHeading_IsARunwayUser_NotACrosser()
    {
        // An aircraft back-taxiing on 28R (heading 100, the reciprocal) is using the runway, not
        // crossing it (P/CG BACK-TAXI). With a rolling departure it is an occupied-runway conflict,
        // never a "RWY INCURSION" taxi-onto alert.
        AircraftState departure = OnRunwayAligned("AAL1", speed: 90);
        AircraftState backTaxi = Aircraft("N123", 37.0005, -122.0040, 100, onGround: true, FieldElevationFt, speedKts: 15);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, backTaxi], FieldElevationFt);

        AsdexSafetyAlert alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
    }

    [Fact]
    public void ClosedRunway_LoneBackTaxi_Alerts()
    {
        AircraftState backTaxi = Aircraft("N123", 37.0005, -122.0050, 100, onGround: true, FieldElevationFt, speedKts: 15);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [backTaxi], FieldElevationFt);

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(alerts).Kind);
    }

    [Fact]
    public void ArrivalAgl_MeasuredFromTheRunwayElevation_NotTheField()
    {
        // A runway 500 ft above the airport reference point (a long sloping field): an arrival 250 ft
        // above the pavement is a low arrival even though it is 737 ft above the field elevation.
        var highRunway = new AsdexRunwaySurface("28R", RunwayArea, IsClosed: true, MagneticVariationDeg: 0, ElevationFt: FieldElevationFt + 500);
        AircraftState arrival = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: false, FieldElevationFt + 750, speedKts: 130);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([highRunway], [], [arrival], FieldElevationFt);

        Assert.Equal(AsdexAlertKind.ClosedRunway, Assert.Single(alerts).Kind);
    }

    [Fact]
    public void TaxiwayLanding_LowArrivalOverTaxiway_Alerts()
    {
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0020, -122.0100), new LatLon(37.0020, -122.0000));
        AircraftState lander = Aircraft("AAL1", 37.0020, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [taxiway], [lander], FieldElevationFt);

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

        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway()], [taxiway], [lander], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void InhibitedAircraft_ExcludedFromAlerts()
    {
        AircraftState inhibited = OnRunwayAligned("AAL1");
        inhibited.Stars.AsdexAlertsInhibited = true;
        IReadOnlyList<AsdexSafetyAlert> alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [inhibited], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void StableId_SamePairAcrossTicks_IsIdentical()
    {
        AircraftState a = OnRunwayAligned("AAL1", speed: 130, onGround: false);
        AircraftState b = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0);

        IReadOnlyList<AsdexSafetyAlert> first = AsdexSafetyLogicDetector.Detect([Runway()], [], [a, b], FieldElevationFt);
        IReadOnlyList<AsdexSafetyAlert> second = AsdexSafetyLogicDetector.Detect([Runway()], [], [b, a], FieldElevationFt); // order swapped

        Assert.Equal(first[0].Id, second[0].Id);
    }
}
