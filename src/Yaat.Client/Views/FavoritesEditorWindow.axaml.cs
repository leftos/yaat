using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Two-pane manager for favorite sets. The left pane lists every container as a peer — Global,
/// each airport, each scenario, each named set (with a Loaded checkbox) — plus a "Not in any set"
/// view of orphaned favorites. The right pane lists the selected container's favorites with
/// multi-select. A favorite is one entity that can belong to any number of sets: Add to… adds a
/// membership, Move to… transfers it, Remove takes it out of the selected set only, and Delete
/// destroys it everywhere. All mutations commit straight to <see cref="FavoriteStore"/>; the live
/// bar/panel follow via the store's Changed event.
/// </summary>
public partial class FavoritesEditorWindow : Window
{
    /// <summary>Sentinel container id for the "Not in any set" view; real set ids are 8-hex.</summary>
    private const string OrphansContainerId = "*orphans*";

    private readonly UserPreferences _preferences;
    private readonly FavoriteStore _store;
    private readonly WindowGeometryHelper _geometryHelper;

    private string _selectedContainerId;

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public FavoritesEditorWindow()
        : this(new UserPreferences(), new FavoriteStore(FavoriteStore.DefaultRootDir)) { }

    public FavoritesEditorWindow(UserPreferences preferences, FavoriteStore store)
    {
        InitializeComponent();
        _preferences = preferences;
        _store = store;
        _selectedContainerId = store.GlobalSet.Id;
        _geometryHelper = new WindowGeometryHelper(this, preferences, "FavoritesEditor", 760, 480);
        _geometryHelper.Restore();

        WireButton("NewSetButton", OnNewSetClick);
        WireButton("RenameSetButton", OnRenameSetClick);
        WireButton("DeleteSetButton", OnDeleteSetClick);
        WireButton("MoveUpButton", OnMoveUpClick);
        WireButton("MoveDownButton", OnMoveDownClick);
        WireButton("AddToButton", (s, e) => ShowTransferFlyout(s, move: false));
        WireButton("MoveToButton", (s, e) => ShowTransferFlyout(s, move: true));
        WireButton("RemoveFromSetButton", OnRemoveFromSetClick);
        WireButton("DeleteFavoritesButton", OnDeleteFavoritesClick);
        WireButton("CloseButton", (_, _) => Close());

        var containers = this.FindControl<ListBox>("ContainersList");
        if (containers is not null)
        {
            containers.SelectionChanged += OnContainerSelectionChanged;
        }

        PopulateContainers();
        PopulateFavorites();
    }

    private void WireButton(string name, EventHandler<RoutedEventArgs> handler)
    {
        var btn = this.FindControl<Button>(name);
        if (btn is not null)
        {
            btn.Click += handler;
        }
    }

