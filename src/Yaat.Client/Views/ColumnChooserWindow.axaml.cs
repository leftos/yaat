using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.Views;

public partial class ColumnChooserWindow : Window
{
    public ObservableCollection<ColumnEntry> Entries { get; } = [];
    public bool Confirmed { get; private set; }
    public bool ShowOnlyActive { get; private set; }

    public ColumnChooserWindow()
    {
        InitializeComponent();
    }

    public ColumnChooserWindow(List<ColumnEntry> columns, bool showOnlyActive)
    {
        InitializeComponent();

        foreach (var col in columns)
        {
            Entries.Add(col);
        }

        ColumnList.ItemsSource = Entries;
        ShowOnlyActiveCheckBox.IsChecked = showOnlyActive;

        MoveTopButton.Click += OnMoveTop;
        MoveUpButton.Click += OnMoveUp;
        MoveDownButton.Click += OnMoveDown;
        MoveLastButton.Click += OnMoveLast;
        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;
    }

    private void OnMoveTop(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx <= 0)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(0, item);
        ColumnList.SelectedIndex = 0;
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx <= 0)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(idx - 1, item);
        ColumnList.SelectedIndex = idx - 1;
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx < 0 || idx >= Entries.Count - 1)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Insert(idx + 1, item);
        ColumnList.SelectedIndex = idx + 1;
    }

    private void OnMoveLast(object? sender, RoutedEventArgs e)
    {
        var idx = ColumnList.SelectedIndex;
        if (idx < 0 || idx >= Entries.Count - 1)
        {
            return;
        }

        var item = Entries[idx];
        Entries.RemoveAt(idx);
        Entries.Add(item);
        ColumnList.SelectedIndex = Entries.Count - 1;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        ShowOnlyActive = ShowOnlyActiveCheckBox.IsChecked == true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public partial class ColumnEntry : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    [ObservableProperty]
    private bool _isVisible = true;
}
