using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
    public DataGridView()
    {
        InitializeComponent();
    }

    public DataGrid? GetDataGrid()
    {
        return this.FindControl<DataGrid>("AircraftGrid");
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var grid = GetDataGrid();
        if (grid is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        ApplyDelayedGroupState(grid, vm);

        // Re-apply after group rebuild (triggered by Refresh() on group transitions)
        ((INotifyCollectionChanged)vm.AircraftView).CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ApplyDelayedGroupState(grid, vm), DispatcherPriority.Render);
        };

        // Apply when user toggles via menu
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsDelayedGroupCollapsed))
            {
                ApplyDelayedGroupState(grid, vm);
            }
        };
    }

    private static void ApplyDelayedGroupState(DataGrid grid, MainViewModel vm)
    {
        if (grid.ItemsSource is not DataGridCollectionView view || view.Groups is null)
        {
            return;
        }

        foreach (var group in view.Groups)
        {
            if (group is DataGridCollectionViewGroup g && g.Key?.ToString() == "Delayed")
            {
                if (vm.IsDelayedGroupCollapsed)
                {
                    grid.CollapseRowGroup(g, false);
                }
                else
                {
                    grid.ExpandRowGroup(g, false);
                }
                break;
            }
        }
    }
}
