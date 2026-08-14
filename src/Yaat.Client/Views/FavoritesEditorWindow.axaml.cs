using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Two-pane manager for favorite sets: the left pane lists the containers (the always-present
/// base pool plus every named set, each with a Loaded checkbox), the right pane lists the
/// selected container's favorites with multi-select. Supports set create/rename/delete and
/// copying/moving/reordering/deleting favorites across containers. All mutations commit
/// straight to <see cref="UserPreferences"/>; the live bar/panel follow via FavoriteSetsChanged.
/// </summary>
public partial class FavoritesEditorWindow : Window
{
    private readonly UserPreferences _preferences;
    private readonly WindowGeometryHelper _geometryHelper;

    /// <summary>Set name of the selected container; null = the base pool.</summary>
    private string? _selectedSetName;

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public FavoritesEditorWindow()
        : this(new UserPreferences()) { }

    public FavoritesEditorWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _preferences = preferences;
        _geometryHelper = new WindowGeometryHelper(this, preferences, "FavoritesEditor", 760, 480);
        _geometryHelper.Restore();

        WireButton("NewSetButton", OnNewSetClick);
        WireButton("RenameSetButton", OnRenameSetClick);
        WireButton("DeleteSetButton", OnDeleteSetClick);
        WireButton("MoveUpButton", OnMoveUpClick);
        WireButton("MoveDownButton", OnMoveDownClick);
        WireButton("CopyToButton", (s, e) => ShowTransferFlyout(s, copy: true));
        WireButton("MoveToButton", (s, e) => ShowTransferFlyout(s, copy: false));
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

        var items = new List<ListBoxItem>
        {
            new()
            {
                Content = new TextBlock { Text = "Favorites (main)", VerticalAlignment = VerticalAlignment.Center },
                Tag = null,
            },
        };

        foreach (var set in _preferences.FavoriteCommandSets)
        {
            var setName = set.Name;
            var loadedBox = new CheckBox
            {
                IsChecked = _preferences.LoadedFavoriteSetNames.Any(n => string.Equals(n, setName, StringComparison.OrdinalIgnoreCase)),
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(loadedBox, "Loaded: show this set's favorites in the bar and panel");
            loadedBox.IsCheckedChanged += (_, _) => _preferences.SetFavoriteSetLoaded(setName, loadedBox.IsChecked == true);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(loadedBox);
            row.Children.Add(new TextBlock { Text = setName, VerticalAlignment = VerticalAlignment.Center });
            items.Add(new ListBoxItem { Content = row, Tag = setName });
        }

        list.SelectionChanged -= OnContainerSelectionChanged;
        list.ItemsSource = items;
        var selected = items.FirstOrDefault(i => string.Equals(i.Tag as string, _selectedSetName, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        list.SelectedItem = selected;
        _selectedSetName = selected.Tag as string;
        list.SelectionChanged += OnContainerSelectionChanged;
    }

    private void OnContainerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var list = this.FindControl<ListBox>("ContainersList");
        if (list?.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        _selectedSetName = item.Tag as string;
        SetStatus(null);
        PopulateFavorites();
    }

    private void PopulateFavorites()
    {
        var list = this.FindControl<ListBox>("FavoritesList");
        if (list is null)
        {
            return;
        }

        list.ItemsSource = GetSelectedContainerFavorites().Select(DescribeFavorite).ToList();
    }

    private IReadOnlyList<FavoriteCommand> GetSelectedContainerFavorites()
    {
        if (_selectedSetName is null)
        {
            return _preferences.FavoriteCommands;
        }

        var set = _preferences.FavoriteCommandSets.FirstOrDefault(s => string.Equals(s.Name, _selectedSetName, StringComparison.OrdinalIgnoreCase));
        return set?.Favorites ?? [];
    }

    private static string DescribeFavorite(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return $"(blank) — {MainViewModel.NormalizeFavoriteCategory(favorite.Category)}";
        }

        var scope = "Global";
        if (!string.IsNullOrWhiteSpace(favorite.ScenarioId))
        {
            scope = "Scenario";
        }
        else if (!string.IsNullOrWhiteSpace(favorite.AirportId))
        {
            scope = $"Airport {MainViewModel.NormalizeFavoriteAirportId(favorite.AirportId)}";
        }

        return $"{favorite.Label} — {MainViewModel.NormalizeFavoriteCategory(favorite.Category)} — {scope}";
    }

    private List<int> GetSelectedFavoriteIndices()
    {
        var list = this.FindControl<ListBox>("FavoritesList");
        return list is null ? [] : list.Selection.SelectedIndexes.ToList();
    }

    private void CommitContainer(string? setName, List<FavoriteCommand> favorites)
    {
        if (setName is null)
        {
            _preferences.SetFavoriteCommands(favorites);
        }
        else
        {
            _preferences.SetFavoriteSetFavorites(setName, favorites);
        }
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

    private async void OnNewSetClick(object? sender, RoutedEventArgs e)
    {
        var dlg = new FavoriteSetNameDialog(_preferences.FavoriteCommandSets.Select(s => s.Name), null) { Title = "New Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null)
        {
            return;
        }

        if (!_preferences.CreateFavoriteSet(dlg.SetName))
        {
            SetStatus($"Could not create set \"{dlg.SetName}\".");
            return;
        }

        _selectedSetName = dlg.SetName;
        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private async void OnRenameSetClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedSetName is null)
        {
            SetStatus("Select a set first — the main favorites list cannot be renamed.");
            return;
        }

        var oldName = _selectedSetName;
        var others = _preferences
            .FavoriteCommandSets.Where(s => !string.Equals(s.Name, oldName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name);
        var dlg = new FavoriteSetNameDialog(others, oldName) { Title = "Rename Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null || string.Equals(dlg.SetName, oldName, StringComparison.Ordinal))
        {
            return;
        }

        if (!_preferences.RenameFavoriteSet(oldName, dlg.SetName))
        {
            SetStatus($"Could not rename to \"{dlg.SetName}\".");
            return;
        }

        _selectedSetName = dlg.SetName;
        SetStatus(null);
        PopulateContainers();
    }

    private async void OnDeleteSetClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedSetName is null)
        {
            SetStatus("Select a set first — the main favorites list cannot be deleted.");
            return;
        }

        var name = _selectedSetName;
        var box = MessageBoxManager.GetMessageBoxStandard(
            "Delete set?",
            $"Delete favorite set \"{name}\" and all favorites in it?",
            ButtonEnum.YesNo
        );
        var result = await box.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        _preferences.DeleteFavoriteSet(name);
        _selectedSetName = null;
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

    private void ReorderSelection(Func<List<FavoriteCommand>, IReadOnlyCollection<int>, List<int>> reorder)
    {
        var indices = GetSelectedFavoriteIndices();
        if (indices.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var favorites = GetSelectedContainerFavorites().ToList();
        var moved = reorder(favorites, indices);
        CommitContainer(_selectedSetName, favorites);
        SetStatus(null);
        PopulateFavorites();
        ReselectFavorites(moved);
    }

    private void ShowTransferFlyout(object? sender, bool copy)
    {
        if (sender is not Button btn)
        {
            return;
        }

        var indices = GetSelectedFavoriteIndices();
        if (indices.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var flyout = new MenuFlyout();
        if (_selectedSetName is not null)
        {
            var mainItem = new MenuItem { Header = "Favorites (main)" };
            mainItem.Click += (_, _) => TransferSelection(targetSetName: null, copy);
            flyout.Items.Add(mainItem);
        }

        foreach (var set in _preferences.FavoriteCommandSets)
        {
            if (string.Equals(set.Name, _selectedSetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var setName = set.Name;
            var item = new MenuItem { Header = setName };
            item.Click += (_, _) => TransferSelection(setName, copy);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var newSetItem = new MenuItem { Header = "New set…" };
        newSetItem.Click += async (_, _) => await TransferToNewSetAsync(copy);
        flyout.Items.Add(newSetItem);

        flyout.ShowAt(btn);
    }

    private async Task TransferToNewSetAsync(bool copy)
    {
        var dlg = new FavoriteSetNameDialog(_preferences.FavoriteCommandSets.Select(s => s.Name), null) { Title = "New Favorite Set" };
        await dlg.ShowDialog(this);
        if (dlg.SetName is null)
        {
            return;
        }

        if (!_preferences.CreateFavoriteSet(dlg.SetName))
        {
            SetStatus($"Could not create set \"{dlg.SetName}\".");
            return;
        }

        TransferSelection(dlg.SetName, copy);
    }

    private void TransferSelection(string? targetSetName, bool copy)
    {
        var indices = GetSelectedFavoriteIndices();
        if (indices.Count == 0)
        {
            return;
        }

        var source = GetSelectedContainerFavorites().ToList();
        var targetSet = _preferences.FavoriteCommandSets.FirstOrDefault(s =>
            string.Equals(s.Name, targetSetName, StringComparison.OrdinalIgnoreCase)
        );
        var target = targetSetName is null ? _preferences.FavoriteCommands.ToList() : (targetSet?.Favorites ?? []).ToList();

        if (copy)
        {
            FavoriteSetEditorModel.CopyTo(source, target, indices);
        }
        else
        {
            FavoriteSetEditorModel.MoveTo(source, target, indices);
            CommitContainer(_selectedSetName, source);
        }

        CommitContainer(targetSetName, target);
        SetStatus(null);
        PopulateContainers();
        PopulateFavorites();
    }

    private async void OnDeleteFavoritesClick(object? sender, RoutedEventArgs e)
    {
        var indices = GetSelectedFavoriteIndices();
        if (indices.Count == 0)
        {
            SetStatus("Select one or more favorites first.");
            return;
        }

        var box = MessageBoxManager.GetMessageBoxStandard("Delete favorites?", $"Delete {indices.Count} favorite(s)?", ButtonEnum.YesNo);
        var result = await box.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        var favorites = GetSelectedContainerFavorites().ToList();
        foreach (var index in indices.Distinct().OrderDescending())
        {
            if (index >= 0 && index < favorites.Count)
            {
                favorites.RemoveAt(index);
            }
        }

        CommitContainer(_selectedSetName, favorites);
        SetStatus(null);
        PopulateFavorites();
    }
}