    private void PopulateContainers()
    {
        var list = this.FindControl<ListBox>("ContainersList");
        if (list is null)
        {
            return;
        }

        var items = new List<ListBoxItem>();
        foreach (var set in _store.OrderedSets)
        {
            items.Add(set.Kind == FavoriteSetKind.Named ? CreateNamedSetRow(set) : CreatePlainRow(set.DisplayName, set.Id));
        }

        items.Add(CreatePlainRow($"Not in any set ({_store.GetOrphanFavorites().Count})", OrphansContainerId));

        list.SelectionChanged -= OnContainerSelectionChanged;
        list.ItemsSource = items;
        var selected =
            items.FirstOrDefault(i => string.Equals(i.Tag as string, _selectedContainerId, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        list.SelectedItem = selected;
        _selectedContainerId = (string)selected.Tag!;
        list.SelectionChanged += OnContainerSelectionChanged;
    }

    private static ListBoxItem CreatePlainRow(string text, string containerId)
    {
        return new ListBoxItem
        {
            Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            Tag = containerId,
        };
    }

    private ListBoxItem CreateNamedSetRow(FavoriteSet set)
    {
        var setId = set.Id;
        var loadedBox = new CheckBox
        {
            IsChecked = _preferences.LoadedFavoriteSetIds.Any(id => string.Equals(id, setId, StringComparison.OrdinalIgnoreCase)),
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(loadedBox, "Loaded: show this set's favorites in the bar and panel");
        loadedBox.IsCheckedChanged += (_, _) => _preferences.SetFavoriteSetLoaded(setId, loadedBox.IsChecked == true);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(loadedBox);
        row.Children.Add(new TextBlock { Text = set.Name, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = setId };
    }

    private void OnContainerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var list = this.FindControl<ListBox>("ContainersList");
        if (list?.SelectedItem is not ListBoxItem { Tag: string containerId })
        {
            return;
        }

        _selectedContainerId = containerId;
        SetStatus(null);
        PopulateFavorites();
    }

    private bool IsOrphansViewSelected => string.Equals(_selectedContainerId, OrphansContainerId, StringComparison.Ordinal);

    private FavoriteSet? SelectedSet => IsOrphansViewSelected ? null : _store.GetSet(_selectedContainerId);

    private void PopulateFavorites()
    {
        var list = this.FindControl<ListBox>("FavoritesList");
        if (list is null)
        {
            return;
        }

        list.ItemsSource = GetSelectedContainerFavorites().Select(DescribeFavorite).ToList();
    }

    private List<FavoriteCommand> GetSelectedContainerFavorites()
    {
        return IsOrphansViewSelected ? _store.GetOrphanFavorites() : _store.GetSetFavorites(_selectedContainerId);
    }

    private string DescribeFavorite(FavoriteCommand favorite)
    {
        var description = favorite.IsSpacer
            ? $"(blank) — {MainViewModel.NormalizeFavoriteCategory(favorite.Category)}"
            : $"{favorite.Label} — {MainViewModel.NormalizeFavoriteCategory(favorite.Category)}";

        var others = _store
            .GetMembershipSetIds(favorite.Id)
            .Where(id => !string.Equals(id, _selectedContainerId, StringComparison.OrdinalIgnoreCase))
            .Select(id => _store.GetSet(id)?.DisplayName)
            .Where(name => name is not null)
            .ToList();
        return others.Count == 0 ? description : $"{description} — also in: {string.Join(", ", others)}";
    }

    private List<int> GetSelectedFavoriteIndices()
    {
        var list = this.FindControl<ListBox>("FavoritesList");
        return list is null ? [] : list.Selection.SelectedIndexes.ToList();
    }

    private List<FavoriteCommand> GetSelectedFavorites()
    {
        var favorites = GetSelectedContainerFavorites();
        return GetSelectedFavoriteIndices().Distinct().Order().Where(i => (i >= 0) && (i < favorites.Count)).Select(i => favorites[i]).ToList();
    }

    private void ReselectFavorites(IEnumerable<int> indices)
    {
        var list = this.FindControl<ListBox>("FavoritesList");
        if (list is null)
        {
            return;
        }

        list.Selection.Clear();
        foreach (var index in indices)
        {
            list.Selection.Select(index);
        }
    }

    private void SetStatus(string? message)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status is null)
        {
            return;
        }
        if (string.IsNullOrEmpty(message))
        {
            status.IsVisible = false;
            return;
        }
        status.Text = message;
        status.IsVisible = true;
    }

    private IEnumerable<string> NamedSetNames => _store.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).Select(s => s.Name);

    private async void OnNewSetClick(object? sender, RoutedEventArgs e)
    {
        var dlg = new FavoriteSetNameDialog(NamedSetNames, null) { Title = "New Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null)
        {
            return;
        }

        var set = _store.CreateNamedSet(dlg.SetName);
        if (set is null)
        {
            SetStatus($"Could not create set \"{dlg.SetName}\".");
            return;
        }

        _selectedContainerId = set.Id;
        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private async void OnRenameSetClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { Kind: FavoriteSetKind.Named } set)
        {
            SetStatus("Select a named set first — only named sets can be renamed.");
            return;
        }

        var others = NamedSetNames.Where(n => !string.Equals(n, set.Name, StringComparison.OrdinalIgnoreCase));
        var dlg = new FavoriteSetNameDialog(others, set.Name) { Title = "Rename Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null || string.Equals(dlg.SetName, set.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (!_store.RenameNamedSet(set.Id, dlg.SetName))
        {
            SetStatus($"Could not rename to \"{dlg.SetName}\".");
            return;
        }

        SetStatus(null);
        PopulateContainers();
    }

    private async void OnDeleteSetClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set)
        {
            SetStatus("Select a set first.");
            return;
        }

