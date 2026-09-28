using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// ERAM field 05 (SPD) as a true airspeed, a Mach number or a classified speed, each clearing the others, with CRC's
/// flight-plan editor sending 0 for a Mach or classified plan; and ERAM field 11 (RMK) kept as intrafacility and
/// interfacility parts, composed intra-then-inter into the one remarks string CRC and the voice-type parse read.
/// </summary>
public class AmendFlightPlanSpeedAndRemarksTests(ITestOutputHelper output)
{
    private const string Callsign = "AAL123";

    private SimulationEngine BuildEngine(AircraftState ac)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.World.AddAircraft(ac);
        return engine;
    }

    private static AircraftState Aircraft() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Transponder = new AircraftTransponder
            {
                Code = 4571,
                AssignedCode = 4571,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                AircraftType = "B738",
                EquipmentSuffix = "L",
                CruiseSpeed = 450,
            },
        };

    private static FlightPlanAmendment NoEdit => new(ClearBeaconCode: false);

    [Fact]
    public void Mach_ReplacesTheTrueAirspeed()
    {
        AircraftState ac = Aircraft();
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, NoEdit with { CruiseMach = 78 });

        Assert.Equal(78, ac.FlightPlan.CruiseMach);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
        Assert.False(ac.FlightPlan.IsSpeedClassified);
    }

    [Fact]
    public void ClassifiedSpeed_ReplacesTheMach()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.SetMach(78);
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, NoEdit with { ClassifiedSpeed = true });

        Assert.True(ac.FlightPlan.IsSpeedClassified);
        Assert.Null(ac.FlightPlan.CruiseMach);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrueAirspeedZero_AsCrcsEditorSendsIt_KeepsAMachOrClassifiedSpeed(bool classified)
    {
        AircraftState ac = Aircraft();
        SetMachOrClassified(ac.FlightPlan, classified);
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, CruiseSpeed: 0));

        Assert.Equal(classified ? null : 78, ac.FlightPlan.CruiseMach);
        Assert.Equal(classified, ac.FlightPlan.IsSpeedClassified);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANonZeroTrueAirspeed_ReplacesAMachOrClassifiedSpeed(bool classified)
    {
        AircraftState ac = Aircraft();
        SetMachOrClassified(ac.FlightPlan, classified);
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, CruiseSpeed: 460));

        Assert.Equal(460, ac.FlightPlan.CruiseSpeed);
        Assert.Null(ac.FlightPlan.CruiseMach);
        Assert.False(ac.FlightPlan.IsSpeedClassified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MachAndClassifiedSpeed_SurviveASnapshotRoundTrip(bool classified)
    {
        AircraftState ac = Aircraft();
        SetMachOrClassified(ac.FlightPlan, classified);

        AircraftFlightPlan restored = RoundTrip(ac).FlightPlan;

        Assert.Equal(ac.FlightPlan.CruiseMach, restored.CruiseMach);
        Assert.Equal(classified, restored.IsSpeedClassified);
        Assert.Equal(0, restored.CruiseSpeed);
    }

    [Fact]
    public void RecordedAmendment_ReplayedFromJson_AppliesTheMachAndBothRemarksParts()
    {
        AircraftState ac = Aircraft();
        SimulationEngine engine = BuildEngine(ac);
        FlightPlanAmendment amendment = NoEdit with { CruiseMach = 82, InterfacilityRemarks = "CHARTS", IntrafacilityRemarks = "LOCAL" };
        var recorded = new RecordedAmendFlightPlan(12.0, Callsign, amendment, null);

        string json = JsonSerializer.Serialize<RecordedAction>(recorded, RecordingJsonOptions.Default);
        RecordedAction? read = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);
        Assert.NotNull(read);
        CommandResult result = engine.Actions.ApplyRecorded(read);

        Assert.True(result.Success, result.Message);
        Assert.Equal(82, ac.FlightPlan.CruiseMach);
        Assert.Equal("LOCAL CHARTS", ac.FlightPlan.Remarks);
    }

    [Theory]
    [InlineData("LOCAL", "CHARTS", "LOCAL CHARTS")]
    [InlineData("LOCAL", "", "LOCAL")]
    [InlineData("", "CHARTS", "CHARTS")]
    [InlineData("", "", "")]
    public void Remarks_ComposeIntrafacilityThenInterfacility(string intra, string inter, string composed)
    {
        var plan = new AircraftFlightPlan { IntrafacilityRemarks = intra, InterfacilityRemarks = inter };

        Assert.Equal(composed, plan.Remarks);
    }

    [Fact]
    public void EachRemarksPart_AmendsOnlyItself_AndTheVoiceTypeReadsTheInterfacilityRemarks()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "OLD";
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, NoEdit with { InterfacilityRemarks = "/t/ NEW" });

        Assert.Equal("LOCAL", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("LOCAL /t/ NEW", ac.FlightPlan.Remarks);
        Assert.Equal(FlightPlanVoice.TextOnly, ac.Voice.Type);

        engine.AmendFlightPlan(Callsign, NoEdit with { IntrafacilityRemarks = "" });

        Assert.Equal("/t/ NEW", ac.FlightPlan.Remarks);
    }

    [Fact]
    public void AVoiceMarkerInTheIntrafacilityPart_DoesNotBlockAVoiceChange()
    {
        AircraftState ac = Aircraft();
        SimulationEngine engine = BuildEngine(ac);
        engine.AmendFlightPlan(Callsign, NoEdit with { IntrafacilityRemarks = "/t/" });
        Assert.Equal(FlightPlanVoice.Full, ac.Voice.Type);

        engine.AmendFlightPlan(Callsign, NoEdit with { InterfacilityRemarks = FlightPlanVoice.ApplyVoiceMarker("", FlightPlanVoice.ReceiveOnly) });
        Assert.Equal(FlightPlanVoice.ReceiveOnly, ac.Voice.Type);

        engine.AmendFlightPlan(Callsign, NoEdit with { InterfacilityRemarks = FlightPlanVoice.ApplyVoiceMarker("/r/", FlightPlanVoice.Full) });
        Assert.Equal(FlightPlanVoice.Full, ac.Voice.Type);
    }

    [Fact]
    public void TheComposedRemarks_SentBackUnchangedButForSpaces_KeepBothParts()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Remarks: " LOCAL CHARTS "));

        Assert.Equal("LOCAL", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("CHARTS", ac.FlightPlan.InterfacilityRemarks);
    }

    [Fact]
    public void TheComposedRemarks_SentBackUnchanged_KeepBothParts()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Remarks: "LOCAL CHARTS"));

        Assert.Equal("LOCAL", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("CHARTS", ac.FlightPlan.InterfacilityRemarks);
    }

    [Fact]
    public void ChangedComposedRemarks_ReplaceTheInterfacilityPart_AndClearTheIntrafacilityPart()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";
        SimulationEngine engine = BuildEngine(ac);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Remarks: "LOCAL CHARTS /r/"));

        Assert.Equal("", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("LOCAL CHARTS /r/", ac.FlightPlan.InterfacilityRemarks);
        Assert.Equal(FlightPlanVoice.ReceiveOnly, ac.Voice.Type);
    }

    [Fact]
    public void BothRemarksParts_SurviveASnapshotRoundTrip()
    {
        AircraftState ac = Aircraft();
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";

        AircraftFlightPlan restored = RoundTrip(ac).FlightPlan;

        Assert.Equal("LOCAL", restored.IntrafacilityRemarks);
        Assert.Equal("CHARTS", restored.InterfacilityRemarks);
    }

    [Fact]
    public void ASnapshotFromBeforeTheSplit_LoadsItsRemarksAsInterfacility_WithNoMachOrClassifiedSpeed()
    {
        // A snapshot written before the split and the speed fields: one "Remarks" key and no intra, Mach or SC keys.
        AircraftState ac = Aircraft();
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";
        JsonObject snapshot = JsonSerializer.SerializeToNode(ac.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject flightPlan = snapshot[nameof(AircraftSnapshotDto.FlightPlan)]!.AsObject();
        Assert.Equal("CHARTS", flightPlan["Remarks"]!.GetValue<string>());
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.IntrafacilityRemarks)));
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.CruiseMach)));
        Assert.True(flightPlan.Remove(nameof(AircraftFlightPlanDto.IsSpeedClassified)));

        AircraftSnapshotDto? dto = snapshot.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        AircraftFlightPlan restored = AircraftState.FromSnapshot(dto, null).FlightPlan;

        Assert.Equal("CHARTS", restored.InterfacilityRemarks);
        Assert.Equal("", restored.IntrafacilityRemarks);
        Assert.Null(restored.CruiseMach);
        Assert.False(restored.IsSpeedClassified);
        Assert.Equal(450, restored.CruiseSpeed);
    }

    private static void SetMachOrClassified(AircraftFlightPlan plan, bool classified)
    {
        if (classified)
        {
            plan.SetClassifiedSpeed();
        }
        else
        {
            plan.SetMach(78);
        }
    }

    private static AircraftState RoundTrip(AircraftState ac)
    {
        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto? dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default);
        Assert.NotNull(dto);
        return AircraftState.FromSnapshot(dto, null);
    }
}
