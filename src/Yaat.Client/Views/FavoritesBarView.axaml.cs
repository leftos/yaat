using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

    private MainViewModel? _boundVm;
    private WrapPanel? _panel;
    private TabControl? _tabControl;
    private Button? _addButton;
    private FavoriteCommandCategory _selectedPaletteCategory = FavoriteCommandCategory.Air;

    public bool IsPaletteMode
    {
        get => GetValue(IsPaletteModeProperty);
        set => SetValue(IsPaletteModeProperty, value);
    }

    public FavoritesBarView()
    {
        InitializeComponent();
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

        var blankButton = CreateAddBlankButton();
        blankButton.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(blankButton, Dock.Right);
        header.Children.Add(blankButton);

        var addButton = CreateAddButton();
        addButton.Content = "Add";
        addButton.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(addButton, Dock.Right);
        header.Children.Add(addButton);

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

        foreach (var fav in vm.DisplayFavorites.Where(f => !f.IsSpacer))
        {
            var btn = CreateFavoriteButton(fav);
            _panel.Children.Add(btn);
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
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };

            foreach (var fav in vm.DisplayFavorites.Where(f => NormalizeCategory(f) == category))
            {
                panel.Children.Add(fav.IsSpacer ? CreateBlankSlot(fav) : CreateFavoriteButton(fav));
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

    private Button CreateFavoriteButton(FavoriteCommand fav)
    {
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
            Tag = fav,
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

    private Button CreateBlankSlot(FavoriteCommand fav)
    {
        var width = GetButtonWidth(fav);
        var height = GetButtonHeight(fav);
        var btn = new Button
        {
            Tag = fav,
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

    private void OnFavoritePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FavoriteCommand fav)
        {
            return;
        }

        var props = e.GetCurrentPoint(btn).Properties;

        // Right-click → edit flyout
        if (props.IsRightButtonPressed)
        {
            e.Handled = true;
            if (fav.IsSpacer)
            {
                ShowEditBlankFlyout(btn, fav);
            }
            else
            {
                ShowEditFlyout(btn, fav);
            }
            return;
        }

        // Ctrl+Left-click → append to input
        if (!fav.IsSpacer && props.IsLeftButtonPressed && PlatformHelper.HasActionModifier(e.KeyModifiers))
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm)
            {
                vm.AppendFavoriteToInput(fav);
            }
        }
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FavoriteCommand fav)
        {
            return;
        }

        if (!fav.IsSpacer && DataContext is MainViewModel vm)
        {
            _ = vm.ExecuteFavoriteAsync(fav);
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
            CreateBlankFavorite(
                GetActiveAddCategory(),
                scopeSource: null,
                width: FavoriteCommandDefaults.ButtonWidth,
                height: FavoriteCommandDefaults.ButtonHeight
            )
        );
    }

    private void OnOpenPanelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        FavoritesPanelWindow.ShowOrActivate(vm, TopLevel.GetTopLevel(this) as Window);
    }

    private void OnPaletteTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabControl?.SelectedItem is TabItem { Header: string header } && Enum.TryParse<FavoriteCommandCategory>(header, out var category))
        {
            _selectedPaletteCategory = category;
        }
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
            Watermark = "Label (e.g. FH 270)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var commandBox = new TextBox
        {
            Text = prefillCommand,
            Watermark = "Command text (e.g. FH 270, CM 014)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var groundCommandBox = new TextBox
        {
            Watermark = "Ground command override (optional)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var categoryBox = CreateCategoryBox(category);
        var backgroundPicker = CreateColorPicker(FavoriteCommandDefaults.BackgroundColor);
        var textPicker = CreateColorPicker(FavoriteCommandDefaults.TextColor);
        var widthBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonWidth, 70, 240);
        var heightBox = CreateDimensionBox(FavoriteCommandDefaults.ButtonHeight, 24, 72);
        var scopeBox = CreateScopeBox(vm, FavoriteScope.Global);

        var saveBtn = new Button { Content = "Add", Margin = new Thickness(0, 4, 0, 0) };

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
            scopeBox,
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
                Label = label,
                CommandText = cmdText,
                GroundCommandText = groundCommandBox.Text?.Trim() ?? "",
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : category,
                BackgroundColor = ToHex(backgroundPicker.Color),
                TextColor = ToHex(textPicker.Color),
                ButtonWidth = GetDimensionValue(widthBox, FavoriteCommandDefaults.ButtonWidth),
                ButtonHeight = GetDimensionValue(heightBox, FavoriteCommandDefaults.ButtonHeight),
            };
            ApplySelectedScope(fav, scopeBox, vm, null);

            vm.AddFavorite(fav);
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }

    private void ShowEditFlyout(Button target, FavoriteCommand fav)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

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
        var scopeBox = CreateScopeBox(vm, GetFavoriteScope(fav));

        var deleteBtn = CreateDeleteButton();
        var saveBtn = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        var insertBeforeBtn = CreateInsertBlankButton("Blank Before");
        var insertAfterBtn = CreateInsertBlankButton("Blank After");
        var footer = CreateEditFooter(deleteBtn, saveBtn, insertBeforeBtn, insertAfterBtn);

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
            scopeBox,
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
                Label = label,
                CommandText = cmdText,
                GroundCommandText = groundCommandBox.Text?.Trim() ?? "",
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : NormalizeCategory(fav),
                BackgroundColor = ToHex(backgroundPicker.Color),
                TextColor = ToHex(textPicker.Color),
                ButtonWidth = GetDimensionValue(widthBox, GetButtonWidth(fav)),
                ButtonHeight = GetDimensionValue(heightBox, GetButtonHeight(fav)),
            };
            ApplySelectedScope(updated, scopeBox, vm, fav);

            vm.UpdateFavorite(fav, updated);
            flyout.Hide();
        };

        deleteBtn.Click += (_, _) =>
        {
            vm.RemoveFavorite(fav);
            flyout.Hide();
        };

        insertBeforeBtn.Click += (_, _) =>
        {
            vm.InsertFavoriteBefore(fav, CreateBlankFavorite(NormalizeCategory(fav), fav, GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        insertAfterBtn.Click += (_, _) =>
        {
            vm.InsertFavoriteAfter(fav, CreateBlankFavorite(NormalizeCategory(fav), fav, GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }

    private void ShowEditBlankFlyout(Button target, FavoriteCommand fav)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null)
        {
            return;
        }

        var categoryBox = CreateCategoryBox(NormalizeCategory(fav));
        var widthBox = CreateDimensionBox(GetButtonWidth(fav), 70, 240);
        var heightBox = CreateDimensionBox(GetButtonHeight(fav), 24, 72);
        var scopeBox = CreateScopeBox(vm, GetFavoriteScope(fav));
        var deleteBtn = CreateDeleteButton();
        var saveBtn = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        var insertBeforeBtn = CreateInsertBlankButton("Blank Before");
        var insertAfterBtn = CreateInsertBlankButton("Blank After");
        var footer = CreateEditFooter(deleteBtn, saveBtn, insertBeforeBtn, insertAfterBtn);

        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(
            new TextBlock
            {
                Text = "Edit Blank Slot",
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            }
        );
        panel.Children.Add(CreateLabeledControl("Category", categoryBox));
        panel.Children.Add(CreateDimensionRow(widthBox, heightBox));
        panel.Children.Add(CreateLabeledControl("Scope", scopeBox));
        panel.Children.Add(footer);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };

        saveBtn.Click += (_, _) =>
        {
            var updated = new FavoriteCommand
            {
                IsSpacer = true,
                Category = categoryBox.SelectedItem is FavoriteCommandCategory selected ? selected : NormalizeCategory(fav),
                ButtonWidth = GetDimensionValue(widthBox, GetButtonWidth(fav)),
                ButtonHeight = GetDimensionValue(heightBox, GetButtonHeight(fav)),
            };
            ApplySelectedScope(updated, scopeBox, vm, fav);

            vm.UpdateFavorite(fav, updated);
            flyout.Hide();
        };

        deleteBtn.Click += (_, _) =>
        {
            vm.RemoveFavorite(fav);
            flyout.Hide();
        };

        insertBeforeBtn.Click += (_, _) =>
        {
            vm.InsertFavoriteBefore(fav, CreateBlankFavorite(NormalizeCategory(fav), fav, GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        insertAfterBtn.Click += (_, _) =>
        {
            vm.InsertFavoriteAfter(fav, CreateBlankFavorite(NormalizeCategory(fav), fav, GetButtonWidth(fav), GetButtonHeight(fav)));
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }

    private FavoriteCommandCategory GetActiveAddCategory()
    {
        return IsPaletteMode ? _selectedPaletteCategory : FavoriteCommandCategory.Air;
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
        ComboBox scopeBox,
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
        panel.Children.Add(CreateLabeledControl("Scope", scopeBox));
        panel.Children.Add(footer);
        return panel;
    }

    private static ComboBox CreateScopeBox(MainViewModel vm, FavoriteScope selectedScope)
    {
        var options = new List<FavoriteScopeOption> { new(FavoriteScope.Global, "Global") };
        if (vm.ActiveScenarioId is not null || selectedScope == FavoriteScope.Scenario)
        {
            options.Add(new FavoriteScopeOption(FavoriteScope.Scenario, "Scenario"));
        }

        if (vm.ActiveScenarioPrimaryAirportId is not null || selectedScope == FavoriteScope.Airport)
        {
            var airport = vm.ActiveScenarioPrimaryAirportId ?? "saved airport";
            options.Add(new FavoriteScopeOption(FavoriteScope.Airport, $"Airport ({airport})"));
        }

        var selected = options.FirstOrDefault(o => o.Scope == selectedScope) ?? options[0];
        return new ComboBox
        {
            ItemsSource = options,
            SelectedItem = selected,
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static void ApplySelectedScope(FavoriteCommand target, ComboBox scopeBox, MainViewModel vm, FavoriteCommand? previous)
    {
        target.ScenarioId = null;
        target.AirportId = null;

        var scope = scopeBox.SelectedItem is FavoriteScopeOption option ? option.Scope : FavoriteScope.Global;
        if (scope == FavoriteScope.Scenario)
        {
            target.ScenarioId = vm.ActiveScenarioId ?? previous?.ScenarioId;
            return;
        }

        if (scope == FavoriteScope.Airport)
        {
            target.AirportId = MainViewModel.NormalizeFavoriteAirportId(vm.ActiveScenarioPrimaryAirportId ?? previous?.AirportId);
        }
    }

    private static FavoriteScope GetFavoriteScope(FavoriteCommand favorite)
    {
        if (!string.IsNullOrWhiteSpace(favorite.ScenarioId))
        {
            return FavoriteScope.Scenario;
        }

        if (!string.IsNullOrWhiteSpace(favorite.AirportId))
        {
            return FavoriteScope.Airport;
        }

        return FavoriteScope.Global;
    }

    private static Button CreateDeleteButton()
    {
        return new Button
        {
            Content = "Delete",
            Foreground = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static Button CreateInsertBlankButton(string content)
    {
        return new Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(6, 2),
        };
    }

    private static Control CreateEditFooter(Button deleteBtn, Button saveBtn, Button insertBeforeBtn, Button insertAfterBtn)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var insertRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
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

    private static FavoriteCommand CreateBlankFavorite(FavoriteCommandCategory category, FavoriteCommand? scopeSource, double width, double height)
    {
        return new FavoriteCommand
        {
            IsSpacer = true,
            ScenarioId = scopeSource?.ScenarioId,
            AirportId = scopeSource?.AirportId,
            Category = category,
            ButtonWidth = width,
            ButtonHeight = height,
        };
    }

    private static FavoriteCommandCategory NormalizeCategory(FavoriteCommand favorite)
    {
        return Enum.IsDefined(favorite.Category) ? favorite.Category : FavoriteCommandCategory.Air;
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
            Width = 84,
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

    private enum FavoriteScope
    {
        Global,
        Scenario,
        Airport,
    }

    private sealed record FavoriteScopeOption(FavoriteScope Scope, string Label)
    {
        public override string ToString() => Label;
    }
}
