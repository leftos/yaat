using Avalonia.Media;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>How a node places its content or a child along one axis.</summary>
internal enum CrcAlign
{
    Start,
    Center,
    End,
}

/// <summary>CRC's Visibility: Hidden keeps the node's space, Collapsed drops it from layout.</summary>
internal enum CrcVisibility
{
    Visible,
    Hidden,
    Collapsed,
}

/// <summary>
/// A port of CRC's layout Node (Vatsim.Nas.Crc.Ui.Displays.Eram.Views.Components.Node): margin, border, padding and
/// content sizes, minimum widths, and grow factors that hand out spare space, all in whole pixels.
/// </summary>
internal class CrcNode
{
    public List<CrcNode> Children { get; } = [];
    public bool Absolute { get; init; }
    public CrcAlign HAlign { get; init; }
    public CrcAlign VAlign { get; init; }
    public int? MinWidth { get; init; }
    public int WidthGrow { get; init; }
    public int HeightGrow { get; init; }
    public CrcEdges Padding { get; init; }
    public CrcEdges Margin { get; init; }
    public int BorderWidth { get; init; }
    public Color? BorderColor { get; init; }
    public Color? Fill { get; init; }
    public CrcVisibility Visibility { get; init; }
    public int Gap { get; init; }
    public string? PickKey { get; init; }
    public Action<bool>? Pick { get; init; }

    public int X { get; protected set; }
    public int Y { get; protected set; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }

    protected int GrowWidth { get; set; }
    protected int GrowHeight { get; set; }

    protected IEnumerable<CrcNode> Laid => Children.Where(c => c.Visibility != CrcVisibility.Collapsed);

    protected IEnumerable<CrcNode> Flow => Laid.Where(c => !c.Absolute);

    protected IEnumerable<CrcNode> Floating => Laid.Where(c => c.Absolute);

    public CrcRect Content =>
        new(
            X + Margin.Left + BorderWidth + Padding.Left,
            Y + Margin.Top + BorderWidth + Padding.Top,
            Width - Margin.Right - BorderWidth - Padding.Right - Padding.Left - BorderWidth - Margin.Left,
            Height - Margin.Bottom - BorderWidth - Padding.Bottom - Padding.Top - BorderWidth - Margin.Top
        );

    public virtual CrcRect BorderArea =>
        new(X + Margin.Left, Y + Margin.Top, Width - Margin.Right - Margin.Left, Height - Margin.Bottom - Margin.Top);

    public CrcNode Add(params CrcNode[] nodes)
    {
        Children.AddRange(nodes);
        return this;
    }

    public virtual void Calculate()
    {
        foreach (CrcNode child in Laid)
        {
            child.Calculate();
        }
    }

    public virtual void Grow(int addWidth, int addHeight)
    {
        Accumulate(addWidth, addHeight);
        List<CrcNode> laid = [.. Laid];
        int widthShares = laid.Sum(c => c.WidthGrow);
        int heightShares = laid.Sum(c => c.HeightGrow);
        int perWidth = (widthShares > 0) ? GrowWidth / widthShares : 0;
        int perHeight = (heightShares > 0) ? GrowHeight / heightShares : 0;
        foreach (CrcNode child in laid)
        {
            child.Grow(perWidth * child.WidthGrow, perHeight * child.HeightGrow);
        }
    }

    public virtual void Render(int x, int y, CrcRenderSink sink)
    {
        X = x;
        Y = y;
        if (Visibility != CrcVisibility.Visible)
        {
            return;
        }
        if ((Fill is not null) || (BorderWidth != 0))
        {
            sink.Ops.Add(new CrcQuadOp(BorderArea, Fill, BorderColor, -BorderWidth));
        }
        if (Pick is not null)
        {
            sink.Targets.Add(new CrcPickTarget(PickKey ?? "", BorderArea, this is CrcText, Pick));
        }
    }

    protected void SetSize(int contentWidth, int contentHeight)
    {
        int width = Math.Max(contentWidth, MinWidth ?? 0);
        Width = Margin.Left + BorderWidth + Padding.Left + width + Padding.Right + BorderWidth + Margin.Right;
        Height = Margin.Top + BorderWidth + Padding.Top + contentHeight + Padding.Bottom + BorderWidth + Margin.Bottom;
        GrowWidth = ((MinWidth is { } min) && (min > contentWidth)) ? min - contentWidth : 0;
        GrowHeight = 0;
    }

    protected void Accumulate(int addWidth, int addHeight)
    {
        GrowWidth += addWidth;
        GrowHeight += addHeight;
        Width += addWidth;
        Height += addHeight;
    }

    protected void PlaceFloating(CrcRenderSink sink)
    {
        CrcRect content = Content;
        foreach (CrcNode child in Floating)
        {
            child.Render(
                Align(child.HAlign, content.X, content.Width, child.Width),
                Align(child.VAlign, content.Y, content.Height, child.Height),
                sink
            );
        }
    }

    protected static int Align(CrcAlign align, int start, int space, int size) =>
        align switch
        {
            CrcAlign.Center => start + ((space - size) / 2),
            CrcAlign.End => start + space - size,
            _ => start,
        };
}

/// <summary>CRC's Row: children side by side, width grow on by default.</summary>
internal class CrcRow : CrcNode
{
    private int _contentWidth;

    public CrcRow()
    {
        WidthGrow = 1;
    }

