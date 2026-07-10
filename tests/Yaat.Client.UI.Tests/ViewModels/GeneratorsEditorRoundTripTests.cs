using System.Text.Json;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The generator editor must round-trip a config without inventing values. An omitted <c>maxTime</c> means
/// "runs for the whole session"; an omitted <c>enabled</c> means "follow the time window". Materializing a
/// default for either would silently change a scenario the first time an instructor opened the editor and
/// pressed Apply.
/// </summary>
public class GeneratorsEditorRoundTripTests
{
    private static ArrivalGeneratorsEditorViewModel Build(
        IReadOnlyList<ScenarioGeneratorConfig>? arrivals = null,
        IReadOnlyList<VfrArrivalGeneratorConfig>? vfrArrivals = null,
        IReadOnlyList<OverflightGeneratorConfig>? overflights = null
    ) => new(arrivals ?? [], vfrArrivals ?? [], overflights ?? [], [], ["28R", "28L"]);

    [Fact]
    public void ArrivalGenerator_WithOmittedMaxTime_RoundTripsAsUnbounded()
    {
        var vm = Build(
            arrivals:
            [
                new ScenarioGeneratorConfig
                {
                    Id = "gen",
                    Runway = "28R",
                    MaxTime = null,
                },
            ]
        );

        Assert.Null(vm.Generators[0].MaxTime);
        Assert.Null(vm.BuildPayload().AircraftGenerators[0].MaxTime);
    }

    [Fact]
    public void ArrivalGenerator_WithMaxTime_KeepsIt()
    {
        var vm = Build(
            arrivals:
            [
                new ScenarioGeneratorConfig
                {
                    Id = "gen",
                    Runway = "28R",
                    MaxTime = 1200,
                },
            ]
        );

        Assert.Equal(1200, vm.Generators[0].MaxTime);
        Assert.Equal(1200, vm.BuildPayload().AircraftGenerators[0].MaxTime);
    }

    /// <summary>An unbounded generator must not gain a maxTime just by being serialized.</summary>
    [Fact]
    public void UnboundedArrivalGenerator_SerializesWithoutAMaxTimeProperty()
    {
        var vm = Build(
            arrivals:
            [
                new ScenarioGeneratorConfig
                {
                    Id = "gen",
                    Runway = "28R",
                    MaxTime = null,
                },
            ]
        );

        var json = vm.BuildJson();

        Assert.DoesNotContain("maxTime", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveToggle_RoundTripsAcrossEveryGeneratorKind(bool? enabled)
    {
        var vm = Build(
            arrivals:
            [
                new ScenarioGeneratorConfig
                {
                    Id = "ifr",
                    Runway = "28R",
                    Enabled = enabled,
                },
            ],
            vfrArrivals: [new VfrArrivalGeneratorConfig { Id = "vfr", Enabled = enabled }],
            overflights: [new OverflightGeneratorConfig { Id = "of", Enabled = enabled }]
        );

        Assert.Equal(enabled, vm.Generators[0].Enabled);
        Assert.Equal(enabled, vm.VfrArrivalGenerators[0].Enabled);
        Assert.Equal(enabled, vm.OverflightGenerators[0].Enabled);

        var payload = vm.BuildPayload();
        Assert.Equal(enabled, payload.AircraftGenerators[0].Enabled);
        Assert.Equal(enabled, payload.VfrArrivalGenerators[0].Enabled);
        Assert.Equal(enabled, payload.OverflightGenerators[0].Enabled);
    }

    /// <summary>An untouched Active toggle must not write an override into the scenario.</summary>
    [Fact]
    public void UntouchedActiveToggle_SerializesWithoutAnEnabledProperty()
    {
        var vm = Build(
            arrivals:
            [
                new ScenarioGeneratorConfig
                {
                    Id = "gen",
                    Runway = "28R",
                    Enabled = null,
                },
            ]
        );

        Assert.DoesNotContain("enabled", vm.BuildJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJson_EmitsAllThreeGeneratorArrays()
    {
        var vm = Build(
            arrivals: [new ScenarioGeneratorConfig { Id = "ifr", Runway = "28R" }],
            vfrArrivals:
            [
                new VfrArrivalGeneratorConfig
                {
                    Id = "vfr",
                    BearingFrom = 120,
                    BearingTo = 200,
                },
            ],
            overflights: [new OverflightGeneratorConfig { Id = "of", ExitDistance = 30 }]
        );

        var payload = JsonSerializer.Deserialize<GeneratorsPayload>(vm.BuildJson());

        Assert.NotNull(payload);
        Assert.Equal("ifr", Assert.Single(payload.AircraftGenerators).Id);
        var vfr = Assert.Single(payload.VfrArrivalGenerators);
        Assert.Equal("vfr", vfr.Id);
        Assert.Equal(120, vfr.BearingFrom);
        Assert.Equal(200, vfr.BearingTo);
        var overflight = Assert.Single(payload.OverflightGenerators);
        Assert.Equal("of", overflight.Id);
        Assert.Equal(30, overflight.ExitDistance);
        Assert.True(overflight.SnapHemisphericAltitude);
    }

    [Fact]
    public void Revert_RestoresEveryGeneratorKind()
    {
        var vm = Build(
            arrivals: [new ScenarioGeneratorConfig { Id = "ifr", Runway = "28R" }],
            vfrArrivals: [new VfrArrivalGeneratorConfig { Id = "vfr" }],
            overflights: [new OverflightGeneratorConfig { Id = "of" }]
        );

        vm.AddGeneratorCommand.Execute(null);
        vm.AddVfrArrivalGeneratorCommand.Execute(null);
        vm.AddOverflightGeneratorCommand.Execute(null);
        Assert.Equal(2, vm.Generators.Count);
        Assert.Equal(2, vm.VfrArrivalGenerators.Count);
        Assert.Equal(2, vm.OverflightGenerators.Count);

        vm.RevertCommand.Execute(null);

        Assert.Equal("ifr", Assert.Single(vm.Generators).Id);
        Assert.Equal("vfr", Assert.Single(vm.VfrArrivalGenerators).Id);
        Assert.Equal("of", Assert.Single(vm.OverflightGenerators).Id);
    }
}
