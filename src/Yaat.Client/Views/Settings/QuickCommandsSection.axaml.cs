using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ContextMenus;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Settings;

/// <summary>
/// Settings section. Quick commands: the situations on the left, the selected situation's ordered list on the right with a
/// preview of the menu it draws, rows reordered by dragging their handle, and a flyout that adds catalog commands by family.
/// </summary>
public partial class QuickCommandsSection : UserControl
{
    private const string DragHandleClass = "drag-handle";
    private const string PreviewCellClass = "preview-cell";

    // How far the pointer moves from the press before a drag shows its drop line and counts as a move.
    private const double DragThreshold = 4;

    private static readonly ImmutableSolidColorBrush PreviewCellBrush = new(0xFF2A3340);

    private readonly ObservableCollection<object> _entryItems = [];
    private readonly List<QuickCommandEntryRow> _watchedRows = [];
    private SettingsViewModel? _vm;
    private INotifyCollectionChanged? _watchedEntries;
    private bool _refreshPending;
    private QuickCommandEntryRow? _dragged;
    private IPointer? _dragPointer;
    private Point _pressedAt;
    private bool _dragMoved;

    public QuickCommandsSection()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entryItems;
        EntryList.AddHandler(PointerPressedEvent, OnEntryPointerPressed);
        EntryList.PointerMoved += OnEntryPointerMoved;
        EntryList.PointerReleased += OnEntryPointerReleased;
        EntryList.PointerCaptureLost += (_, _) => EndDrag();

