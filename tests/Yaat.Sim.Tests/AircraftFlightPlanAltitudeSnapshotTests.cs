using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The filed <see cref="PlannedAltitude"/> round-trips through the flattened snapshot DTO fields
/// (AltitudeCruiseFeet / AltitudeBlockFloorFeet / AltitudeIsVfr / AltitudeIsVfrOnTop / AltitudeIsAbove),
/// so block / VFR-on-top / above notations survive replay reconstruction.
/// </summary>
public class AircraftFlightPlanAltitudeSnapshotTests
{
    public static TheoryData<PlannedAltitude> Cases =>
        [
            PlannedAltitude.None,
            PlannedAltitude.Ifr(24000),
            PlannedAltitude.Block(20000, 25000),
            PlannedAltitude.Vfr(6500),
            PlannedAltitude.Vfr(null),
            PlannedAltitude.Otp(12000),
            PlannedAltitude.Otp(null),
            PlannedAltitude.UntilFix(17000, "SJC", 11000),
            PlannedAltitude.UntilFix(17000, "3730N/12200W", 11000),
        ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixQualifiedAltitude_AndItsPassageFlags_SurviveSnapshotRoundTrip(bool passed)
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = PlannedAltitude.UntilFix(17000, "SJC", 11000),
            AltitudeFixApproached = true,
            AltitudeFixPassed = passed,
        };

        var restored = AircraftFlightPlan.FromSnapshot(fp.ToSnapshot());

        Assert.Equal(PlannedAltitude.UntilFix(17000, "SJC", 11000), restored.Altitude);
        Assert.True(restored.AltitudeFixApproached);
        Assert.Equal(passed, restored.AltitudeFixPassed);
    }

    /// <summary>A flight plan as a V30 snapshot wrote it: one Remarks string, no fix, Mach or classified-speed fields.</summary>
    private const string V30FlightPlanJson = """
        {
          "HasFlightPlan": true,
          "AircraftType": "B738/L",
          "Departure": "KOAK",
          "Destination": "KLAX",
          "Route": "OAK5 SJC",
          "Remarks": "/t/ CHARTS",
          "RevisionNumber": 2,
          "EquipmentSuffix": "L",
          "IcaoEquipmentCodes": "",
          "FlightRules": "IFR",
          "AltitudeCruiseFeet": 24000,
          "AltitudeBlockFloorFeet": null,
          "AltitudeIsVfr": false,
          "AltitudeIsVfrOnTop": false,
          "AltitudeIsAbove": false,
          "CruiseSpeed": 450
        }
        """;

    [Fact]
    public void AV30Snapshot_RestoresAPlainAltitude_Unlatched_WithItsRemarksAsTheInterfacilityPart()
    {
        var restored = AircraftFlightPlan.FromSnapshot(JsonSerializer.Deserialize<AircraftFlightPlanDto>(V30FlightPlanJson)!);

        Assert.Equal(PlannedAltitude.Ifr(24000), restored.Altitude);
        Assert.False(restored.AltitudeFixApproached);
        Assert.False(restored.AltitudeFixPassed);
        Assert.Equal("/t/ CHARTS", restored.InterfacilityRemarks);
        Assert.Equal("", restored.IntrafacilityRemarks);
        Assert.Equal(450, restored.CruiseSpeed);
        Assert.Null(restored.CruiseMach);
        Assert.False(restored.IsSpeedClassified);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Altitude_SurvivesSnapshotRoundTrip(PlannedAltitude altitude)
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = altitude,
        };
        var restored = AircraftFlightPlan.FromSnapshot(fp.ToSnapshot());
        Assert.Equal(altitude, restored.Altitude);
    }
}
