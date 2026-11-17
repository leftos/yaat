using SkiaSharp;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>A speech bubble placed under one track: its wrapped lines, the rect it fills and the severity it colours by.</summary>
/// <param name="Callsign">The aircraft the bubble belongs to.</param>
/// <param name="Rect">The rect the bubble's fill, border and text draw in, in canvas coordinates.</param>
/// <param name="Lines">The text's wrapped lines, at most <see cref="ScopeBubbles.MaxLines"/> of them.</param>
/// <param name="Severity">The colour the bubble draws in: green for a SAY / pilot transmission, amber for a warning.</param>
public sealed record ScopeBubble(string Callsign, SKRect Rect, IReadOnlyList<string> Lines, SpeechBubbleSeverity Severity);

/// <summary>The preferences a frame's speech bubbles are placed and surfaced under.</summary>
/// <param name="Show">Whether speech bubbles are drawn at all.</param>
/// <param name="AlwaysShowGround">Whether a ground aircraft's bubble surfaces the aircraft on every scope view.</param>
public readonly record struct ScopeBubbleOptions(bool Show, bool AlwaysShowGround);

/// <summary>
/// Places the speech bubbles a frame draws: each aircraft's active bubble, wrapped and laid below the block it belongs to (at
/// the symbol when the block is hidden), measured on the UI thread with the frame. The canvas places them again from its current
/// frame with the same inputs, so its hit test matches the rects the renderer fills.
/// </summary>
public static class ScopeBubbles
{
    /// <summary>How many characters of text a bubble's line carries before its wrap moves on.</summary>
    public const int MaxLineChars = 36;

    /// <summary>How many lines a bubble shows before the rest of its text is dropped behind an ellipsis.</summary>
    public const int MaxLines = 3;

    /// <summary>The padding between a bubble's rect and its text, on every side.</summary>
    public const float Pad = 4f;

    /// <summary>The gap between the anchor's bottom and the bubble's top.</summary>
    public const float Gap = 4f;

    // A text line's height above the font size, and what the last line's padding gives back below its baseline.
    private const float LineExtraPx = 2f;
    private const float RectBottomTrimPx = 2f;

    /// <summary>How much vertical room one wrapped line takes in a bubble measured with <paramref name="font"/>.</summary>
    public static float LineHeight(SKFont font) => font.Size + LineExtraPx;

    /// <summary>
    /// Every active bubble <paramref name="tracks"/> draw, in track order; none when <paramref name="options"/> turns speech
    /// bubbles off, and none for a track whose bubble has expired.
    /// </summary>
    public static IReadOnlyList<ScopeBubble> Place(IReadOnlyList<ScopeTrack> tracks, ScopeBubbleOptions options, DateTime nowUtc, SKFont font)
    {
        if (!options.Show)
        {
            return [];
        }

        var bubbles = new List<ScopeBubble>();
        foreach (ScopeTrack track in tracks)
        {
            if (BubbleFor(track, nowUtc, font) is { } bubble)
            {
                bubbles.Add(bubble);
            }
        }

        return bubbles;
    }

    /// <summary>
    /// The rect a track's bubble hangs from: the block's bounds, or a box around the symbol when the block draws nothing, so a
    /// hidden block keeps its bubble at the target.
    /// </summary>
    public static SKRect BubbleAnchor(ScopeTrack track) => (track.Block.Fields.Count > 0) ? track.Block.Bounds : SymbolBounds(track.Symbol);

    /// <summary>
    /// <paramref name="text"/> split into a bubble's lines: words packed to <see cref="MaxLineChars"/>, a word longer than that
    /// broken into <see cref="MaxLineChars"/>-character chunks that each take their own line after the pending one, at most
    /// <see cref="MaxLines"/> lines, the last one ending in an ellipsis when any text went unplaced.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text)
    {
        var lines = new List<string>();
        string? current = null;
        bool overflowed = false;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (lines.Count >= MaxLines)
            {
                overflowed = true;
                break;
            }

            if (word.Length > MaxLineChars)
            {
                if (AddLongWord(lines, word, ref current))
                {
                    overflowed = true;
                }

                continue;
            }

            if ((current is not null) && ((current.Length + 1 + word.Length) > MaxLineChars))
            {
                AddLine(lines, ref current);
            }

            if (current is null)
            {
                current = word;
                continue;
            }

            current = current + " " + word;
        }

        return Finish(lines, current, overflowed);
    }

    private static ScopeBubble? BubbleFor(ScopeTrack track, DateTime nowUtc, SKFont font)
    {
        if ((track.Aircraft.SpeechBubble is not { } bubble) || (bubble.ExpiresAt <= nowUtc))
        {
            return null;
        }

        IReadOnlyList<string> lines = Wrap(bubble.Text);
        if (lines.Count == 0)
        {
            return null;
        }

        return new ScopeBubble(track.Aircraft.Callsign, RectFor(BubbleAnchor(track), lines, font), lines, bubble.Severity);
    }

    private static SKRect RectFor(SKRect anchor, IReadOnlyList<string> lines, SKFont font)
    {
        float widest = 0f;
        foreach (string line in lines)
        {
            widest = Math.Max(widest, font.MeasureText(line));
        }

        float left = anchor.Left;
        float top = anchor.Bottom + Gap;
        float height = (lines.Count * LineHeight(font)) + (2 * Pad) - RectBottomTrimPx;
        return new SKRect(left, top, left + widest + (2 * Pad), top + height);
    }

    private static SKRect SymbolBounds(SKPoint symbol) =>
        new(
            symbol.X - ScopeRenderer.SymbolRadius,
            symbol.Y - ScopeRenderer.SymbolRadius,
            symbol.X + ScopeRenderer.SymbolRadius,
            symbol.Y + ScopeRenderer.SymbolRadius
        );

    private static void AddLine(List<string> lines, ref string? current)
    {
        if (current is null)
        {
            return;
        }

        lines.Add(current);
        current = null;
    }

    // Breaks a word longer than a line into full-width chunks, each its own line, after the pending line is flushed. Returns
    // whether any of the word's text went unplaced.
    private static bool AddLongWord(List<string> lines, string word, ref string? current)
    {
        AddLine(lines, ref current);
        for (int offset = 0; offset < word.Length; offset += MaxLineChars)
        {
            if (lines.Count >= MaxLines)
            {
                return true;
            }

            lines.Add(word.Substring(offset, Math.Min(MaxLineChars, word.Length - offset)));
        }

        return false;
    }

    // Places the line still pending, and marks the last line with an ellipsis when any text went unplaced.
    private static IReadOnlyList<string> Finish(List<string> lines, string? current, bool overflowed)
    {
        bool leftOver = overflowed;
        if (current is not null)
        {
            if (lines.Count < MaxLines)
            {
                lines.Add(current);
            }
            else
            {
                leftOver = true;
            }
        }

        return leftOver ? Ellipsize(lines) : lines;
    }

    // The last of a full set of lines closes with an ellipsis, cut to leave room for it.
    private static IReadOnlyList<string> Ellipsize(List<string> lines)
    {
        string last = lines[^1];
        if (last.Length >= (MaxLineChars - 1))
        {
            last = last[..(MaxLineChars - 1)];
        }

        lines[^1] = last + "…";
        return lines;
    }
}
