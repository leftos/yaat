using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>
/// The shell CRC's ERAM FDB menus share (BaseFdbMenu): a header row with the callsign and an X cell, an optional ERAM | PILOT
/// chip row in the same header-cell style, and the menu's contents, laid out on CRC's character grid and drawn by the control
/// itself. It sends nothing: a pick raises an event and the host acts on it.
/// </summary>
public abstract class CrcFdbMenu : Control, ICustomHitTest
{
    private static readonly ILogger Log = AppLog.CreateLogger("CrcFdbMenu");

    private readonly int _widthChars;
    private readonly string _menuName;
    private IReadOnlyList<CrcDrawOp> _ops = [];
    private IReadOnlyList<CrcPickTarget> _targets = [];
    private string? _hoveredKey;
    private Typeface? _typeface;
    private CrcNode? _listBox;

    /// <summary>Builds the shell; a derived menu lays itself out with <see cref="Relayout"/> once its own state is set.</summary>
    /// <param name="callsign">The aircraft ID CRC shows in the header.</param>
    /// <param name="widthChars">The menu's minimum width in characters (BaseFdbMenu.GetWidth: 10 for heading, 14 otherwise).</param>
    /// <param name="mode">The chip cell lit at open.</param>
    /// <param name="showModeChip">Whether the ERAM | PILOT chip row shows.</param>
    protected CrcFdbMenu(string callsign, int widthChars, CrcMenuMode mode, bool showModeChip)
    {
        Callsign = callsign;
        _widthChars = widthChars;
        _menuName = GetType().Name;
        Mode = mode;
        ShowModeChip = showModeChip;
        Focusable = false;
    }

    /// <summary>Raised when the unlit chip cell is clicked, with the mode it selects.</summary>
    public event Action<CrcMenuMode>? ModeChanged;

    /// <summary>Raised when the header's X cell is clicked (CRC's ClosePickArea hides the menu).</summary>
    public event Action? CloseRequested;

    /// <summary>The aircraft ID in the header.</summary>
    public string Callsign { get; }

    /// <summary>The chip cell now lit.</summary>
    public CrcMenuMode Mode { get; private set; }

    /// <summary>Whether the ERAM | PILOT chip row shows.</summary>
    public bool ShowModeChip { get; }

    /// <summary>The menu's outer width in pixels.</summary>
    public int MenuWidth { get; private set; }

    /// <summary>The menu's outer height in pixels.</summary>
    public int MenuHeight { get; private set; }

    /// <summary>The vertical middle of the scrolling list box, from the menu's top, the header and chip rows included.</summary>
    public double ListMiddleY { get; private set; }

    /// <summary>
    /// The FDB line the list's middle is level with (BaseFdbMenu.GetFdbLineForYAlignment): 2 for altitude, 4 for heading and
    /// speed.
    /// </summary>
    public abstract int FdbAlignLine { get; }

    internal IReadOnlyList<CrcDrawOp> DrawOps => _ops;

    internal IReadOnlyList<CrcPickTarget> PickTargets => _targets;

    /// <summary>The strips a quad's border is drawn as: inside the rectangle for a negative thickness, outside for a positive one.</summary>
    internal static IEnumerable<CrcRect> BorderStrips(CrcRect r, int thickness)
    {
        int t = Math.Abs(thickness);
        if (thickness < 0)
        {
            return
            [
                new(r.X, r.Y, r.Width, t),
                new(r.X + r.Width - t, r.Y, t, r.Height),
                new(r.X, r.Y + r.Height - t, r.Width, t),
                new(r.X, r.Y, t, r.Height),
            ];
        }
        if (thickness > 0)
        {
            return
            [
                new(r.X - t, r.Y - t, r.Width + (2 * t), t),
                new(r.X + r.Width, r.Y - t, t, r.Height + (2 * t)),
                new(r.X - t, r.Y + r.Height, r.Width + (2 * t), t),
                new(r.X - t, r.Y - t, t, r.Height + (2 * t)),
            ];
        }
        return [];
    }

    internal CrcPickTarget? HitPickArea(Point point) => _targets.FirstOrDefault(t => t.Area.Contains(point));

    /// <summary>The nodes under the header (and chip) rows, top to bottom, built from the menu's current state.</summary>
    private protected abstract IEnumerable<CrcNode> BuildContents();

