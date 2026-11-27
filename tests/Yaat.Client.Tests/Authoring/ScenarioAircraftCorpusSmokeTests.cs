using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Every committed ATCTrainer example scenario opens in the Aircraft tab and is walked row by row without an edit: one
/// row per aircraft, nothing to undo, nothing dirty, and no field error on the detail panel, flight plan, presets or
/// auto-track after any selection.
/// </summary>
public sealed class ScenarioAircraftCorpusSmokeTests : IDisposable
{
    private static readonly string ExamplesDir = Path.Combine(
        TestRepo.Root("the scenario examples are read from the checkout."),
        "docs",
        "atctrainer-scenario-examples"
    );

    // Every field of the detail panel and the flight plan that shows an error under it.
    private static readonly (string Name, Func<ScenarioAircraftViewModel, string?> Error)[] ErrorFields =
    [
        ("spawn delay", vm => vm.SpawnDelayError),
        ("taxi time", vm => vm.TaxiTimeError),
        ("latitude", vm => vm.LatitudeError),
        ("longitude", vm => vm.LongitudeError),
        ("altitude", vm => vm.AltitudeError),
        ("speed", vm => vm.SpeedError),
        ("heading", vm => vm.HeadingError),
        ("distance from runway", vm => vm.DistanceFromRunwayError),
        ("cruise altitude", vm => vm.CruiseAltitudeError),
        ("cruise speed", vm => vm.CruiseSpeedError),
        ("handoff delay", vm => vm.HandoffDelayError),
    ];

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftCorpusSmokeTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    /// <summary>Every example scenario under <c>docs/atctrainer-scenario-examples</c>, as the Sim round-trip corpus reads them.</summary>
    public static TheoryData<string> CorpusScenarios()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(ExamplesDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusScenarios))]
    public void AircraftEditor_OpensEveryCorpusScenario_WithoutAnEdit(string scenarioPath)
    {
        string text = File.ReadAllText(scenarioPath);
        string id = Path.GetFileNameWithoutExtension(scenarioPath);
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, text, id);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, id);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();

        Assert.Equal(session.Document.Scenario.Aircraft.Count, vm.Rows.Count);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);

        foreach (ScenarioAircraftRowViewModel row in vm.Rows)
        {
            vm.Selected = row;

            Assert.Same(row, vm.Selected);
            Assert.True(vm.IsDetailVisible);
            Assert.Null(FieldError(vm, row));
            Assert.False(session.CanUndo);
            Assert.False(session.IsDirty);
        }
    }

    // The first error the panel shows for the selected row, named, or null when every field is clean.
    private static string? FieldError(ScenarioAircraftViewModel vm, ScenarioAircraftRowViewModel row)
    {
        foreach ((string name, Func<ScenarioAircraftViewModel, string?> error) in ErrorFields)
        {
            if (error(vm) is { } text)
            {
                return $"{row.Callsign}: {name} shows \"{text}\"";
            }
        }

        foreach (ScenarioPresetRowViewModel preset in vm.Presets)
        {
            if (preset.CommandError is { } commandError)
            {
                return $"{row.Callsign}: preset {preset.Index} command shows \"{commandError}\"";
            }

            if (preset.OffsetError is { } offsetError)
            {
                return $"{row.Callsign}: preset {preset.Index} offset shows \"{offsetError}\"";
            }
        }

        return null;
    }
}
