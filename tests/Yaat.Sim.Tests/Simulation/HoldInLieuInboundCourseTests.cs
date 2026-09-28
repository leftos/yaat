using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The hold-in-lieu of procedure turn is flown on the hold leg's own published inbound course. KACV RNAV (GPS)
/// RWY 1 has a HILPT at the SEGVE IAF: HF SEGVE 012.7° magnetic (the course field of an HA/HF/HM leg is the
/// inbound holding course), with no recommended navaid, so it converts with the airport's magnetic variation of
/// record (KACV E017): 029.7° true.
/// </summary>
public class HoldInLieuInboundCourseTests(ITestOutputHelper output)
{
    [Fact]
    public void Capp_KacvR01_DctSegve_HoldInLieuInboundIsPublishedHoldCourseConvertedWithAirportVariation()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("KACV", "R01"));

        CifpLeg holdLeg = Assert.IsType<CifpLeg>(procedure.HoldInLieuLeg);
        Assert.Equal("SEGVE", holdLeg.FixIdentifier);
        Assert.True(string.IsNullOrWhiteSpace(holdLeg.RecommendedNavaidId), $"HF SEGVE unexpectedly names navaid {holdLeg.RecommendedNavaidId}");
        Assert.Equal(12.7, holdLeg.OutboundCourse);
        Assert.Equal(17.0, navDb.GetAirportMagneticVariation("KACV"));

        (double Lat, double Lon)? segve = navDb.GetFixPosition("SEGVE");
        Assert.NotNull(segve);

        // South-west of SEGVE, heading for it: cleared direct SEGVE for the approach, entering the HILPT there.
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            TrueHeading = new TrueHeading(30.0),
            Position = new LatLon(segve.Value.Lat - 0.15, segve.Value.Lon - 0.1),
            Altitude = 4000,
            IndicatedAirspeed = 110,
            Declination = 15.0,
            FlightPlan = new AircraftFlightPlan { Destination = "KACV", Route = "" },
            Procedure = new AircraftProcedure { DestinationRunway = null },
        };
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "SEGVE", Position = new LatLon(segve.Value.Lat, segve.Value.Lon) });

        var cmd = new ClearedApproachCommand(
            "R01",
            "KACV",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: "SEGVE",
            DctFixLat: segve.Value.Lat,
            DctFixLon: segve.Value.Lon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        HoldingPatternPhase hold = Assert.Single(aircraft.Phases.Phases.OfType<HoldingPatternPhase>());
        output.WriteLine($"HILPT at {hold.FixName}: inbound {hold.InboundCourse}T, {hold.Direction} turns");
        Assert.Equal("SEGVE", hold.FixName);
        Assert.Equal(30, hold.InboundCourse);
    }
}