    public override void Calculate()
    {
        base.Calculate();
        List<CrcNode> flow = [.. Flow];
        _contentWidth = flow.Sum(c => c.Width) + ((flow.Count - 1) * Gap);
        SetSize(_contentWidth, (flow.Count > 0) ? flow.Max(c => c.Height) : 0);
    }

    public override void Grow(int addWidth, int addHeight)
    {
        Accumulate(addWidth, addHeight);
        List<CrcNode> flow = [.. Flow];
        int shares = flow.Sum(c => c.WidthGrow);
        int per = (shares > 0) ? GrowWidth / shares : 0;
        foreach (CrcNode child in flow)
        {
            child.Grow(per * child.WidthGrow, (child.HeightGrow > 0) ? Content.Height - child.Height : 0);
        }
        foreach (CrcNode child in Floating)
        {
            child.Grow(0, 0);
        }
    }

    public override void Render(int x, int y, CrcRenderSink sink)
    {
        base.Render(x, y, sink);
        if (Visibility != CrcVisibility.Visible)
        {
            return;
        }
        CrcRect content = Content;
        int next = Align(HAlign, content.X, content.Width, _contentWidth);
        foreach (CrcNode child in Flow)
        {
            child.Render(next, Align(VAlign, content.Y, content.Height, child.Height), sink);
            next += child.Width + Gap;
        }
        PlaceFloating(sink);
    }
}

/// <summary>CRC's Column: children stacked, height grow on by default.</summary>
internal class CrcColumn : CrcNode
{
    private int _contentHeight;

    public CrcColumn()
    {
        HeightGrow = 1;
    }

    public override void Calculate()
    {
        base.Calculate();
        List<CrcNode> flow = [.. Flow];
        _contentHeight = flow.Sum(c => c.Height) + ((flow.Count - 1) * Gap);
        SetSize((flow.Count > 0) ? flow.Max(c => c.Width) : 0, _contentHeight);
    }

    public override void Grow(int addWidth, int addHeight)
    {
        Accumulate(addWidth, addHeight);
        List<CrcNode> flow = [.. Flow];
        int shares = flow.Sum(c => c.HeightGrow);
        int per = (shares > 0) ? GrowHeight / shares : 0;
        foreach (CrcNode child in flow)
        {
            child.Grow((child.WidthGrow > 0) ? Content.Width - child.Width : 0, per * child.HeightGrow);
        }
        foreach (CrcNode child in Floating)
        {
            child.Grow(0, 0);
        }
    }

    public override void Render(int x, int y, CrcRenderSink sink)
    {
        base.Render(x, y, sink);
        if (Visibility != CrcVisibility.Visible)
        {
            return;
        }
        CrcRect content = Content;
        int next = Align(VAlign, content.Y, content.Height, _contentHeight);
        foreach (CrcNode child in Flow)
        {
            child.Render(Align(HAlign, content.X, content.Width, child.Width), next, sink);
            next += child.Height + Gap;
        }
        PlaceFloating(sink);
    }
}

/// <summary>
/// CRC's Text: one line on the character grid with 3 px above and below; its pick area is the circumscription box, the text
/// grown by 2 px each way, which a selected value fills with the emphasis colour.
/// </summary>
internal sealed class CrcText : CrcNode
{
    private CrcRect _circle;

    public CrcText(string text, Color color)
    {
        Text = text;
        Color = color;
        Padding = new CrcEdges(3, 0, 3, 0);
        VAlign = CrcAlign.Center;
    }

    public string Text { get; }
    public Color Color { get; }
    public Color? CircleFill { get; init; }

    public override CrcRect BorderArea => _circle;

    public override void Calculate() => SetSize(CrcGrid.TextWidth(Text), CrcGrid.LineHeight);

    public override void Render(int x, int y, CrcRenderSink sink)
    {
        X = x;
        Y = y;
        CrcRect content = Content;
        int textWidth = CrcGrid.TextWidth(Text);
        int textY = Align(VAlign, content.Y, content.Height, CrcGrid.LineHeight);
        int textX = Align(HAlign, content.X, content.Width, textWidth);
        _circle = new CrcRect(textX - 2, textY - 2, textWidth + 4, CrcGrid.LineHeight + 4);
        if (Visibility != CrcVisibility.Visible)
        {
            return;
        }
        base.Render(x, y, sink);
        if (CircleFill is { } fill)
        {
            sink.Ops.Add(new CrcQuadOp(_circle, fill, null, 1));
        }
        sink.Ops.Add(new CrcTextOp(textX, textY, Text, Color));
    }
}

/// <summary>CRC's Header cell (HeaderElements.Header): a bordered pick-area-grey box with its text centred.</summary>
internal sealed class CrcHeaderCell : CrcNode
{
    private readonly CrcText _text;

    public CrcHeaderCell(string text)
    {
        WidthGrow = 1;
        BorderWidth = 1;
        BorderColor = CrcPalette.Outline;
        Fill = CrcPalette.PickBackground;
        _text = new CrcText(text, CrcPalette.Text) { HAlign = CrcAlign.Center, WidthGrow = 1 };
        Children.Add(_text);
    }

    public override void Calculate()
    {
        base.Calculate();
        SetSize(_text.Width, _text.Height);
    }

    public override void Render(int x, int y, CrcRenderSink sink)
    {
        base.Render(x, y, sink);
        if (Visibility == CrcVisibility.Visible)
        {
            _text.Render(Content.X, Content.Y, sink);
        }
    }
}
