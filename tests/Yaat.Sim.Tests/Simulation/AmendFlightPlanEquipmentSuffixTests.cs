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
    public void FiledPlan_TypeAmendedWithElementAAndSuffix_StoresBareTypeAndParts()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "G");

        engine.AmendFlightPlan("UPS2941", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "H/B763/L", EquipmentSuffix: null));

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
        Assert.True(ac.FlightPlan.HasSpecialAircraftIndicator);
    }

    [Fact]
    public void FiledPlan_TypeWithSuffixAndExplicitSuffix_ExplicitSuffixWins()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "A");

        engine.AmendFlightPlan("UPS2941", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "H/B763/L", EquipmentSuffix: "G"));

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("G", ac.FlightPlan.EquipmentSuffix);
    }

    [Fact]
    public void FiledPlan_TypeWithIndicatorAndExplicitIndicatorFalse_StaysFalse()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "L");

        engine.AmendFlightPlan("UPS2941", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "H/B763", SpecialAircraftIndicator: false));

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.False(ac.FlightPlan.HasSpecialAircraftIndicator);
    }

    [Fact]
    public void FiledPlan_TypeWithCountAndExplicitCount_ExplicitCountWins()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "RCH01", filed: true, equipmentSuffix: "G");

        engine.AmendFlightPlan("RCH01", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "2/C130/G", NumberOfAircraft: 3));

        Assert.Equal("C130", ac.FlightPlan.AircraftType);
        Assert.Equal(3, ac.FlightPlan.NumberOfAircraft);
    }

    [Fact]
    public void FiledPlan_BareTypeAmend_KeepsIndicatorAndCount()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS2941", filed: true, equipmentSuffix: "L");
        ac.FlightPlan.HasSpecialAircraftIndicator = true;
        ac.FlightPlan.NumberOfAircraft = 2;

        engine.AmendFlightPlan("UPS2941", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "B738"));

        Assert.Equal("B738", ac.FlightPlan.AircraftType);
        Assert.True(ac.FlightPlan.HasSpecialAircraftIndicator);
        Assert.Equal(2, ac.FlightPlan.NumberOfAircraft);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
    }

    [Fact]
    public void FiledPlan_TypeWithCount_SetsCountAndSuffix()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "RCH01", filed: true, equipmentSuffix: "A");

        engine.AmendFlightPlan("RCH01", new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "2/C130/G"));

        Assert.Equal("C130", ac.FlightPlan.AircraftType);
        Assert.Equal(2, ac.FlightPlan.NumberOfAircraft);
        Assert.Equal("G", ac.FlightPlan.EquipmentSuffix);
        Assert.False(ac.FlightPlan.HasSpecialAircraftIndicator);
    }

    [Fact]
    public void TypedFp_TypeWithIndicator_SetsSpecialAircraftIndicator()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddAircraft(engine, "UPS1", filed: false, equipmentSuffix: "");

        FlightPlanAmendment amendment = FlightPlanNormalization.FromCreateCommand(
            new CreateFlightPlanCommand("IFR", "H/B763/L", PlannedAltitude.Ifr(35000), "KOAK KSFO")
        );
        engine.AmendFlightPlan("UPS1", amendment);

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
        Assert.True(ac.FlightPlan.HasSpecialAircraftIndicator);
    }

    [Fact]
    public void SyntheticSpawn_FiledTypeWithElementAAndSuffix_StoresBareTypeAndParts()
    {
        var plan = new AircraftFlightPlan { AircraftType = "H/A306/L", EquipmentSuffix = "A" };

        SimulationEngine.NormalizeSyntheticFiledType(plan, baseType: "A30B", sibling: "A306");

        Assert.Equal("A306", plan.AircraftType);
        Assert.Equal("L", plan.EquipmentSuffix);
        Assert.True(plan.HasSpecialAircraftIndicator);
        Assert.Null(plan.NumberOfAircraft);
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

        FlightPlanAmendment amendment = FlightPlanNormalization.FromCreateCommand(
            new CreateFlightPlanCommand("IFR", "B763", PlannedAltitude.Ifr(35000), "KOAK KSFO")
        );
        engine.AmendFlightPlan("UPS2941", amendment);

        Assert.Equal("B763", ac.FlightPlan.AircraftType);
        Assert.Equal("L", ac.FlightPlan.EquipmentSuffix);
    }
}