        AddQuickCommandList.ContainerPrepared += OnCatalogContainerPrepared;
        AddQuickCommandList.DoubleTapped += OnCatalogDoubleTapped;
        AddQuickCommandList.KeyDown += OnCatalogKeyDown;
    }

    /// <summary>Draws a catalog row's strip glyph through the menu's own factory; a row with no glyph shows nothing.</summary>
    public static FuncValueConverter<QuickCommandGlyph?, Control?> GlyphIconConverter { get; } =
        new(glyph => (glyph is null) ? null : QuickCommandStrip.GlyphIcon(glyph));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as SettingsViewModel;
        _vm?.PropertyChanged += OnViewModelChanged;
        WatchEntries();
    }

    private IReadOnlyList<QuickCommandEntryRow> Rows() => _vm?.QuickCommandEntries ?? [];

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.QuickCommandEntries))
        {
            WatchEntries();
        }
    }

    private void WatchEntries()
    {
        _watchedEntries?.CollectionChanged -= OnEntriesChanged;
        _watchedEntries = Rows() as INotifyCollectionChanged;
        _watchedEntries?.CollectionChanged += OnEntriesChanged;
        ScheduleRefresh();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCommandEntryRow.IsInStrip))
        {
            ScheduleRefresh();
        }
        else if (e.PropertyName == nameof(QuickCommandEntryRow.Label))
        {
            RefreshPreview(Rows());
        }
    }

    // A reset or a move changes the list and every row's strip flag in one burst; the list and preview rebuild once after it.
    private void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            RefreshEntries();
        });
    }

    // The rows in order, with the divider after the tenth glyph entry when any row follows it.
    private void RefreshEntries()
    {
        IReadOnlyList<QuickCommandEntryRow> rows = Rows();
        WatchRows(rows);
        _entryItems.Clear();
        int stripRows = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            _entryItems.Add(rows[i]);
            if (rows[i].IsInStrip)
            {
                stripRows++;
            }

            if (rows[i].IsInStrip && (stripRows == QuickCommandGlyphs.StripCapacity) && (i < rows.Count - 1))
            {
                _entryItems.Add(QuickCommandStripDivider.Instance);
            }
        }

        RefreshPreview(rows);
    }

    private void WatchRows(IReadOnlyList<QuickCommandEntryRow> rows)
    {
        foreach (QuickCommandEntryRow row in _watchedRows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _watchedRows.Clear();
        _watchedRows.AddRange(rows);
        foreach (QuickCommandEntryRow row in _watchedRows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    // The strip as the menu lays it out, through the menu's own factory, then every other entry as text in list order.
    private void RefreshPreview(IReadOnlyList<QuickCommandEntryRow> rows)
    {
        List<Control> cells = [];
        foreach (QuickCommandEntryRow row in rows)
        {
            if (row.IsInStrip && (row.Glyph is { } glyph))
            {
                cells.Add(PreviewCell(row, glyph));
            }
        }

        PreviewStripHost.Child = (cells.Count == 0) ? null : QuickCommandStrip.Rows(cells);
        PreviewStripHost.IsVisible = cells.Count > 0;

        PreviewTextEntries.Children.Clear();
        foreach (QuickCommandEntryRow row in rows.Where(r => !r.IsInStrip))
        {
            PreviewTextEntries.Children.Add(
                new TextBlock { Text = string.IsNullOrWhiteSpace(row.Label) ? "(no label)" : row.Label, Margin = new Thickness(0, 0, 12, 2) }
            );
        }
    }

    private static Border PreviewCell(QuickCommandEntryRow row, QuickCommandGlyph glyph)
    {
        var cell = new Border
        {
            Child = QuickCommandStrip.GlyphCell(glyph, opensSubmenu: false),
            Background = PreviewCellBrush,
            CornerRadius = new CornerRadius(4),
            Tag = row.CatalogId,
        };
        cell.Classes.Add(PreviewCellClass);
        ToolTip.SetTip(cell, row.Label);
        return cell;
    }

    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EntryList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Control? handle = (e.Source as Visual)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(c => c.Classes.Contains(DragHandleClass));
        if (handle?.DataContext is not QuickCommandEntryRow row)
        {
            return;
        }

        _dragged = row;
        _dragPointer = e.Pointer;
        _pressedAt = e.GetPosition(EntryList);
        _dragMoved = false;
        e.Pointer.Capture(EntryList);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
        e.Handled = true;
    }

    private void OnEntryPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null)
        {
            return;
        }

        Point at = e.GetPosition(EntryList);
        Point travel = at - _pressedAt;
        _dragMoved |= (Math.Abs(travel.X) >= DragThreshold) || (Math.Abs(travel.Y) >= DragThreshold);
        if (_dragMoved)
        {
            ShowDropIndicator(at.Y);
        }
    }

    private void OnEntryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((_dragged is not { } row) || (e.InitialPressMouseButton != MouseButton.Left))
        {
            return;
        }

        bool moved = _dragMoved;
        int fromIndex = IndexOf(row);
        int insertIndex = InsertIndexAt(e.GetPosition(EntryList).Y);
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
        if (moved && (fromIndex >= 0))
        {
            _vm?.MoveQuickCommandEntry(fromIndex, insertIndex);
        }
    }

    // Escape drops the drag where it started: releasing the capture ends it through PointerCaptureLost.
    private void OnDragKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Escape) && (_dragPointer is { } pointer))
        {
            pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
        _dragged = null;
        _dragPointer = null;
        _dragMoved = false;
        DropIndicator.IsVisible = false;
    }

    private int IndexOf(QuickCommandEntryRow row)
    {
        IReadOnlyList<QuickCommandEntryRow> rows = Rows();
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    // The line between rows where the dragged row would land: the top of the row it would go before, or the bottom of the last.
    private void ShowDropIndicator(double y)
    {
        IReadOnlyList<QuickCommandEntryRow> rows = Rows();
        int insertIndex = InsertIndexAt(y);
        Control? anchor = (rows.Count == 0) ? null : EntryList.ContainerFromItem(rows[Math.Min(insertIndex, rows.Count - 1)]);
        if (anchor is null)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        double top = TopOf(anchor) + ((insertIndex < rows.Count) ? 0 : anchor.Bounds.Height);
        Canvas.SetTop(DropIndicator, top - 1);
        DropIndicator.Width = EntryList.Bounds.Width;
        DropIndicator.IsVisible = true;
    }

    // The insert position (0 to the list's count) a row dropped at y, in the entry list's coordinates, lands at: before the
    // first row whose upper half is below it, or last.
    private int InsertIndexAt(double y)
    {
        IReadOnlyList<QuickCommandEntryRow> rows = Rows();
        for (int i = 0; i < rows.Count; i++)
        {
            if ((EntryList.ContainerFromItem(rows[i]) is { } container) && (y < (TopOf(container) + (container.Bounds.Height / 2))))
            {
                return i;
            }
        }

        return rows.Count;
    }

    private double TopOf(Control container) => container.TranslatePoint(default, EntryList)?.Y ?? container.Bounds.Y;

    private void OnCatalogContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        bool header = e.Container.DataContext is QuickCommandFamilyHeader;
        e.Container.IsHitTestVisible = !header;
        e.Container.Focusable = !header;
    }

    private void OnCatalogDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is QuickCommandCatalogItem)
        {
            AddSelectedCatalogEntry();
        }
    }

    private void OnCatalogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddSelectedCatalogEntry();
            e.Handled = true;
        }
    }

    private void AddSelectedCatalogEntry()
    {
        if ((_vm?.AddQuickCommandCatalogEntryCommand is { } add) && add.CanExecute(null))
        {
            add.Execute(null);
        }
    }
}

/// <summary>The entry list's marker after the tenth glyph entry, where the icon strip ends and text entries begin.</summary>
public sealed class QuickCommandStripDivider
{
    public static QuickCommandStripDivider Instance { get; } = new();

    private QuickCommandStripDivider() { }
}
