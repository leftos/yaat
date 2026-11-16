using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's entry areas, drawn over the canvas and never hit: on a STARS profile one unboxed green area under the SSA's
/// default place, showing the entry being typed under its kind label and, once it runs, its response; on ERAM and Mixed the
/// Response Area (empty for now) over the MCA, whose Preview shows the entry and whose Feedback shows the last response.
/// Text is scaled by the FDB brightness group.
/// </summary>
public partial class ScopeEntryLayer : UserControl
{
    private const double StarsLineHeight = 19;
    private const double EramLineHeight = 18;
    private const double EramLabelSize = 13;

    private static readonly SKColor StarsText = SKColor.Parse("#2fe02f");
    private static readonly SKColor EramText = SKColor.Parse("#e6e6e6");
    private static readonly SKColor Ok = SKColor.Parse("#3fd06a");
    private static readonly SKColor Error = SKColor.Parse("#ff4040");
    private static readonly SKColor PilotLabel = SKColor.Parse("#E0B84A");
    private static readonly SKColor DialectLabel = SKColor.Parse("#58c4e6");

    private ScopeViewModel? _vm;

    public ScopeEntryLayer()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as ScopeViewModel;
        _vm?.PropertyChanged += OnViewModelPropertyChanged;
        Refresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(ScopeViewModel.EntryText)
                or nameof(ScopeViewModel.EntryResponse)
                or nameof(ScopeViewModel.ProfileKind)
                or nameof(ScopeViewModel.SelectedAircraft)
                or nameof(ScopeViewModel.Brightness)
        )
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        StarsArea.Children.Clear();
        EramPreview.Children.Clear();
        EramFeedback.Children.Clear();
        if (_vm is not { } vm)
        {
            StarsArea.IsVisible = false;
            EramBoxes.IsVisible = false;
            return;
        }

        EramBoxes.IsVisible = vm.IsEramEntryLayout;
        if (vm.IsEramEntryLayout)
        {
            FillEramPreview(vm);
            FillResponse(vm, EramFeedback, EramText, EramLineHeight);
        }
        else
        {
            FillStarsArea(vm);
        }

        StarsArea.IsVisible = StarsArea.Children.Count > 0;
    }

    private void FillStarsArea(ScopeViewModel vm)
    {
        if (vm.EntryText.Length == 0)
        {
            FillResponse(vm, StarsArea, StarsText, StarsLineHeight);
            return;
        }

        AddLabel(vm, StarsArea, double.NaN, StarsLineHeight);
        foreach (string line in vm.EntryPreviewLines)
        {
            StarsArea.Children.Add(Line(line, Brush(vm, StarsText), StarsLineHeight));
        }
    }

    private void FillEramPreview(ScopeViewModel vm)
    {
        AddLabel(vm, EramPreview, EramLabelSize, EramLineHeight);
        IReadOnlyList<string> lines = vm.EntryPreviewLines;
        if (lines.Count == 0)
        {
            lines = [ScopeViewModel.Caret];
        }

        foreach (string line in lines)
        {
            EramPreview.Children.Add(Line(line, Brush(vm, EramText), EramLineHeight));
        }
    }

    private static void AddLabel(ScopeViewModel vm, Panel panel, double fontSize, double lineHeight)
    {
        ScopeEntryReading reading = vm.EntryReading;
        if (reading.Label.Length == 0)
        {
            return;
        }

        SKColor color = reading.Kind is ScopeEntryKind.Stars or ScopeEntryKind.Eram ? DialectLabel : PilotLabel;
        TextBlock label = Line(reading.Label, Brush(vm, color), lineHeight);
        if (!double.IsNaN(fontSize))
        {
            label.FontSize = fontSize;
        }

        panel.Children.Add(label);
    }

    /// <summary>The response's lines: the message coloured by its kind (accept green, error red), then the echo.</summary>
    private static void FillResponse(ScopeViewModel vm, Panel panel, SKColor text, double lineHeight)
    {
        if (vm.EntryResponse is not { } response)
        {
            return;
        }

        bool eram = vm.IsEramEntryLayout;
        SKColor message = response.Kind switch
        {
            ScopeResponseKind.Error => Error,
            ScopeResponseKind.Accept when !eram => Ok,
            _ => text,
        };
        SKColor echo = ((response.Kind == ScopeResponseKind.Accept) && !eram) ? Ok : text;
        IReadOnlyList<ScopeResponseLine> lines = vm.EntryResponseLines;
        for (int i = 0; i < lines.Count; i++)
        {
            // Only the first line of an ERAM acceptance is its ACCEPT; the descriptor and ACID under it are plain lines.
            ScopeResponseLine line = lines[i];
            bool eramAccept = eram && (i == 0) && line.IsMessage && (response.Kind == ScopeResponseKind.Accept);
            panel.Children.Add(eramAccept ? EramAcceptLine(vm, lineHeight) : Line(line.Text, Brush(vm, line.IsMessage ? message : echo), lineHeight));
        }
    }

    // ERAM's accept: a green check, then ACCEPT in the box's text colour.
    private static StackPanel EramAcceptLine(ScopeViewModel vm, double lineHeight)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Line("✓", Brush(vm, Ok), lineHeight));
        row.Children.Add(Line(" " + ScopeViewModel.AcceptMessage, Brush(vm, EramText), lineHeight));
        return row;
    }

    private static TextBlock Line(string text, IBrush brush, double lineHeight) =>
        new()
        {
            Text = text,
            Foreground = brush,
            LineHeight = lineHeight,
            TextWrapping = TextWrapping.NoWrap,
        };

    private static SolidColorBrush Brush(ScopeViewModel vm, SKColor color)
    {
        SKColor scaled = ScopeBrightness.Apply(color, vm.Brightness.Fdb);
        return new SolidColorBrush(Color.FromArgb(scaled.Alpha, scaled.Red, scaled.Green, scaled.Blue));
    }
}
