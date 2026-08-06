using System.ComponentModel;
using Avalonia.Controls;
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
            splitter = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns };
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
            splitter = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows };
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
