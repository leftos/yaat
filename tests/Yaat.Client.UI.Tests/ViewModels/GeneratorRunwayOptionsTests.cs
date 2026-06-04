using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The generator editor's runway options are the union of the active ground layout's runway ends and
/// the runways already on the loaded generators — so an existing generator's runway is always offered
/// even before the ground layout has loaded (the editor can open first), which used to leave the
/// dropdown empty.
/// </summary>
public class GeneratorRunwayOptionsTests
{
    [Fact]
    public void UnionRunwayIds_NullLayout_ReturnsGeneratorRunways_Deduped()
    {
        var generators = new[]
        {
            new ScenarioGeneratorConfig { Runway = "28R" },
            new ScenarioGeneratorConfig { Runway = "30" },
            new ScenarioGeneratorConfig { Runway = "28R" },
            new ScenarioGeneratorConfig { Runway = "" },
        };

        var union = MainViewModel.UnionRunwayIds(null, generators);

        Assert.Equal(["28R", "30"], union);
    }

    [Fact]
    public void UnionRunwayIds_LayoutAndGenerators_UnionsLayoutEndsThenExtraGeneratorRunways()
    {
        var layout = new AirportGroundLayout
        {
            AirportId = "KOAK",
            Runways =
            [
                new GroundRunway
                {
                    Name = "28R - 10L",
                    Coordinates = [],
                    WidthFt = 150,
                },
            ],
        };
        var generators = new[]
        {
            new ScenarioGeneratorConfig { Runway = "28R" }, // already in the layout
            new ScenarioGeneratorConfig { Runway = "33" }, // not in the layout — must still be offered
        };

        var union = MainViewModel.UnionRunwayIds(layout, generators);

        Assert.Equal(["28R", "10L", "33"], union);
    }

    [Fact]
    public void UnionRunwayIds_NoLayoutNoGenerators_IsEmpty()
    {
        Assert.Empty(MainViewModel.UnionRunwayIds(null, []));
    }
}
