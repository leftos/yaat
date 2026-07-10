using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Scenarios;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Scenarios and Weather > Arrival Generators Editor.
// Captures the editor pre-populated with two arrival generators that mirror
// the S2-OAK-3 ERB-EF reference scenario (jet/large on 30, piston/small on
// 28R). The OAK runway list and a couple of sample positions are seeded so
// the runway and AutoTrack dropdowns render with realistic values.
internal sealed class ArrivalGeneratorsEditorScene : StandaloneWindowSceneBase
{
    public override string Name => "arrival-generators-editor";

    public override int Width => 820;

    public override int Height => 760;

    public override Window CreateWindow(CaptureContext ctx)
    {
        var generators = new List<ScenarioGeneratorConfig>
        {
            new()
            {
                Id = "01HA3KGNJ69M8W0A3B141PC86Q",
                Runway = "30",
                EngineType = "Jet",
                WeightCategory = "Large",
                InitialDistance = 20,
                MaxDistance = 50,
                IntervalDistance = 5,
                MaxTime = 3600,
                IntervalTime = 240,
                RandomizeInterval = true,
                RandomizeWeightCategory = true,
                AutoTrackConfiguration = new AutoTrackConditions { PositionId = "01GEAMB98RKCPP9HCNPW5AVDA5", ScratchPad = "OA1" },
            },
            new()
            {
                Id = "01HA3KJHZV9MVH06R7NX1ZVX73",
                Runway = "28R",
                EngineType = "Piston",
                WeightCategory = "Small",
                InitialDistance = 15,
                MaxDistance = 50,
                IntervalDistance = 5,
                MaxTime = 3600,
                IntervalTime = 240,
                RandomizeInterval = true,
                RandomizeWeightCategory = true,
                AutoTrackConfiguration = new AutoTrackConditions { PositionId = "01GEAMB98RKCPP9HCNPW5AVDA5", ScratchPad = "OA1" },
            },
        };

        var positions = new List<ScenarioPositionDto>
        {
            new("01GEAMB98RKCPP9HCNPW5AVDA5", "OAK_APP", "Oakland Approach"),
            new("01GEAMB98RKCPP9HCNPW5AVDA6", "NCT_S_APP", "NCT South Approach"),
            new("01GEAMB98RKCPP9HCNPW5AVDA7", "NCT_E_APP", "NCT East Approach"),
        };

        var runways = new List<string> { "12", "30", "10L", "28R", "10R", "28L" };

        var vfrArrivals = new List<VfrArrivalGeneratorConfig>
        {
            new()
            {
                Id = "vfr-south",
                BearingFrom = 120,
                BearingTo = 200,
                InitialDistance = 12,
                MaxDistance = 20,
                IntervalTime = 240,
            },
        };

        var overflights = new List<OverflightGeneratorConfig>
        {
            new()
            {
                Id = "of-eastwest",
                FromBearingFrom = 80,
                FromBearingTo = 100,
                ToBearingFrom = 260,
                ToBearingTo = 280,
                IntervalTime = 300,
            },
        };

        var vm = new ArrivalGeneratorsEditorViewModel(generators, vfrArrivals, overflights, positions, runways);
        return new ArrivalGeneratorsEditorWindow(vm, new UserPreferences(), null, null);
    }
}
