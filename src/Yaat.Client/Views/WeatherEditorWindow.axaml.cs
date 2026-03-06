using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class WeatherEditorWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<WeatherEditorWindow>();

    private readonly Func<string, string, Task>? _applyCallback;

    public WeatherEditorWindow()
        : this(new WeatherEditorViewModel(), new UserPreferences(), null) { }

    public WeatherEditorWindow(WeatherEditorViewModel viewModel, UserPreferences preferences, Func<string, string, Task>? applyCallback)
    {
        _applyCallback = applyCallback;
        DataContext = viewModel;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "WeatherEditor", 650, 550).Restore();

        this.FindControl<Button>("ApplyButton")!.Click += OnApplyClick;
        this.FindControl<Button>("SaveAsButton")!.Click += OnSaveAsClick;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private async void OnApplyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not WeatherEditorViewModel vm || _applyCallback is null)
        {
            return;
        }

        try
        {
            var json = vm.BuildJson();
            var name = string.IsNullOrWhiteSpace(vm.Name) ? "Custom Weather" : vm.Name;
            await _applyCallback(json, name);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Apply weather error");
        }
    }

    private async void OnSaveAsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not WeatherEditorViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save Weather As…",
                SuggestedFileName = string.IsNullOrWhiteSpace(vm.Name) ? "weather" : vm.Name,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                DefaultExtension = "json",
            }
        );

        if (file is null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            var json = vm.BuildJson();
            await File.WriteAllTextAsync(path, json);
            Log.LogInformation("Weather saved to {Path}", path);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Save weather error");
        }
    }
}
