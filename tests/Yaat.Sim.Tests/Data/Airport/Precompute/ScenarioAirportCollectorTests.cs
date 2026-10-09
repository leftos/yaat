using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="ScenarioAirportCollector"/> over the real ATCTrainer scenarios in <c>docs/atctrainer-scenario-examples/</c>:
/// a scenario uses its primary airport and every aircraft's airport, folded to the FAA id, never a flight plan's
/// departure or destination; and <see cref="PrecomputeAirportList"/> writes and reads the committed list.
/// </summary>
public class ScenarioAirportCollectorTests
{
    /// <summary>Primary OAK; aircraft at nine airports, OAK among them, and 27 aircraft with no airport.</summary>
    [Fact]
    public void AirportsOf_PrimaryAndAircraftAirports_NullAircraftAirportSkipped()
    {
        Scenario scenario = Example("01J02M96SPYP4JV55R5RMVCQBS");
        Assert.Equal("OAK", scenario.PrimaryAirportId);
        Assert.Contains(scenario.Aircraft, a => a.AirportId is null);

        Assert.Equal(["C83", "HWD", "LVK", "MCE", "MOD", "OAK", "SCK", "SMF", "TCY"], ScenarioAirportCollector.AirportsOf([scenario]));
    }

    /// <summary>Primary OAK, aircraft at both <c>KOAK</c> and <c>OAK</c>: one airport, in the FAA form.</summary>
    [Fact]
    public void AirportsOf_IcaoAndFaaIds_FoldToOneFaaId()
    {
        Scenario scenario = Example("01HHWYG5C2DCFR09A7S5A0Q0KP");
        Assert.Contains(scenario.Aircraft, a => a.AirportId == "KOAK");
        Assert.Contains(scenario.Aircraft, a => a.AirportId == "OAK");

        Assert.Equal(["OAK"], ScenarioAirportCollector.AirportsOf([scenario]));
    }

    /// <summary>A real scenario whose 18 aircraft name no airport, with its primary airport cleared: no airport at all.</summary>
    [Fact]
    public void AirportsOf_NoPrimaryAndNoAircraftAirports_IsEmpty()
    {
        Scenario scenario = Example("01H06NVK7VN8BS7MCDXHKJZ7MQ");
        Assert.All(scenario.Aircraft, a => Assert.Null(a.AirportId));
        scenario.PrimaryAirportId = null;

        Assert.Empty(ScenarioAirportCollector.AirportsOf([scenario]));
    }

    /// <summary>FAT's scenario files flight plans to and from KSEA, KDEN and others no aircraft starts at: they are left out.</summary>
    [Fact]
    public void AirportsOf_FlightPlanOnlyAirports_AreLeftOut()
    {
        Scenario scenario = Example("01HM8ARK79GCPJZG7RW6A0EEKW");
        Assert.Contains(scenario.Aircraft, a => a.FlightPlan?.Departure == "KSEA");
        Assert.Contains(scenario.Aircraft, a => a.FlightPlan?.Destination == "KDEN");

        Assert.Equal(["D86", "FAT", "FCH", "MAE", "O32", "PTV", "TLR", "VIS"], ScenarioAirportCollector.AirportsOf([scenario]));
    }

    /// <summary>Two OAK scenarios and an SFO one: each airport once, sorted ordinal, whatever order the scenarios come in.</summary>
    [Fact]
    public void AirportsOf_DuplicatesAcrossScenarios_ListedOnceSorted()
    {
        Scenario[] scenarios = [Example("01HCE8FW7P81CGQQFZP0HRM9F7"), Example("01HB59R6JNK4HC9QGHXWE21ETC"), Example("01HCHWFSVGKA6H0F0QSFFG9MMN")];

        Assert.Equal(["OAK", "SFO"], ScenarioAirportCollector.AirportsOf(scenarios));
        Assert.Equal(["OAK", "SFO"], ScenarioAirportCollector.AirportsOf(scenarios.Reverse()));
    }

    [Fact]
    public void AirportList_WriteThenRead_SortedDistinctFaaIdsUnderTheHeader()
    {
        string path = Path.Combine(Path.GetTempPath(), "precompute-airports-" + Guid.NewGuid() + ".txt");
        try
        {
            PrecomputeAirportList.Write(path, ["SFO", "KOAK", "OAK", " fat "]);

            Assert.Equal($"{PrecomputeAirportList.Header}\nFAT\nOAK\nSFO\n", File.ReadAllText(path));
            Assert.Equal(["FAT", "OAK", "SFO"], PrecomputeAirportList.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AirportList_Missing_ThrowsNamingPathAndCommand()
    {
        string path = Path.Combine(Path.GetTempPath(), "precompute-airports-" + Guid.NewGuid() + ".txt");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => PrecomputeAirportList.Read(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("--refresh-airports", ex.Message, StringComparison.Ordinal);
    }

    private static Scenario Example(string id) =>
        ScenarioAirportCollector.Parse(
            File.ReadAllText(Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples", id + ".json"))
        );
}
