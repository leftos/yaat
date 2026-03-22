using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Tests for inferring VFR/IFR flight rules from scenario flight plan data.
/// Aircraft with no flight plan or no cruise altitude should default to VFR.
/// Only aircraft with a positive cruise altitude (and no explicit rules) should be IFR.
/// </summary>
public class FlightRulesInferenceTests
{
    [Fact]
    public void NullFlightPlan_InfersVfr()
    {
        var result = ScenarioLoader.InferFlightRules(null);
        Assert.Equal("VFR", result);
    }

    [Fact]
    public void FlightPlan_NoRules_NoAltitude_InfersVfr()
    {
        var fp = new ScenarioFlightPlan { CruiseAltitude = 0 };
        var result = ScenarioLoader.InferFlightRules(fp);
        Assert.Equal("VFR", result);
    }

    [Fact]
    public void FlightPlan_NoRules_WithAltitude_InfersIfr()
    {
        var fp = new ScenarioFlightPlan { CruiseAltitude = 12000 };
        var result = ScenarioLoader.InferFlightRules(fp);
        Assert.Equal("IFR", result);
    }

    [Fact]
    public void FlightPlan_ExplicitIfr_ReturnsIfr()
    {
        var fp = new ScenarioFlightPlan { Rules = "IFR", CruiseAltitude = 0 };
        var result = ScenarioLoader.InferFlightRules(fp);
        Assert.Equal("IFR", result);
    }

    [Fact]
    public void FlightPlan_ExplicitVfr_ReturnsVfr()
    {
        var fp = new ScenarioFlightPlan { Rules = "VFR", CruiseAltitude = 0 };
        var result = ScenarioLoader.InferFlightRules(fp);
        Assert.Equal("VFR", result);
    }

    [Fact]
    public void FlightPlan_ExplicitVfr_WithAltitude_ReturnsVfr()
    {
        var fp = new ScenarioFlightPlan { Rules = "VFR", CruiseAltitude = 3000 };
        var result = ScenarioLoader.InferFlightRules(fp);
        Assert.Equal("VFR", result);
    }
}
