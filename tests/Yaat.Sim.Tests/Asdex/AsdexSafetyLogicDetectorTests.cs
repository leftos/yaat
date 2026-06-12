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
    private static AsdexRunwaySurface Runway(bool closed = false) => new("28R", RunwayArea, closed, 0);

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
        var alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [OnRunwayAligned("AAL1")], FieldElevationFt);

        var alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
        Assert.Equal(["AAL1"], alert.Callsigns);
        Assert.Equal(["28R", "AAL1", "CLOSED RWY"], alert.MessageLines);
        Assert.True(alert.PlayAuralAlert);
    }

    [Fact]
    public void ClosedRunway_LoneCrosser_DoesNotAlert()
    {
        // A perpendicular taxiing aircraft is crossing, not landing/departing.
        var crosser = Aircraft("AAL1", 37.0005, -122.0050, 10, onGround: true, FieldElevationFt, speedKts: 8);
        var alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [crosser], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void OpenRunway_SingleUser_DoesNotAlert()
    {
        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [OnRunwayAligned("AAL1")], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void OccupiedRunway_ArrivalOverLinedUpDeparture_Alerts()
    {
        var arrival = OnRunwayAligned("AAL1", speed: 130, onGround: false); // airborne low over the runway
        var linedUp = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0); // holding, aligned

        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [arrival, linedUp], FieldElevationFt);

        var alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["AAL1", "UAL2"], alert.Callsigns); // ordinal-sorted, stable id
        Assert.Equal("OCCUPIED RWY", alert.MessageLines[2]);
    }

    [Fact]
    public void TaxiOntoActiveRunway_CrosserWithActiveDeparture_Alerts()
    {
        var departure = OnRunwayAligned("AAL1", speed: 90); // rolling, aligned 280
        var crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12); // crossing N

        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [departure, crosser], FieldElevationFt);

        var alert = Assert.Single(alerts);
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
        var linedUp = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: true, FieldElevationFt, speedKts: 0);
        var crosser = Aircraft("GND3", 37.0005, -122.0040, 10, onGround: true, FieldElevationFt, speedKts: 12);

        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [], [linedUp, crosser], FieldElevationFt);

        var alert = Assert.Single(alerts);
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
        var runway = new AsdexRunwaySurface("28R", RunwayArea, IsClosed: true, MagneticVariationDeg: 15);
        var lander = Aircraft("AAL1", 37.0005, -122.0050, 328, onGround: true, FieldElevationFt, speedKts: 20);

        var alerts = AsdexSafetyLogicDetector.Detect([runway], [], [lander], FieldElevationFt);

        var alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }

    [Fact]
    public void TaxiwayLanding_LowArrivalOverTaxiway_Alerts()
    {
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0020, -122.0100), new LatLon(37.0020, -122.0000));
        var lander = Aircraft("AAL1", 37.0020, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [taxiway], [lander], FieldElevationFt);

        var alert = Assert.Single(alerts);
        Assert.Equal(AsdexAlertKind.TaxiwayLanding, alert.Kind);
        Assert.Equal(["A", "AAL1", "TAXIWAY LANDING"], alert.MessageLines);
    }

    [Fact]
    public void TaxiwayLanding_ArrivalOverRunway_DoesNotAlert()
    {
        // Over the runway footprint (a normal approach), not a taxiway, even if a taxiway is near.
        var taxiway = new AsdexTaxiwaySegment("A", new LatLon(37.0005, -122.0100), new LatLon(37.0005, -122.0000));
        var lander = Aircraft("AAL1", 37.0005, -122.0050, 280, onGround: false, FieldElevationFt + 60, speedKts: 120);

        var alerts = AsdexSafetyLogicDetector.Detect([Runway()], [taxiway], [lander], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void InhibitedAircraft_ExcludedFromAlerts()
    {
        var inhibited = OnRunwayAligned("AAL1");
        inhibited.Stars.AsdexAlertsInhibited = true;
        var alerts = AsdexSafetyLogicDetector.Detect([Runway(closed: true)], [], [inhibited], FieldElevationFt);
        Assert.Empty(alerts);
    }

    [Fact]
    public void StableId_SamePairAcrossTicks_IsIdentical()
    {
        var a = OnRunwayAligned("AAL1", speed: 130, onGround: false);
        var b = Aircraft("UAL2", 37.0005, -122.0040, 280, onGround: true, FieldElevationFt, speedKts: 0);

        var first = AsdexSafetyLogicDetector.Detect([Runway()], [], [a, b], FieldElevationFt);
        var second = AsdexSafetyLogicDetector.Detect([Runway()], [], [b, a], FieldElevationFt); // order swapped

        Assert.Equal(first[0].Id, second[0].Id);
    }
}
