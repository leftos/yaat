using System.Text.Json.Nodes;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class ArrivalGeneratorsEditorWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<ArrivalGeneratorsEditorWindow>();

    private readonly Func<string, Task<CommandResultDto>>? _applyCallback;
    private readonly Func<string?>? _scenarioJsonProvider;
    private readonly IFilePickerService _filePicker;

    public ArrivalGeneratorsEditorWindow()
        : this(new ArrivalGeneratorsEditorViewModel([], [], []), new UserPreferences(), null, null) { }

    public ArrivalGeneratorsEditorWindow(
        ArrivalGeneratorsEditorViewModel viewModel,
        UserPreferences preferences,
        Func<string, Task<CommandResultDto>>? applyCallback,
        Func<string?>? scenarioJsonProvider
    )
    {
        _applyCallback = applyCallback;
        _scenarioJsonProvider = scenarioJsonProvider;
        DataContext = viewModel;
        InitializeComponent();
        _filePicker = new AvaloniaFilePickerService(this);
        new WindowGeometryHelper(this, preferences, "ArrivalGeneratorsEditor", 760, 560).Restore();

        this.FindControl<Button>("ApplyButton")!.Click += OnApplyClick;
        this.FindControl<Button>("SaveAsButton")!.Click += OnSaveAsClick;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private async void OnApplyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ArrivalGeneratorsEditorViewModel vm || _applyCallback is null)
        {
            return;
        }

        try
        {
            var json = vm.BuildJson();
            var result = await _applyCallback(json);
            vm.StatusMessage = result.Success
                ? $"Applied {vm.Generators.Count} generator(s)" + (result.Message is not null ? $" — {result.Message}" : "")
                : $"Apply failed: {result.Message}";
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Apply arrival generators error");
            vm.StatusMessage = $"Apply error: {ex.Message}";
        }
    }

    private async void OnSaveAsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ArrivalGeneratorsEditorViewModel vm)
        {
            return;
        }

        var path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Save Scenario As…",
                SuggestedFileName: "scenario",
                Filters: [new FilePickerFilter("JSON", ["*.json"])],
                DefaultExtension: "json"
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            var sourceJson = _scenarioJsonProvider?.Invoke();
            if (string.IsNullOrEmpty(sourceJson))
            {
                vm.StatusMessage = "Save As needs the originally-loaded scenario JSON; none is available";
                return;
            }

            var node = JsonNode.Parse(sourceJson);
            if (node is not JsonObject obj)
            {
                vm.StatusMessage = "Loaded scenario JSON is not an object";
                return;
            }

            var generatorsJson = vm.BuildJson();
            obj["aircraftGenerators"] = JsonNode.Parse(generatorsJson);

            await File.WriteAllTextAsync(path, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            vm.StatusMessage = $"Saved to {path}";
            Log.LogInformation("Scenario with edited generators saved to {Path}", path);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Save As failed");
            vm.StatusMessage = $"Save error: {ex.Message}";
        }
    }
}