        if (set.Kind == FavoriteSetKind.Global)
        {
            SetStatus("The Global set cannot be deleted.");
            return;
        }

        var box = MessageBoxManager.GetMessageBoxStandard(
            "Delete set?",
            $"Delete \"{set.DisplayName}\"? Its favorites are kept — any not in another set move to \"Not in any set\".",
            ButtonEnum.YesNo
        );
        var result = await box.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        _store.DeleteSet(set.Id);
        _preferences.SetFavoriteSetLoaded(set.Id, loaded: false);
        _selectedContainerId = _store.GlobalSet.Id;
        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        ReorderSelection(static (list, indices) => FavoriteSetEditorModel.MoveUp(list, indices));
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        ReorderSelection(static (list, indices) => FavoriteSetEditorModel.MoveDown(list, indices));
    }

    private void ReorderSelection(Func<List<string>, IReadOnlyCollection<int>, List<int>> reorder)
    {
        if (SelectedSet is not { } set)
        {
            SetStatus("Favorites in \"Not in any set\" have no order — add them to a set first.");
            return;
        }

        var indices = GetSelectedFavoriteIndices();
        if (indices.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var ids = set.FavoriteIds.ToList();
        var moved = reorder(ids, indices);
        _store.ReplaceSetFavorites(set.Id, ids);
        SetStatus(null);
        PopulateFavorites();
        ReselectFavorites(moved);
    }

    private void ShowTransferFlyout(object? sender, bool move)
    {
        if (sender is not Button btn)
        {
            return;
        }

        var selected = GetSelectedFavorites();
        if (selected.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var set in _store.OrderedSets)
        {
            if (string.Equals(set.Id, _selectedContainerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetId = set.Id;
            var item = new MenuItem { Header = set.DisplayName };
            item.Click += (_, _) => TransferSelection(targetId, move);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var newSetItem = new MenuItem { Header = "New set…" };
        newSetItem.Click += async (_, _) => await TransferToNewSetAsync(move);
        flyout.Items.Add(newSetItem);

        flyout.ShowAt(btn);
    }

    private async Task TransferToNewSetAsync(bool move)
    {
        var dlg = new FavoriteSetNameDialog(NamedSetNames, null) { Title = "New Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null)
        {
            return;
        }

        var set = _store.CreateNamedSet(dlg.SetName);
        if (set is null)
        {
            SetStatus($"Could not create set \"{dlg.SetName}\".");
            return;
        }

        TransferSelection(set.Id, move);
    }

    private void TransferSelection(string targetSetId, bool move)
    {
        var selected = GetSelectedFavorites();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var favorite in selected)
        {
            _store.AddToSet(targetSetId, favorite.Id);
            if (move && SelectedSet is { } source)
            {
                _store.RemoveFromSet(source.Id, favorite.Id);
            }
        }

        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private void OnRemoveFromSetClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set)
        {
            SetStatus("Favorites in \"Not in any set\" belong to no set to remove them from.");
            return;
        }

        var selected = GetSelectedFavorites();
        if (selected.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        foreach (var favorite in selected)
        {
            _store.RemoveFromSet(set.Id, favorite.Id);
        }

        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private async void OnDeleteFavoritesClick(object? sender, RoutedEventArgs e)
    {
        var selected = GetSelectedFavorites();
        if (selected.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var box = MessageBoxManager.GetMessageBoxStandard(
            "Delete favorites?",
            $"Delete {selected.Count} favorite(s) everywhere, from every set?",
            ButtonEnum.YesNo
        );
        var result = await box.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        foreach (var favorite in selected)
        {
            _store.DeleteFavorite(favorite.Id);
        }

        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }
}