    /// <summary>Lays the menu out again from its current state and redraws it.</summary>
    private protected void Relayout()
    {
        var root = new CrcColumn { MinWidth = CrcGrid.Chars(_widthChars), Padding = new CrcEdges(0, 0, 0, 1) };
        root.Add(BuildHeader());
        if (ShowModeChip)
        {
            root.Add(BuildChip());
        }
        var contents = new CrcColumn { Fill = CrcPalette.Black, WidthGrow = 1 };
        contents.Add([.. BuildContents()]);
        root.Add(contents);
        root.Calculate();
        root.Grow(0, 0);
        var sink = new CrcRenderSink();
        root.Render(0, 0, sink);
        _ops = sink.Ops;
        _targets = sink.Targets;
        MenuWidth = root.Width;
        MenuHeight = root.Height;
        ListMiddleY = (_listBox is { } box) ? box.Y + (box.Height / 2.0) : MenuHeight / 2.0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>CRC's MenuPickArea: a bordered grey cell overlapping the one above by 1 px, its label centred, coral when lit.</summary>
    private protected static CrcRow PickArea(string label, Action<bool> pick, bool lit) =>
        PickArea(label, pick, lit, null, new CrcEdges(-1, 0, 0, 0));

    /// <summary>As the other overload, at least <paramref name="minWidth"/> pixels wide.</summary>
    private protected static CrcRow PickArea(string label, Action<bool> pick, bool lit, int minWidth) =>
        PickArea(label, pick, lit, minWidth, new CrcEdges(-1, 0, 0, 0));

    /// <summary>As the other overloads, at least <paramref name="minWidth"/> pixels wide (none: as its label) with <paramref name="margin"/>.</summary>
    private protected static CrcRow PickArea(string label, Action<bool> pick, bool lit, int? minWidth, CrcEdges margin)
    {
        var area = new CrcRow
        {
            Margin = margin,
            MinWidth = minWidth,
            BorderWidth = 1,
            BorderColor = CrcPalette.Outline,
            Fill = lit ? CrcPalette.Emphasis : CrcPalette.PickBackground,
            PickKey = label,
            Pick = pick,
        };
        area.Add(new CrcText(label, CrcPalette.Text) { WidthGrow = 1, HAlign = CrcAlign.Center });
        return area;
    }

    /// <summary>
    /// The list box every FDB menu scrolls: a bordered row overlapping the cell above by 1 px, holding the seven-line list (half
    /// a character of padding, 1 px between lines) and CRC's ScrollPickAreas, hidden while the whole list fits.
    /// </summary>
    private protected CrcNode ScrollWrapper(IEnumerable<CrcNode> rows, CrcPageScroll scroll)
    {
        int vertical = CrcGrid.Lines(0.5);
        int horizontal = CrcGrid.Chars(0.5);
        var list = new CrcColumn
        {
            Padding = new CrcEdges(vertical, horizontal, vertical, horizontal),
            Gap = 1,
            WidthGrow = 1,
        };
        list.Add([.. rows]);
        var wrapper = new CrcRow
        {
            Padding = new CrcEdges(1, 0, 0, 0),
            Margin = new CrcEdges(-1, 0, 0, 0),
            BorderWidth = 1,
            BorderColor = CrcPalette.Outline,
        };
        _listBox = wrapper;
        return wrapper.Add(list, ScrollArrows(scroll));
    }

    private static CrcNode ScrollArrows(CrcPageScroll scroll)
    {
        Color up = scroll.UpIsEnabled ? CrcPalette.ScrollEnabled : CrcPalette.ScrollDisabled;
        Color down = scroll.DownIsEnabled ? CrcPalette.ScrollEnabled : CrcPalette.ScrollDisabled;
        var upBox = new CrcColumn
        {
            BorderWidth = 1,
            BorderColor = up,
            Padding = new CrcEdges(2, 3, 0, 2),
            PickKey = "UP",
            Pick = _ => scroll.Scroll(down: false),
        };
        upBox.Add(new CrcText("▲", up) { Padding = CrcEdges.None, Fill = CrcPalette.Black });
        var downBox = new CrcColumn
        {
            BorderWidth = 1,
            BorderColor = down,
            Padding = new CrcEdges(0, 3, 2, 2),
            PickKey = "DOWN",
            Pick = _ => scroll.Scroll(down: true),
        };
        downBox.Add(
            new CrcText("▼", down)
            {
                Padding = CrcEdges.None,
                HeightGrow = 1,
                VAlign = CrcAlign.End,
                Fill = CrcPalette.Black,
            }
        );
        var arrows = new CrcColumn
        {
            Margin = new CrcEdges(1, 1, 1, 1),
            Gap = 1,
            Visibility = scroll.ScrollIsEnabled ? CrcVisibility.Visible : CrcVisibility.Hidden,
        };
        return arrows.Add(upBox, downBox);
    }

    private CrcNode BuildHeader()
    {
        var header = new CrcRow { Margin = new CrcEdges(0, 0, -1, 0) };
        var close = new CrcHeaderCell("X")
        {
            WidthGrow = 0,
            Margin = new CrcEdges(0, 0, 0, -1),
            MinWidth = CrcGrid.Chars(2, -1),
            PickKey = "X",
            Pick = _ => CloseRequested?.Invoke(),
        };
        return header.Add(new CrcHeaderCell(Callsign), close);
    }

    private CrcNode BuildChip()
    {
        var chip = new CrcRow { Margin = new CrcEdges(0, 0, -1, 0) };
        return chip.Add(ChipCell("ERAM", CrcMenuMode.Eram, CrcEdges.None), ChipCell("PILOT", CrcMenuMode.Pilot, new CrcEdges(0, 0, 0, -1)));
    }

    private CrcHeaderCell ChipCell(string label, CrcMenuMode mode, CrcEdges margin) =>
        new(label)
        {
            Margin = margin,
            Fill = (Mode == mode) ? CrcPalette.Emphasis : CrcPalette.PickBackground,
            PickKey = label,
            Pick = _ => SelectMode(mode),
        };

    private void SelectMode(CrcMenuMode mode)
    {
        if (mode == Mode)
        {
            return;
        }
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => new(MenuWidth, MenuHeight);

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        CrcPickTarget? target = HitPickArea(e.GetPosition(this));
        if (target is null)
        {
            return;
        }
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Menu} for {Callsign}: picked {Key} (shift {Shift})", _menuName, Callsign, target.Key, shift);
        }
        target.Pick(shift);
        Relayout();
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovered(HitPickArea(e.GetPosition(this))?.Key);
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovered(null);
    }

    private void SetHovered(string? key)
    {
        if (key != _hoveredKey)
        {
            _hoveredKey = key;
            InvalidateVisual();
        }
    }

    bool ICustomHitTest.HitTest(Point point) => (point.X >= 0) && (point.Y >= 0) && (point.X < MenuWidth) && (point.Y < MenuHeight);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        foreach (CrcDrawOp op in _ops)
        {
            if (op is CrcQuadOp quad)
            {
                DrawQuad(context, quad);
            }
            else if (op is CrcTextOp text)
            {
                DrawText(context, text);
            }
        }
        DrawHover(context);
    }

    private void DrawHover(DrawingContext context)
    {
        CrcPickTarget? hovered = (_hoveredKey is null) ? null : _targets.FirstOrDefault(t => t.Key == _hoveredKey);
        if (hovered is not null)
        {
            DrawQuad(context, new CrcQuadOp(hovered.Area, null, CrcPalette.CursorEmphasis, hovered.IsText ? 1 : -1));
        }
    }

    private static void DrawQuad(DrawingContext context, CrcQuadOp quad)
    {
        if (quad.Fill is { } fill)
        {
            context.FillRectangle(new ImmutableSolidColorBrush(fill), ToRect(quad.Rect));
        }
        if (quad.Border is { } border)
        {
            var brush = new ImmutableSolidColorBrush(border);
            foreach (CrcRect strip in BorderStrips(quad.Rect, quad.Thickness))
            {
                context.FillRectangle(brush, ToRect(strip));
            }
        }
    }

    private void DrawText(DrawingContext context, CrcTextOp text)
    {
        var brush = new ImmutableSolidColorBrush(text.Color);
        _typeface ??= new Typeface(ResolveMonoFont());
        for (int i = 0; i < text.Text.Length; i++)
        {
            if (text.Text[i] == ' ')
            {
                continue;
            }
            var glyph = new FormattedText(
                text.Text[i].ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface.Value,
                CrcGrid.FontSize,
                brush
            );
            double centre = text.X + (i * CrcGrid.Advance) + (CrcGrid.GlyphWidth / 2.0);
            context.DrawText(glyph, new Point(centre - (glyph.Width / 2), text.Y + CrcGrid.Baseline - glyph.Baseline));
        }
    }

    /// <summary>The client's MonoFont resource (embedded JetBrains Mono, then system monospace fallbacks).</summary>
    private static FontFamily ResolveMonoFont() =>
        ((Application.Current?.TryGetResource("MonoFont", null, out object? value) == true) && (value is FontFamily family))
            ? family
            : FontFamily.Default;

    private static Rect ToRect(CrcRect r) => new(r.X, r.Y, r.Width, r.Height);
}
