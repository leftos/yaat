using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Yaat.Client.Models;

namespace Yaat.Client.Views;

/// <summary>
/// Per-line foreground colorizer for the terminal. Pulls colors from a callback that
/// the host wires to <see cref="UserPreferences.TerminalColors"/>; refresh by calling
/// <see cref="ReloadColors"/> when the user updates the scheme.
/// </summary>
internal sealed class TerminalColorizer : DocumentColorizingTransformer
{
    private readonly List<TerminalEntryKind> _lineKinds;
    private readonly Func<TerminalColorScheme> _schemeProvider;
    private Dictionary<TerminalEntryKind, IBrush> _brushes;

    public TerminalColorizer(List<TerminalEntryKind> lineKinds, Func<TerminalColorScheme> schemeProvider)
    {
        _lineKinds = lineKinds;
        _schemeProvider = schemeProvider;
        _brushes = BuildBrushes(_schemeProvider());
    }

    /// <summary>Rebuild the per-kind brush cache from the latest scheme. Caller invalidates the view.</summary>
    public void ReloadColors() => _brushes = BuildBrushes(_schemeProvider());

    protected override void ColorizeLine(DocumentLine line)
    {
        var index = line.LineNumber - 1;
        if (index < 0 || index >= _lineKinds.Count)
        {
            return;
        }

        var brush = _brushes.TryGetValue(_lineKinds[index], out var b) ? b : Brushes.White;
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
    }

    private static Dictionary<TerminalEntryKind, IBrush> BuildBrushes(TerminalColorScheme scheme) =>
        new()
        {
            [TerminalEntryKind.Command] = Parse(scheme.Command, Brushes.White),
            [TerminalEntryKind.Response] = Parse(scheme.Response, Brushes.LightGray),
            [TerminalEntryKind.System] = Parse(scheme.System, Brushes.Gray),
            [TerminalEntryKind.Say] = Parse(scheme.Say, Brushes.LimeGreen),
            [TerminalEntryKind.PilotSpeech] = Parse(scheme.PilotSpeech, Brushes.LimeGreen),
            [TerminalEntryKind.Warning] = Parse(scheme.Warning, Brushes.Orange),
            [TerminalEntryKind.Error] = Parse(scheme.Error, Brushes.Red),
            [TerminalEntryKind.Chat] = Parse(scheme.Chat, Brushes.Cyan),
        };

    private static IBrush Parse(string hex, IBrush fallback)
    {
        if (Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }
        return fallback;
    }
}
