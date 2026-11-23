using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's flight plan block: the Has flight plan checkbox makes an IFR plan with the aircraft's type or
/// removes the plan, one undo step each; a cruise number that is not a whole number of 0 or more writes nothing; the filed
/// type is its own field.
/// </summary>
public sealed class ScenarioAircraftFlightPlanTests : IDisposable
{
    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftFlightPlanTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void FlightPlan_TurnOn_CreatesIfrPlanWithTheAircraftsType()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(
            _fx,
            a =>
            {
                a["aircraftType"] = "C172";
                a.Remove("flightplan");
            }
        );
        Assert.False(vm.HasFlightPlan);

        vm.HasFlightPlan = true;

        ScenarioFlightPlan plan = Assert.IsType<ScenarioFlightPlan>(ScenarioAircraftPanel.First(session).FlightPlan);
        Assert.Equal("IFR", plan.Rules);
        Assert.Equal("C172", plan.AircraftType);
        Assert.Equal(("", "", 0, 0, "", ""), (plan.Departure, plan.Destination, plan.CruiseAltitude, plan.CruiseSpeed, plan.Route, plan.Remarks));
        Assert.Null(plan.ExtensionData);
        Assert.Equal("Add flight plan", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));
        Assert.Equal("IFR", vm.FlightRulesPick);
        Assert.Equal("C172", vm.FiledType);
    }

    [Fact]
    public void FlightPlan_TurnOff_SetsNull_UndoRestoresIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(
            _fx,
            a => a["flightplan"]!["equipmentSuffix"] = JsonNode.Parse("""{"code": "L"}""")
        );
        string original = session.Document.Write();
        Assert.True(vm.HasFlightPlan);

        vm.HasFlightPlan = false;

        Assert.Null(ScenarioAircraftPanel.First(session).FlightPlan);
        Assert.Equal("Remove flight plan", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));

        Assert.True(session.Undo());

        Assert.Equal(original, session.Document.Write());
        ScenarioFlightPlan plan = ScenarioAircraftPanel.First(session).FlightPlan!;
        Assert.True(plan.ExtensionData!.ContainsKey("equipmentSuffix"));
        Assert.True(vm.HasFlightPlan);
        Assert.Equal("TRUKN2 ORRCA Q120 GALLI BQU TWF PRESN", vm.FlightPlanRoute);
        Assert.Equal("41000", vm.CruiseAltitude);
    }

    [Fact]
    public void FlightPlan_CruiseAltitudeBadText_WritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        string before = session.Document.Write();

        ScenarioAircraftPanel.Commit(() => vm.CruiseAltitude = "abc", vm.CommitCruiseAltitudeCommand);

        Assert.Equal("Enter a whole number, 0 or more.", vm.CruiseAltitudeError);
        Assert.Equal("abc", vm.CruiseAltitude);
        Assert.Equal(before, session.Document.Write());
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void FlightPlan_UnknownRulesInFile_AddedToPicker()
    {
        (_, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, a => a["flightplan"]!["rules"] = "SVFR");

        Assert.Contains("SVFR", vm.FlightRulesOptions);
        Assert.Equal("SVFR", vm.FlightRulesPick);
    }

    [Fact]
    public void FlightPlan_BlankCruise_WritesZero()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        ScenarioAircraftPanel.Commit(() => vm.CruiseAltitude = "  ", vm.CommitCruiseAltitudeCommand);

        Assert.Equal(0, ScenarioAircraftPanel.First(session).FlightPlan!.CruiseAltitude);
        Assert.Null(vm.CruiseAltitudeError);
        Assert.Equal("0", vm.CruiseAltitude);
        Assert.Equal("Edit cruise altitude", session.UndoDescription);
    }

    [Fact]
    public void FlightPlan_NoOpDepartureCommit_KeepsCruiseAltitudeError()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        ScenarioAircraftPanel.Commit(() => vm.CruiseAltitude = "abc", vm.CommitCruiseAltitudeCommand);
        Assert.NotNull(vm.CruiseAltitudeError);

        ScenarioAircraftPanel.Commit(() => { }, vm.CommitDepartureCommand);

        Assert.NotNull(vm.CruiseAltitudeError);
        Assert.Equal("abc", vm.CruiseAltitude);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void FlightPlan_FiledTypeDoesNotFollowTypeEdit()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        ScenarioAircraftPanel.Commit(() => vm.AircraftType = "B738", vm.CommitAircraftTypeCommand);

        Assert.Equal("B738", ScenarioAircraftPanel.First(session).AircraftType);
        Assert.Equal("E55P/L", ScenarioAircraftPanel.First(session).FlightPlan!.AircraftType);
        Assert.Equal("E55P/L", vm.FiledType);
    }
}
