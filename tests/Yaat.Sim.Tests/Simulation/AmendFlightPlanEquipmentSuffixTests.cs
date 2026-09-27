using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The equipment suffix is a separate flight-plan field (issue #463): amending the type never changes it, and
/// <see cref="SimulationEngine.AmendFlightPlan"/> defaults it to /A only when the amendment files a new plan with no suffix.
/// </summary>
public class AmendFlightPlanEquipmentSuffixTests(ITestOutputHelper output)
{
    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static AircraftState AddAircraft(SimulationEngine engine, string callsign, bool filed, string equipmentSuffix)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B763",
            FlightPlan = new AircraftFlightPlan
            {
                AircraftType = filed ? "H/B763/L" : "",
                EquipmentSuffix = equipmentSuffix,
                FlightRules = "IFR",
                HasFlightPlan = filed,
            },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void FiledPlan_TypeAmendedWithoutSuffix_KeepsSuffix()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "L");

        engine.AmendFlightPlan("UPS2941", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "B763", EquipmentSuffix: null));

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
    }

    [Fact]
    public void NewPlan_FiledWithoutSuffix_DefaultsSuffixToA()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "N513SJ", filed: false, equipmentSuffix: "");

        engine.AmendFlightPlan("N513SJ", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "C172", EquipmentSuffix: null));

        Assert.True(ac.FlightPlan.HasFlightPlan);
        Assert.Equal("C172", ac.FlightPlan.AircraftType);
        Assert.Equal("A", ac.FlightPlan.EquipmentSuffix);
    }

    [Fact]
    public void FiledPlan_TypedFpWithBareType_KeepsSuffix()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "L");

        FlightPlanAmendment amendment = FlightPlanNormalization.FromCreateCommand(new CreateFlightPlanCommand("IFR", "B763", 35000, "KOAK KSFO"));
        engine.AmendFlightPlan("UPS2941", amendment);

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
    }
}
