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
    private readonly WrapPanel _panel;

    public FavoritesBarView()
    {
        InitializeComponent();

        _panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        Content = _panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel vm)
        {
            vm.DisplayFavorites.CollectionChanged += OnFavoritesChanged;
            RebuildButtons();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (DataContext is MainViewModel vm)
        {
            vm.DisplayFavorites.CollectionChanged -= OnFavoritesChanged;
        }
    }

    private void OnFavoritesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        _panel.Children.Clear();

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        foreach (var fav in vm.DisplayFavorites)
        {
            var btn = CreateFavoriteButton(fav);
            _panel.Children.Add(btn);
        }

        _panel.Children.Add(CreateAddButton());
    }

    private Button CreateFavoriteButton(FavoriteCommand fav)
    {
        var btn = new Button
        {
            Content = fav.Label,
            Tag = fav,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(8, 2),
            FontSize = 12,
        };

        ToolTip.SetTip(btn, "Left-click: execute | Ctrl+click: append to input | Right-click: edit");

        btn.AddHandler(PointerPressedEvent, OnFavoritePointerPressed, RoutingStrategies.Tunnel);
        btn.Click += OnFavoriteClick;

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
            ShowEditFlyout(btn, fav);
            return;
        }

        // Ctrl+Left-click → append to input
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
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

        if (DataContext is MainViewModel vm)
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

        ShowAddFlyout(btn);
    }

    private void ShowAddFlyout(Button target)
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
            Watermark = "Command text (e.g. FH 270, CM 014)",
            Width = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var scenarioCheck = new CheckBox
        {
            Content = "Scenario-specific",
            IsEnabled = vm.ActiveScenarioId is not null,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var saveBtn = new Button { Content = "Add", Margin = new Thickness(0, 4, 0, 0) };

        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(
            new TextBlock
            {
                Text = "Add Favorite",
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            }
        );
        panel.Children.Add(labelBox);
        panel.Children.Add(commandBox);
        panel.Children.Add(scenarioCheck);
        panel.Children.Add(saveBtn);

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
                ScenarioId = scenarioCheck.IsChecked == true ? vm.ActiveScenarioId : null,
            };

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
        var scenarioCheck = new CheckBox
        {
            Content = "Scenario-specific",
            IsChecked = fav.ScenarioId is not null,
            IsEnabled = vm.ActiveScenarioId is not null,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var deleteBtn = new Button
        {
            Content = "Delete",
            Foreground = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var saveBtn = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };

        var buttonRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(deleteBtn, Dock.Left);
        buttonRow.Children.Add(deleteBtn);
        buttonRow.Children.Add(saveBtn);

        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(
            new TextBlock
            {
                Text = "Edit Favorite",
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            }
        );
        panel.Children.Add(labelBox);
        panel.Children.Add(commandBox);
        panel.Children.Add(scenarioCheck);
        panel.Children.Add(buttonRow);

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
                ScenarioId = scenarioCheck.IsChecked == true ? vm.ActiveScenarioId ?? fav.ScenarioId : null,
            };

            vm.UpdateFavorite(fav, updated);
            flyout.Hide();
        };

        deleteBtn.Click += (_, _) =>
        {
            vm.RemoveFavorite(fav);
            flyout.Hide();
        };

        flyout.ShowAt(target);
    }
}
