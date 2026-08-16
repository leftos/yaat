using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class FavoritesBarView : UserControl
{
    public static readonly StyledProperty<bool> IsPaletteModeProperty = AvaloniaProperty.Register<FavoritesBarView, bool>(nameof(IsPaletteMode));

    private static readonly FavoriteCommandCategory[] PaletteCategories =
    [
        FavoriteCommandCategory.Air,
        FavoriteCommandCategory.Ground,
        FavoriteCommandCategory.Vehicle,
        FavoriteCommandCategory.Airport,
    ];

    private static readonly FilePickerFilter FavoritesZipType = new("YAAT Favorites", ["*.yaat-favset.zip", "*.yaat-favlibrary.zip"]);

    private static readonly FilePickerFilter SetZipType = new("YAAT Favorite Set", ["*.yaat-favset.zip"]);

    private static readonly FilePickerFilter LibraryZipType = new("YAAT Favorites Library", ["*.yaat-favlibrary.zip"]);

    private static readonly FilePickerFilter JsonFileType = new("JSON Files", ["*.json"]);

    private static readonly ILogger Log = AppLog.CreateLogger<FavoritesBarView>();

    private MainViewModel? _boundVm;
    private WrapPanel? _panel;
    private TabControl? _tabControl;
    private Button? _addButton;
    private NumericUpDown? _columnsBox;
    private FavoriteCommandCategory _selectedPaletteCategory = FavoriteCommandCategory.Air;
    private FavoriteDisplayEntry? _pendingDragFavorite;
    private FavoriteDisplayEntry? _activeDragFavorite;
    private FavoriteDisplayEntry? _lastDragTarget;
    private IPointer? _capturedPointer;
    private Point _dragStartPoint;
    private DateTime _dragPressUtc;
    private DateTime _lastDragReorderUtc = DateTime.MinValue;
    private bool _suppressNextFavoriteClick;

    private static readonly TimeSpan DragHoldDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DragReorderDebounce = TimeSpan.FromMilliseconds(175);
    private const double DragStartDistance = 6;

    public bool IsPaletteMode
    {
        get => GetValue(IsPaletteModeProperty);
        set => SetValue(IsPaletteModeProperty, value);
    }

    public FavoritesBarView()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, OnFavoritePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnFavoritePointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnFavoritePointerCaptureLost, RoutingStrategies.Tunnel);
        RebuildRoot();
    }

    static FavoritesBarView()
    {
        IsPaletteModeProperty.Changed.AddClassHandler<FavoritesBarView>((view, _) => view.RebuildRoot());
    }

    public void OpenAddFlyoutForCommand(string commandText)
    {
        if (_addButton is null)
        {
            return;
        }

        ShowAddFlyout(_addButton, commandText, GetActiveAddCategory());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_boundVm is not null)
        {
            _boundVm.DisplayFavorites.CollectionChanged -= OnFavoritesChanged;
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }

        if (DataContext is MainViewModel vm)
        {
            _boundVm = vm;
            vm.DisplayFavorites.CollectionChanged += OnFavoritesChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            if (_columnsBox is not null)
            {
                _columnsBox.Value = vm.Preferences.FavoritePanelColumns;
            }
            RebuildButtons();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_boundVm is not null)
        {
            _boundVm.DisplayFavorites.CollectionChanged -= OnFavoritesChanged;
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }
    }

    private void OnFavoritesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildButtons();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAircraft))
        {
            RebuildButtons();
        }
    }

    private void RebuildRoot()
    {
        _panel = null;
        _tabControl = null;
        _addButton = null;
        _columnsBox = null;

        if (IsPaletteMode)
        {
            Content = CreatePaletteRoot();
        }
        else
        {
            _panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            Content = _panel;
        }

        RebuildButtons();
    }

    private Control CreatePaletteRoot()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        DockPanel.SetDock(header, Dock.Top);

        var title = new TextBlock
        {
            Text = "Favorites",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);

        var columnsLabel = new TextBlock
        {
            Text = "Cols",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
            Margin = new Thickness(12, 0, 4, 0),
        };
        DockPanel.SetDock(columnsLabel, Dock.Right);
        header.Children.Add(columnsLabel);

        _columnsBox = new NumericUpDown
        {
            Value = DataContext is MainViewModel vm ? vm.Preferences.FavoritePanelColumns : 6,
            Minimum = 1,
            Maximum = 20,
            Increment = 1,
            FormatString = "0",
            // The Fluent spinner's two RepeatButtons alone need 68px; anything narrower hides the
            // value text and clips the decrease button outside the border (issue #359).
            Width = 100,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _columnsBox.ValueChanged += OnPanelColumnsChanged;
        DockPanel.SetDock(_columnsBox, Dock.Right);
        header.Children.Add(_columnsBox);

        var blankButton = CreateAddBlankButton();
        blankButton.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(blankButton, Dock.Right);
        header.Children.Add(blankButton);

        var batchButton = CreateBatchButton();
        batchButton.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(batchButton, Dock.Right);
        header.Children.Add(batchButton);

        var addButton = CreateAddButton();
        addButton.Content = "Add";
        addButton.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(addButton, Dock.Right);
        header.Children.Add(addButton);

        var exportButton = CreateExportButton();
        DockPanel.SetDock(exportButton, Dock.Right);
        header.Children.Add(exportButton);

        var importButton = CreateImportButton();
        DockPanel.SetDock(importButton, Dock.Right);
        header.Children.Add(importButton);

        var setsButton = CreateSetsButton();
        DockPanel.SetDock(setsButton, Dock.Right);
        header.Children.Add(setsButton);

        root.Children.Add(header);

        _tabControl = new TabControl();
        _tabControl.SelectionChanged += OnPaletteTabChanged;
        root.Children.Add(_tabControl);

        return root;
    }

    private void RebuildButtons()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (IsPaletteMode)
        {
            RebuildPaletteButtons(vm);
            return;
        }

        if (_panel is null)
        {
            return;
        }

        _panel.Children.Clear();

        foreach (var entry in vm.DisplayFavorites.Where(f => !f.Favorite.IsSpacer))
        {
            var btn = CreateFavoriteButton(entry);
            _panel.Children.Add(btn);
        }

        if (vm.NamedFavoriteSets.Count > 0)
        {
            var setsButton = CreateSetsButton();
            setsButton.Margin = new Thickness(0, 0, 4, 4);
            _panel.Children.Add(setsButton);
        }

        _panel.Children.Add(CreateOpenPanelButton());
        _panel.Children.Add(CreateAddButton());
    }

    private void RebuildPaletteButtons(MainViewModel vm)
    {
        if (_tabControl is null)
        {
            return;
        }

        _tabControl.Items.Clear();

        foreach (var category in PaletteCategories)
        {
            var panel = new UniformGrid { Columns = GetPanelColumns(vm), Margin = new Thickness(8) };

            foreach (var entry in vm.DisplayFavorites.Where(f => NormalizeCategory(f.Favorite) == category))
            {
                panel.Children.Add(entry.Favorite.IsSpacer ? CreateBlankSlot(entry) : CreateFavoriteButton(entry));
            }

            var scroll = new ScrollViewer
            {
                Content = panel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var tab = new TabItem
            {
                Header = category.ToString(),
                Content = scroll,
                IsSelected = category == _selectedPaletteCategory,
            };
            _tabControl.Items.Add(tab);
        }
    }

    private Button CreateFavoriteButton(FavoriteDisplayEntry entry)
    {
        var fav = entry.Favorite;
        var width = GetButtonWidth(fav);
        var height = GetButtonHeight(fav);
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = fav.Label,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Tag = entry,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = IsPaletteMode ? new Thickness(6, 2) : new Thickness(8, 2),
            FontSize = IsPaletteMode ? 13 : 12,
            FontWeight = FontWeight.SemiBold,
            Width = IsPaletteMode ? width : double.NaN,
            Height = IsPaletteMode ? height : double.NaN,
            MinWidth = IsPaletteMode ? width : 0,
            MinHeight = IsPaletteMode ? height : 0,
            Background = ParseBrush(fav.BackgroundColor, FavoriteCommandDefaults.BackgroundColor),
            Foreground = ParseBrush(fav.TextColor, FavoriteCommandDefaults.TextColor),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(btn, BuildFavoriteToolTip(fav));

        btn.AddHandler(PointerPressedEvent, OnFavoritePointerPressed, RoutingStrategies.Tunnel);
        btn.Click += OnFavoriteClick;

        return btn;
    }

    private Button CreateBlankSlot(FavoriteDisplayEntry entry)
    {
        var fav = entry.Favorite;
        var width = GetButtonWidth(fav);
        var height = GetButtonHeight(fav);
        var btn = new Button
        {
            Tag = entry,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(0),
            Width = width,
            Height = height,
            MinWidth = width,
            MinHeight = height,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        ToolTip.SetTip(btn, "Blank slot\nRight-click: edit or delete");
        btn.AddHandler(PointerPressedEvent, OnFavoritePointerPressed, RoutingStrategies.Tunnel);
        btn.Click += OnFavoriteClick;
        return btn;
    }

    private Button CreateOpenPanelButton()
    {
        var btn = new Button
        {
            Content = "Panel",
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Open the favorites panel");
        btn.Click += OnOpenPanelClick;
        return btn;
    }

    private Button CreateAddButton()
    {
        var btn = new Button
        {
            Content = "+",
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 2),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
        };

        ToolTip.SetTip(btn, "Add a favorite command");
        btn.Click += OnAddClick;

        _addButton = btn;
        return btn;
    }

    private Button CreateAddBlankButton()
    {
        var btn = new Button
        {
            Content = "Blank",
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Add a blank slot to the active category");
        btn.Click += OnAddBlankClick;
        return btn;
    }

    private Button CreateBatchButton()
    {
        var btn = new Button
        {
            Content = "Batch",
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Add multiple blank slots to the active category");
        btn.Click += OnBatchClick;
        return btn;
    }

    private Button CreateSetsButton()
    {
        var btn = new Button
        {
            Content = "Sets",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Load or unload favorite sets, or open the sets editor");
        btn.Click += OnSetsClick;
        return btn;
    }

    private void OnSetsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainViewModel vm)
        {
            return;
        }

        // Built fresh on every open so the checkboxes always reflect the current loaded state —
        // no persistent subscription (and no repopulation reentrancy) needed.
        var flyout = new MenuFlyout();
        var namedSets = vm.NamedFavoriteSets;
        foreach (var set in namedSets)
        {
            var setId = set.Id;
            var item = new MenuItem
            {
                Header = set.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = vm.IsFavoriteSetLoaded(setId),
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                var nowLoaded = !vm.IsFavoriteSetLoaded(setId);
                item.IsChecked = nowLoaded;
                vm.SetFavoriteSetLoaded(setId, nowLoaded);
            };
            flyout.Items.Add(item);
        }

        if (namedSets.Count > 0)
        {
            flyout.Items.Add(new Separator());
        }

        var manage = new MenuItem { Header = "Manage sets…" };
        manage.Click += (_, _) => OpenFavoritesEditor(vm);
        flyout.Items.Add(manage);
        flyout.ShowAt(btn);
    }

    private void OpenFavoritesEditor(MainViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new FavoritesEditorWindow(vm.Preferences, vm.FavoriteStore);
        _ = editor.ShowDialog(owner);
    }

    private Button CreateImportButton()
    {
        var btn = new Button
        {
            Content = "Import",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Import favorites from a shared file");
        btn.Click += OnImportFavoritesClick;
        return btn;
    }

    private Button CreateExportButton()
    {
        var btn = new Button
        {
            Content = "Export",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Export favorites to a file to share");
        btn.Click += OnExportClick;
        return btn;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainViewModel vm)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var set in vm.FavoriteStore.OrderedSets)
        {
            var setId = set.Id;
            var displayName = set.DisplayName;
            var item = new MenuItem { Header = $"Set: {displayName}" };
            item.Click += (_, _) => _ = ExportSetAsync(vm, setId, displayName);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var libraryItem = new MenuItem { Header = "Everything (library)" };
        libraryItem.Click += (_, _) => _ = ExportLibraryAsync(vm);
        flyout.Items.Add(libraryItem);

        flyout.ShowAt(btn);
    }

    private async Task ExportSetAsync(MainViewModel vm, string setId, string displayName)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var picker = new AvaloniaFilePickerService(owner);
        var path = await picker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Export Favorite Set",
                SuggestedFileName: $"{FavoriteStore.SanitizeFileName(displayName, "set")}{FavoriteExport.SetExportExtension}",
                Filters: [SetZipType],
                DefaultExtension: "yaat-favset.zip"
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.Create(path);
            vm.ExportFavoriteSet(setId, stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Favorite set export failed to write {Path}", path);
        }
    }

    private async Task ExportLibraryAsync(MainViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var picker = new AvaloniaFilePickerService(owner);
        var path = await picker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Export Favorites Library",
                SuggestedFileName: $"favorites{FavoriteExport.LibraryExportExtension}",
                Filters: [LibraryZipType],
                DefaultExtension: "yaat-favlibrary.zip"
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.Create(path);
            vm.ExportFavoriteLibrary(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Favorites library export failed to write {Path}", path);
        }
    }

    private async void OnImportFavoritesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var picker = new AvaloniaFilePickerService(owner);
        var path = await picker.OpenFileAsync(new OpenFileOptions("Import Favorites", [FavoritesZipType, JsonFileType]));
        if (path is null)
        {
            return;
        }

        FavoriteImportResult? result;
        try
        {
            await using var stream = File.OpenRead(path);
            result = vm.ImportFavoritesFile(Path.GetFileName(path), stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Favorites import failed to read {Path}", path);
            return;
        }

        if (result is null)
        {
            Log.LogWarning("Favorites import did not recognize {Path} as a favorites zip or entity json", path);
            await MessageBoxManager
                .GetMessageBoxStandard("Import Favorites", "The selected file is not a recognized favorites export.")
                .ShowWindowDialogAsync(owner);
            return;
        }

        var summary =
            $"Imported {result.FavoritesAdded} new favorite(s), updated {result.FavoritesUpdated}; "
            + $"added {result.SetsAdded} set(s), merged into {result.SetsUpdated}.";
        if (result.MissingReferences > 0)
        {
            summary += $" {result.MissingReferences} referenced favorite(s) were missing from the file and were skipped.";
        }
        await MessageBoxManager.GetMessageBoxStandard("Import Favorites", summary).ShowWindowDialogAsync(owner);
    }

    private void OnFavoritePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FavoriteDisplayEntry entry)
        {
            return;
        }

        var props = e.GetCurrentPoint(btn).Properties;

        // Right-click → edit flyout
        if (props.IsRightButtonPressed)
        {
            ClearFavoriteDragState();
            e.Handled = true;
            if (entry.Favorite.IsSpacer)
            {
                ShowEditBlankFlyout(btn, entry);
            }
            else
            {
                ShowEditFlyout(btn, entry);
            }
            return;
        }

        // Ctrl+Left-click → append to input
        if (!entry.Favorite.IsSpacer && props.IsLeftButtonPressed && PlatformHelper.HasActionModifier(e.KeyModifiers))
        {
            ClearFavoriteDragState();
            e.Handled = true;
            if (DataContext is MainViewModel vm)
            {
                vm.AppendFavoriteToInput(entry.Favorite);
            }
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            // Record a *pending* drag but do NOT capture the pointer yet. Capturing on every press
            // stole the click from the underlying Button (the CaptureLost cascade released the
            // button's own capture), so plain clicks did nothing (#287). Capture is deferred to the
            // moment a drag actually activates in OnFavoritePointerMoved.
            _pendingDragFavorite = entry;
            _activeDragFavorite = null;
            _lastDragTarget = null;
            _dragStartPoint = e.GetPosition(this);
            _dragPressUtc = DateTime.UtcNow;
            _lastDragReorderUtc = DateTime.MinValue;
        }
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FavoriteDisplayEntry entry)
        {
            return;
        }

        if (_suppressNextFavoriteClick)
        {
            _suppressNextFavoriteClick = false;
            return;
        }

        if (!entry.Favorite.IsSpacer && DataContext is MainViewModel vm)
        {
            _ = vm.ExecuteFavoriteAsync(entry.Favorite);
        }
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
        {
            return;
        }

        ShowAddFlyout(btn, string.Empty, GetActiveAddCategory());
    }

    private void OnAddBlankClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.AddFavorite(
            CreateBlankFavorite(GetActiveAddCategory(), FavoriteCommandDefaults.ButtonWidth, FavoriteCommandDefaults.ButtonHeight),
            [vm.FavoriteStore.GlobalSet.Id]
        );
    }

    private void OnBatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
        {
            return;
        }

        ShowBatchFlyout(btn, GetActiveAddCategory());
    }

    private void OnOpenPanelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        FavoritesPanelWindow.ShowOrActivate(vm);
    }

    private void OnPaletteTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabControl?.SelectedItem is TabItem { Header: string header } && Enum.TryParse<FavoriteCommandCategory>(header, out var category))
        {
            _selectedPaletteCategory = category;
        }
    }

    private void OnPanelColumnsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!IsPaletteMode || DataContext is not MainViewModel vm || sender is not NumericUpDown box)
        {
            return;
        }

        var columns = Math.Clamp((int)Math.Round((double)(box.Value ?? 6)), 1, 20);
        if (columns == vm.Preferences.FavoritePanelColumns)
        {
            return;
        }

        vm.Preferences.SetFavoritePanelColumns(columns);
        RebuildButtons();
    }

    private void OnFavoritePointerMoved(object? sender, PointerEventArgs e)
    {
        var dragged = _activeDragFavorite ?? _pendingDragFavorite;
        if (dragged is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        var pointer = e.GetCurrentPoint(this);
        if (!pointer.Properties.IsLeftButtonPressed)
        {
            EndFavoriteDrag();
            return;
        }

        var now = DateTime.UtcNow;
        var currentPoint = e.GetPosition(this);
        if (_activeDragFavorite is null)
        {
            var distance = currentPoint - _dragStartPoint;
            if (now - _dragPressUtc < DragHoldDelay || Math.Abs(distance.X) < DragStartDistance && Math.Abs(distance.Y) < DragStartDistance)
            {
                return;
            }

            _activeDragFavorite = dragged;
            _suppressNextFavoriteClick = true;

            // Now that this is a real drag (past the hold delay + move threshold), capture the
            // pointer so reorder moves keep flowing even if it leaves a button. The click is already
            // suppressed above, so stealing the button's capture here no longer loses a click.
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        if (now - _lastDragReorderUtc < DragReorderDebounce)
        {
            e.Handled = true;
            return;
        }

        var target = FindFavoriteAt(currentPoint);
        if (target is null || target == dragged || target == _lastDragTarget)
        {
            e.Handled = true;
            return;
        }

        var reorderContext = GetReorderContext(vm, dragged).ToList();
        var draggedIndex = reorderContext.IndexOf(dragged);
        var targetIndex = reorderContext.IndexOf(target);
        if (draggedIndex < 0 || targetIndex < 0)
        {
            e.Handled = true;
            return;
        }

        if (targetIndex < draggedIndex)
        {
            vm.MoveFavoriteBefore(dragged, target);
        }
        else
        {
            vm.MoveFavoriteAfter(dragged, target);
        }

        _lastDragTarget = target;
        _lastDragReorderUtc = now;
        e.Handled = true;
    }

    private void OnFavoritePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndFavoriteDrag();
    }

    private void OnFavoritePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndFavoriteDrag();
    }

    private FavoriteDisplayEntry? FindFavoriteAt(Point point)
    {
        return this.GetVisualsAt(point).OfType<Button>().Select(button => button.Tag).OfType<FavoriteDisplayEntry>().FirstOrDefault();
    }

    private void EndFavoriteDrag()
    {
        if (_activeDragFavorite is not null)
        {
            _suppressNextFavoriteClick = true;
        }

        ClearFavoriteDragState();
    }

    private void ClearFavoriteDragState()
    {
        _pendingDragFavorite = null;
        _activeDragFavorite = null;
        _lastDragTarget = null;
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
    }

    private void ShowAddFlyout(Button target, string prefillCommand, FavoriteCommandCategory category)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

        var labelBox = new TextBox
        {
            PlaceholderText = "Label (e.g. FH 270)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var commandBox = new TextBox
        {
            Text = prefillCommand,
            PlaceholderText = "Command text (e.g. FH 270, CM 014)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var groundCommandBox = new TextBox
        {
            PlaceholderText = "Ground command override (optional)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var categoryBox = CreateCategoryBox(category);
        var backgroundPicker = CreateColorPicker(FavoriteCommandDefaults.BackgroundColor);
        var textPicker = CreateColorPicker(FavoriteCommandDefaults.TextColor);
        var widthBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonWidth, 70, 240);
        var heightBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonHeight, 24, 72);
        var containerPicker = new FavoriteContainerPicker(vm, checkedSetIds: [vm.FavoriteStore.GlobalSet.Id]);

        var saveBtn = new Button { Content = "Add", Margin = new Thickness(0, 4, 0, 0) };
        BindSaveToContainers(saveBtn, containerPicker);

        var panel = CreateEditorPanel(
            "Add Favorite",
            labelBox,
            commandBox,
            groundCommandBox,
            categoryBox,
            backgroundPicker,
            textPicker,
            widthBox,
            heightBox,
            containerPicker,
            saveBtn
        );

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };

        saveBtn.Click += (_, _) =>
        {
            var label = labelBox.Text?.Trim() ?? "";
            var cmdText = commandBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(cmdText))
            {
                return;
            }

            var fav = new FavoriteCommand
            {
                Id = vm.FavoriteStore.NewFavoriteId(),
                Label = label,
                CommandText = cmdText,
                GroundCommandText = groundCommandBox.Text?.Trim() ?? "",
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : category,
                BackgroundColor = ToHex(backgroundPicker.Color),
                TextColor = ToHex(textPicker.Color),
                ButtonWidth = GetDimensionValue(widthBox, FavoriteCommandDefaults.ButtonWidth),
                ButtonHeight = GetDimensionValue(heightBox, FavoriteCommandDefaults.ButtonHeight),
            };

            vm.AddFavorite(fav, containerPicker.ResolveSelectedSetIds(vm));
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }

    private void ShowEditFlyout(Button target, FavoriteDisplayEntry entry)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

        var fav = entry.Favorite;
        var labelBox = new TextBox
        {
            Text = fav.Label,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var commandBox = new TextBox
        {
            Text = fav.CommandText,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var groundCommandBox = new TextBox
        {
            Text = fav.GroundCommandText,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var categoryBox = CreateCategoryBox(NormalizeCategory(fav));
        var backgroundPicker = CreateColorPicker(GetFavoriteBackgroundColor(fav));
        var textPicker = CreateColorPicker(GetFavoriteTextColor(fav));
        var widthBox = CreateDimensionBox(GetButtonWidth(fav), 70, 240);
        var heightBox = CreateDimensionBox(GetButtonHeight(fav), 24, 72);
        var containerPicker = new FavoriteContainerPicker(vm, vm.GetFavoriteMembership(fav.Id));

        var deleteBtn = CreateDeleteButton();
        var saveBtn = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        BindSaveToContainers(saveBtn, containerPicker);
        var moveLeftBtn = CreateSmallActionButton("Move Left");
        var moveRightBtn = CreateSmallActionButton("Move Right");
        var insertBeforeBtn = CreateInsertBlankButton("Blank Before");
        var insertAfterBtn = CreateInsertBlankButton("Blank After");
        var footer = CreateEditFooter(deleteBtn, saveBtn, moveLeftBtn, moveRightBtn, insertBeforeBtn, insertAfterBtn);

        var panel = CreateEditorPanel(
            "Edit Favorite",
            labelBox,
            commandBox,
            groundCommandBox,
            categoryBox,
            backgroundPicker,
            textPicker,
            widthBox,
            heightBox,
            containerPicker,
            footer
        );

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };

        saveBtn.Click += (_, _) =>
        {
            var label = labelBox.Text?.Trim() ?? "";
            var cmdText = commandBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(cmdText))
            {
                return;
            }

            var updated = new FavoriteCommand
            {
                Id = fav.Id,
                Label = label,
                CommandText = cmdText,
                GroundCommandText = groundCommandBox.Text?.Trim() ?? "",
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : NormalizeCategory(fav),
                BackgroundColor = ToHex(backgroundPicker.Color),
                TextColor = ToHex(textPicker.Color),
                ButtonWidth = GetDimensionValue(widthBox, GetButtonWidth(fav)),
                ButtonHeight = GetDimensionValue(heightBox, GetButtonHeight(fav)),
            };

            vm.UpdateFavorite(updated, containerPicker.ResolveSelectedSetIds(vm));
            flyout.Hide();
        };

        deleteBtn.Click += (_, _) =>
        {
            vm.DeleteFavorite(fav.Id);
            flyout.Hide();
        };

        insertBeforeBtn.Click += (_, _) =>
        {
            vm.InsertBlankBefore(entry, CreateBlankFavorite(NormalizeCategory(fav), GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        insertAfterBtn.Click += (_, _) =>
        {
            vm.InsertBlankAfter(entry, CreateBlankFavorite(NormalizeCategory(fav), GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        moveLeftBtn.Click += (_, _) => MoveFavorite(entry, direction: -1, flyout);
        moveRightBtn.Click += (_, _) => MoveFavorite(entry, direction: 1, flyout);

        flyout.ShowAt(target);
    }

    private void ShowEditBlankFlyout(Button target, FavoriteDisplayEntry entry)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

        var fav = entry.Favorite;
        var labelBox = new TextBox
        {
            PlaceholderText = "Label (leave blank for spacer)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var commandBox = new TextBox
        {
            PlaceholderText = "Command text",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var groundCommandBox = new TextBox
        {
            PlaceholderText = "Ground command override (optional)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var categoryBox = CreateCategoryBox(NormalizeCategory(fav));
        var backgroundPicker = CreateColorPicker(FavoriteCommandDefaults.BackgroundColor);
        var textPicker = CreateColorPicker(FavoriteCommandDefaults.TextColor);
        var widthBox = CreateDimensionBox(GetButtonWidth(fav), 70, 240);
        var heightBox = CreateDimensionBox(GetButtonHeight(fav), 24, 72);
        var containerPicker = new FavoriteContainerPicker(vm, vm.GetFavoriteMembership(fav.Id));
        var deleteBtn = CreateDeleteButton();
        var saveBtn = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        BindSaveToContainers(saveBtn, containerPicker);
        var moveLeftBtn = CreateSmallActionButton("Move Left");
        var moveRightBtn = CreateSmallActionButton("Move Right");
        var insertBeforeBtn = CreateInsertBlankButton("Blank Before");
        var insertAfterBtn = CreateInsertBlankButton("Blank After");
        var footer = CreateEditFooter(deleteBtn, saveBtn, moveLeftBtn, moveRightBtn, insertBeforeBtn, insertAfterBtn);

        var panel = CreateEditorPanel(
            "Edit Slot",
            labelBox,
            commandBox,
            groundCommandBox,
            categoryBox,
            backgroundPicker,
            textPicker,
            widthBox,
            heightBox,
            containerPicker,
            footer
        );

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };

        saveBtn.Click += (_, _) =>
        {
            var label = labelBox.Text?.Trim() ?? "";
            var cmdText = commandBox.Text?.Trim() ?? "";
            var isFavorite = !string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(cmdText);
            var updated = new FavoriteCommand
            {
                Id = fav.Id,
                IsSpacer = !isFavorite,
                Label = isFavorite ? label : "",
                CommandText = isFavorite ? cmdText : "",
                GroundCommandText = isFavorite ? groundCommandBox.Text?.Trim() ?? "" : "",
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : NormalizeCategory(fav),
                BackgroundColor = isFavorite ? ToHex(backgroundPicker.Color) : FavoriteCommandDefaults.BackgroundColor,
                TextColor = isFavorite ? ToHex(textPicker.Color) : FavoriteCommandDefaults.TextColor,
                ButtonWidth = GetDimensionValue(widthBox, GetButtonWidth(fav)),
                ButtonHeight = GetDimensionValue(heightBox, GetButtonHeight(fav)),
            };

            vm.UpdateFavorite(updated, containerPicker.ResolveSelectedSetIds(vm));
            flyout.Hide();
        };

        deleteBtn.Click += (_, _) =>
        {
            vm.DeleteFavorite(fav.Id);
            flyout.Hide();
        };

        insertBeforeBtn.Click += (_, _) =>
        {
            vm.InsertBlankBefore(entry, CreateBlankFavorite(NormalizeCategory(fav), GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        insertAfterBtn.Click += (_, _) =>
        {
            vm.InsertBlankAfter(entry, CreateBlankFavorite(NormalizeCategory(fav), GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        moveLeftBtn.Click += (_, _) => MoveFavorite(entry, direction: -1, flyout);
        moveRightBtn.Click += (_, _) => MoveFavorite(entry, direction: 1, flyout);

        flyout.ShowAt(target);
    }

    private void ShowBatchFlyout(Button target, FavoriteCommandCategory category)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

        var countBox = CreateDimensionBox(12, 1, 100);
        countBox.FormatString = "0";
        countBox.Increment = 1;
        var widthBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonWidth, 70, 240);
        var heightBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonHeight, 24, 72);
        var containerPicker = new FavoriteContainerPicker(vm, checkedSetIds: [vm.FavoriteStore.GlobalSet.Id]);
        var saveBtn = new Button { Content = "Add Blanks", Margin = new Thickness(0, 4, 0, 0) };
        BindSaveToContainers(saveBtn, containerPicker);

        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(
            new TextBlock
            {
                Text = "Batch Add Blank Slots",
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            }
        );
        panel.Children.Add(CreateLabeledControl("Slots", countBox));
        panel.Children.Add(CreateDimensionRow(widthBox, heightBox));
        panel.Children.Add(CreateLabeledControl("In", containerPicker.Control));
        panel.Children.Add(saveBtn);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };
        saveBtn.Click += (_, _) =>
        {
            var count = Math.Clamp((int)Math.Round(GetDimensionValue(countBox, 12)), 1, 100);
            var width = GetDimensionValue(widthBox, FavoriteCommandDefaults.ButtonWidth);
            var height = GetDimensionValue(heightBox, FavoriteCommandDefaults.ButtonHeight);
            var blanks = Enumerable.Range(0, count).Select(_ => CreateBlankFavorite(category, width, height)).ToList();

            vm.AddFavorites(blanks, containerPicker.ResolveSelectedSetIds(vm));
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }

    private FavoriteCommandCategory GetActiveAddCategory()
    {
        return IsPaletteMode ? _selectedPaletteCategory : FavoriteCommandCategory.Air;
    }

    private static int GetPanelColumns(MainViewModel vm)
    {
        return Math.Clamp(vm.Preferences.FavoritePanelColumns, 1, 20);
    }

    private static StackPanel CreateEditorPanel(
        string title,
        TextBox labelBox,
        TextBox commandBox,
        TextBox groundCommandBox,
        ComboBox categoryBox,
        ColorPicker backgroundPicker,
        ColorPicker textPicker,
        NumericUpDown widthBox,
        NumericUpDown heightBox,
        FavoriteContainerPicker containerPicker,
        Control footer
    )
    {
        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            }
        );
        panel.Children.Add(labelBox);
        panel.Children.Add(commandBox);
        panel.Children.Add(groundCommandBox);
        panel.Children.Add(CreateLabeledControl("Category", categoryBox));
        panel.Children.Add(CreateLabeledControl("Button color", backgroundPicker));
        panel.Children.Add(CreateLabeledControl("Text color", textPicker));
        panel.Children.Add(CreateDimensionRow(widthBox, heightBox));
        panel.Children.Add(CreateLabeledControl("In", containerPicker.Control));
        panel.Children.Add(footer);
        return panel;
    }

    /// <summary>Disables the save button whenever no container is checked.</summary>
    private static void BindSaveToContainers(Button saveBtn, FavoriteContainerPicker containerPicker)
    {
        saveBtn.IsEnabled = containerPicker.SelectedCount > 0;
        containerPicker.SelectionChanged += () => saveBtn.IsEnabled = containerPicker.SelectedCount > 0;
    }

    private void MoveFavorite(FavoriteDisplayEntry entry, int direction, Flyout flyout)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var orderedFavorites = GetReorderContext(vm, entry).ToList();
        var index = orderedFavorites.IndexOf(entry);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= orderedFavorites.Count)
        {
            return;
        }

        var target = orderedFavorites[targetIndex];
        if (direction < 0)
        {
            vm.MoveFavoriteBefore(entry, target);
        }
        else
        {
            vm.MoveFavoriteAfter(entry, target);
        }

        flyout.Hide();
    }

    private IEnumerable<FavoriteDisplayEntry> GetReorderContext(MainViewModel vm, FavoriteDisplayEntry entry)
    {
        return IsPaletteMode
            ? vm.DisplayFavorites.Where(f => NormalizeCategory(f.Favorite) == NormalizeCategory(entry.Favorite))
            : vm.DisplayFavorites.Where(f => !f.Favorite.IsSpacer);
    }

    private static Button CreateDeleteButton()
    {
        var btn = new Button
        {
            Content = "Delete",
            Foreground = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(btn, "Delete this favorite from every set");
        return btn;
    }

    private static Button CreateInsertBlankButton(string content)
    {
        return CreateSmallActionButton(content);
    }

    private static Button CreateSmallActionButton(string content)
    {
        return new Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(6, 2),
        };
    }

    private static StackPanel CreateEditFooter(
        Button deleteBtn,
        Button saveBtn,
        Button moveLeftBtn,
        Button moveRightBtn,
        Button insertBeforeBtn,
        Button insertAfterBtn
    )
    {
        var panel = new StackPanel();

        var moveRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
        };
        moveRow.Children.Add(moveLeftBtn);
        moveRow.Children.Add(moveRightBtn);
        panel.Children.Add(moveRow);

        var insertRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
        };
        insertRow.Children.Add(insertBeforeBtn);
        insertRow.Children.Add(insertAfterBtn);
        panel.Children.Add(insertRow);

        panel.Children.Add(CreateEditButtonRow(deleteBtn, saveBtn));
        return panel;
    }

    private static DockPanel CreateEditButtonRow(Button deleteBtn, Button saveBtn)
    {
        var buttonRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(deleteBtn, Dock.Left);
        buttonRow.Children.Add(deleteBtn);
        buttonRow.Children.Add(saveBtn);
        return buttonRow;
    }

    private static FavoriteCommand CreateBlankFavorite(FavoriteCommandCategory category, double width, double height)
    {
        return new FavoriteCommand
        {
            IsSpacer = true,
            Category = category,
            ButtonWidth = width,
            ButtonHeight = height,
        };
    }

    private static FavoriteCommandCategory NormalizeCategory(FavoriteCommand favorite)
    {
        return MainViewModel.NormalizeFavoriteCategory(favorite.Category);
    }

    private static double GetButtonWidth(FavoriteCommand favorite)
    {
        return ClampDimension(favorite.ButtonWidth, FavoriteCommandDefaults.ButtonWidth, 70, 240);
    }

    private static double GetButtonHeight(FavoriteCommand favorite)
    {
        return ClampDimension(favorite.ButtonHeight, FavoriteCommandDefaults.ButtonHeight, 24, 72);
    }

    private static double ClampDimension(double value, double fallback, double min, double max)
    {
        return double.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : fallback;
    }

    private static string GetFavoriteBackgroundColor(FavoriteCommand favorite)
    {
        return Color.TryParse(favorite.BackgroundColor, out _) ? favorite.BackgroundColor : FavoriteCommandDefaults.BackgroundColor;
    }

    private static string GetFavoriteTextColor(FavoriteCommand favorite)
    {
        return Color.TryParse(favorite.TextColor, out _) ? favorite.TextColor : FavoriteCommandDefaults.TextColor;
    }

    private static IBrush ParseBrush(string? color, string fallback)
    {
        return Color.TryParse(color, out var parsed) ? new SolidColorBrush(parsed) : new SolidColorBrush(Color.Parse(fallback));
    }

    private string BuildFavoriteToolTip(FavoriteCommand favorite)
    {
        var defaultCommand = favorite.CommandText;
        var groundCommand = string.IsNullOrWhiteSpace(favorite.GroundCommandText) ? null : favorite.GroundCommandText;
        var activeCommand = DataContext is MainViewModel vm ? vm.ResolveFavoriteCommandText(favorite) : defaultCommand;

        return groundCommand is null
            ? $"{activeCommand}\nLeft-click: execute\n{PlatformHelper.ActionModifierName}+click: append to input\nRight-click: edit"
            : string.Join(
                "\n",
                $"Active: {activeCommand}",
                $"Air/default: {defaultCommand}",
                $"Ground: {groundCommand}",
                "Left-click: execute",
                $"{PlatformHelper.ActionModifierName}+click: append to input",
                "Right-click: edit"
            );
    }

    private static ComboBox CreateCategoryBox(FavoriteCommandCategory selected)
    {
        return new ComboBox
        {
            ItemsSource = PaletteCategories,
            SelectedItem = selected,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static ColorPicker CreateColorPicker(string color)
    {
        return new ColorPicker
        {
            Color = Color.TryParse(color, out var parsed) ? parsed : Colors.White,
            IsAlphaEnabled = false,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static NumericUpDown CreateDimensionBox(double value, double minimum, double maximum)
    {
        return new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            Increment = 2,
            // Wide enough for a 3-digit value next to the 68px Fluent spinner pair (issue #359).
            Width = 110,
            Margin = new Thickness(0, 0, 4, 4),
        };
    }

    private static Control CreateLabeledControl(string label, Control control)
    {
        var panel = new StackPanel();
        panel.Children.Add(
            new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 2),
            }
        );
        panel.Children.Add(control);
        return panel;
    }

    private static Control CreateDimensionRow(NumericUpDown widthBox, NumericUpDown heightBox)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        row.Children.Add(CreateInlineDimension("W", widthBox));
        row.Children.Add(CreateInlineDimension("H", heightBox));
        return row;
    }

    private static Control CreateInlineDimension(string label, NumericUpDown box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(
            new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 4, 4),
            }
        );
        panel.Children.Add(box);
        return panel;
    }

    private static double GetDimensionValue(NumericUpDown box, double fallback)
    {
        return box.Value is { } value ? (double)value : fallback;
    }

    private static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// The "In" checkbox group of a favorite flyout: one checkbox per container — Global, the active
    /// airport/scenario (created on demand when first checked), every airport/scenario that already
    /// has favorites, and every named set. A favorite lives in exactly the checked containers; the
    /// same entity shows in each, so an edit anywhere updates it everywhere.
    /// </summary>
    private sealed class FavoriteContainerPicker
    {
        private readonly List<(CheckBox Box, FavoriteContainerOption Option)> _entries = [];

        public FavoriteContainerPicker(MainViewModel vm, IReadOnlyList<string> checkedSetIds)
        {
            var panel = new StackPanel();
            foreach (var option in vm.BuildFavoriteContainerOptions())
            {
                var isChecked = option.SetId is not null && checkedSetIds.Contains(option.SetId, StringComparer.OrdinalIgnoreCase);
                var box = new CheckBox
                {
                    Content = option.Label,
                    IsChecked = isChecked,
                    FontSize = 12,
                    MinHeight = 0,
                    Padding = new Thickness(4, 0, 0, 0),
                };
                box.IsCheckedChanged += (_, _) => SelectionChanged?.Invoke();
                _entries.Add((box, option));
                panel.Children.Add(box);
            }

            Control = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 132,
                Width = 200,
                Margin = new Thickness(0, 0, 0, 4),
            };
        }

        public Control Control { get; }

        public event Action? SelectionChanged;

        public int SelectedCount => _entries.Count(e => e.Box.IsChecked == true);

        /// <summary>Set ids of the checked containers, creating on-demand airport/scenario sets as needed.</summary>
        public List<string> ResolveSelectedSetIds(MainViewModel vm)
        {
            return _entries.Where(e => e.Box.IsChecked == true).Select(e => vm.EnsureFavoriteContainer(e.Option)).ToList();
        }
    }
}
