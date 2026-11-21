namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>
/// A port of CRC's PageScrollManager as the FDB menus use it: seven lines on show (MAX_LINES), the arrows move a whole
/// page, and the top line is clamped to the list every layout (BaseScrollManager.QueryScrollStateChanged).
/// </summary>
internal sealed class CrcPageScroll
{
    public const int MaxLines = 7;

    public int TopLine { get; private set; }

    public int TotalLines { get; private set; }

    public bool ScrollIsEnabled => TotalLines > MaxLines;

    public bool UpIsEnabled => TopLine > 0;

    public bool DownIsEnabled => TopLine + MaxLines < TotalLines;

    /// <summary>Sets the list length and the wanted top line, clamped as CRC's AdjustTopLine clamps it.</summary>
    public void Reset(int totalLines, int topLine)
    {
        TotalLines = totalLines;
        TopLine = Math.Clamp(topLine, 0, Math.Max(0, totalLines - MaxLines));
    }

    /// <summary>CRC's InternalScroll: a page down or up; an arrow that cannot scroll does nothing.</summary>
    public void Scroll(bool down)
    {
        if (down && DownIsEnabled)
        {
            TopLine = Math.Min(TopLine + MaxLines, TotalLines - MaxLines);
        }
        else if (!down && UpIsEnabled)
        {
            TopLine = Math.Max(TopLine - MaxLines, 0);
        }
    }

    /// <summary>The lines on show.</summary>
    public IEnumerable<T> Window<T>(IEnumerable<T> lines) => lines.Skip(TopLine).Take(MaxLines);
}
