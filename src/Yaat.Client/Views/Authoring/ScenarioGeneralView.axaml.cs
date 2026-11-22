using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor's General tab over a <see cref="ViewModels.Authoring.ScenarioGeneralViewModel"/>: one scrolling
/// page whose sections (Settings, ATC positions, Initialization triggers, Generators, YAAT extensions, Strip bays)
/// collapse to a one-line summary, each list a grid of rows edited in place. A text field commits when it loses focus or
/// on Enter: it names its commit command in its Tag. A picker commits through the view model's pick property, which takes
/// only the user's selection.
/// </summary>
public partial class ScenarioGeneralView : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="ScenarioGeneralView"/> class.</summary>
    public ScenarioGeneralView()
    {
        InitializeComponent();
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnFieldLostFocus(object? sender, FocusChangedEventArgs e) => Commit(e.Source);

    private static void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit(e.Source);
        }
    }

    // Runs the commit command a text field names in its Tag.
    private static void Commit(object? source)
    {
        if ((source is TextBox { Tag: ICommand command }) && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
