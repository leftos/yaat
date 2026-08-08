using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Hosts one strips entry's pane layout: a single <see cref="VStripsView"/>
/// normally, or two full views around a draggable <see cref="GridSplitter"/>
/// when the entry is split (<see cref="StripsSplitMode.SideBySide"/> /
/// <see cref="StripsSplitMode.Stacked"/>). DataContext is the
/// <see cref="VStripsDockEntryViewModel"/>; the panes bind to its
/// <c>Vm</c> / <c>SecondaryVm</c> explicitly. Splitter drags write back
/// <c>SplitRatio</c>, which MainViewModel persists for the student entry.
/// Both hosts of an entry (hidden TabItem + popped-out window) each build
/// their own view instances — same duplication the unsplit path always had.
/// </summary>
public sealed class VStripsSplitHost : ContentControl
{
    /// <summary>
    /// Rest color of the pane splitter. Public so tests can pin that the
    /// splitter is visibly styled rather than falling back to the theme
    /// default, which is indistinguishable from the bay dividers inside a
    /// pane.
    /// </summary>
    public static readonly ImmutableSolidColorBrush SplitterRestBrush = new(0xFF4A4A52);

    private static readonly ImmutableSolidColorBrush SplitterActiveBrush = new(0xFF6A6A78);
    private static readonly ImmutableSolidColorBrush SplitterGripBrush = new(0xFFA0A0AC);
    private const double SplitterThickness = 8;

    private readonly VStripsView _primaryView = new();
    private VStripsView? _secondaryView;
    private VStripsDockEntryViewModel? _entry;
    private Grid? _splitGrid;
    private bool _updatingRatioFromDrag;

    public VStripsSplitHost()
    {
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        DataContextChanged += (_, _) => OnEntryChanged();
    }

    private void OnEntryChanged()
    {
        if (_entry is not null)
        {
            _entry.PropertyChanged -= OnEntryPropertyChanged;
        }
        _entry = DataContext as VStripsDockEntryViewModel;
        if (_entry is not null)
        {
            _entry.PropertyChanged += OnEntryPropertyChanged;
        }
        RebuildLayout();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VStripsDockEntryViewModel.SplitMode):
            case nameof(VStripsDockEntryViewModel.SecondaryVm):
                RebuildLayout();
                break;
            case nameof(VStripsDockEntryViewModel.SplitRatio):
                // Splitter drags already moved the definitions; only external
                // ratio changes (e.g. preference restore) need re-applying.
                if (!_updatingRatioFromDrag)
                {
                    ApplyRatioToDefinitions();
                }
                break;
        }
    }

    private void RebuildLayout()
    {
        // Detach the pane views from the previous arrangement before
        // re-parenting them — Avalonia throws on double visual parents.
        _splitGrid?.Children.Clear();
        _splitGrid = null;
        Content = null;

        _primaryView.DataContext = _entry?.Vm;

        if (_entry is null || _entry.SplitMode == StripsSplitMode.None || _entry.SecondaryVm is null)
        {
            Content = _primaryView;
            return;
        }

        _secondaryView ??= new VStripsView();
        _secondaryView.DataContext = _entry.SecondaryVm;

        var grid = new Grid();
        var ratio = _entry.SplitRatio;
        GridSplitter splitter;
        if (_entry.SplitMode == StripsSplitMode.SideBySide)
        {
            grid.ColumnDefinitions =
            [
                new ColumnDefinition(ratio, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(1 - ratio, GridUnitType.Star),
            ];
            splitter = CreateSplitter(vertical: true);
            Grid.SetColumn(_primaryView, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_secondaryView, 2);
        }
        else
        {
            grid.RowDefinitions =
            [
                new RowDefinition(ratio, GridUnitType.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(1 - ratio, GridUnitType.Star),
            ];
            splitter = CreateSplitter(vertical: false);
            Grid.SetRow(_primaryView, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(_secondaryView, 2);
        }

        splitter.DragCompleted += (_, _) => CaptureRatioFromDefinitions();

        grid.Children.Add(_primaryView);
        grid.Children.Add(splitter);
        grid.Children.Add(_secondaryView);
        _splitGrid = grid;
        Content = grid;
    }

    private static GridSplitter CreateSplitter(bool vertical)
    {
        var splitter = new GridSplitter { ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows };
        if (vertical)
        {
            splitter.Width = SplitterThickness;
        }
        else
        {
            splitter.Height = SplitterThickness;
        }

        splitter.Template = new FuncControlTemplate<GridSplitter>(
            (parent, _) =>
            {
                var dots = new StackPanel
                {
                    Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                for (var i = 0; i < 3; i++)
                {
                    dots.Children.Add(
                        new Ellipse
                        {
                            Width = 3,
                            Height = 3,
                            Fill = SplitterGripBrush,
                        }
                    );
                }
                var border = new Border { Child = dots };
                border.Bind(BackgroundProperty, parent.GetObservable(BackgroundProperty));
                return border;
            }
        );

        // The rest color must come from a style rather than a local value —
        // a local Background would outrank the pointerover/pressed setters
        // and the hover highlight would never show.
        splitter.Styles.Add(new Style(x => x.OfType<GridSplitter>()) { Setters = { new Setter(BackgroundProperty, SplitterRestBrush) } });
        splitter.Styles.Add(
            new Style(x => x.OfType<GridSplitter>().Class(":pointerover")) { Setters = { new Setter(BackgroundProperty, SplitterActiveBrush) } }
        );
        splitter.Styles.Add(
            new Style(x => x.OfType<GridSplitter>().Class(":pressed")) { Setters = { new Setter(BackgroundProperty, SplitterActiveBrush) } }
        );
        return splitter;
    }

    private void CaptureRatioFromDefinitions()
    {
        if (_entry is null || _splitGrid is null)
        {
            return;
        }
        double first;
        double second;
        if (_entry.SplitMode == StripsSplitMode.SideBySide)
        {
            first = _splitGrid.ColumnDefinitions[0].ActualWidth;
            second = _splitGrid.ColumnDefinitions[2].ActualWidth;
        }
        else
        {
            first = _splitGrid.RowDefinitions[0].ActualHeight;
            second = _splitGrid.RowDefinitions[2].ActualHeight;
        }
        var total = first + second;
        if (total <= 0)
        {
            return;
        }
        _updatingRatioFromDrag = true;
        try
        {
            _entry.SplitRatio = first / total;
        }
        finally
        {
            _updatingRatioFromDrag = false;
        }
    }

    private void ApplyRatioToDefinitions()
    {
        if (_entry is null || _splitGrid is null)
        {
            return;
        }
        var ratio = _entry.SplitRatio;
        if (_entry.SplitMode == StripsSplitMode.SideBySide)
        {
            _splitGrid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
            _splitGrid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
        }
        else
        {
            _splitGrid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
            _splitGrid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
        }
    }
}
