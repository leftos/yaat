using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class MacroImportItem : ObservableObject
{
    public required SavedMacro Macro { get; init; }
    public required bool IsOverwrite { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;

    public string DisplayText => $"#{Macro.Name}  →  {Macro.Expansion}";
    public string OverwriteLabel => IsOverwrite ? "(overwrite)" : "";
}

public partial class MacroImportWindow : Window
{
    private readonly List<MacroImportItem> _items;

    public MacroImportWindow()
    {
        _items = [];
        InitializeComponent();
    }

    public MacroImportWindow(List<SavedMacro> macros, HashSet<string> existingNames)
    {
        _items = macros.Select(m => new MacroImportItem { Macro = m, IsOverwrite = existingNames.Contains(m.Name) }).ToList();

        InitializeComponent();

        var list = this.FindControl<ListBox>("MacroList");
        if (list is not null)
        {
            list.ItemsSource = _items;
        }

        var importAllBtn = this.FindControl<Button>("ImportAllButton");
        if (importAllBtn is not null)
        {
            importAllBtn.Click += OnImportAllClick;
        }

        var importSelectedBtn = this.FindControl<Button>("ImportSelectedButton");
        if (importSelectedBtn is not null)
        {
            importSelectedBtn.Click += OnImportSelectedClick;
        }

        var cancelBtn = this.FindControl<Button>("CancelButton");
        if (cancelBtn is not null)
        {
            cancelBtn.Click += OnCancelClick;
        }
    }

    private void OnImportAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsSelected = true;
        }

        Close(GetSelectedMacros());
    }

    private void OnImportSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(GetSelectedMacros());
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }

    private List<SavedMacro> GetSelectedMacros()
    {
        return _items.Where(i => i.IsSelected).Select(i => i.Macro).ToList();
    }
}
