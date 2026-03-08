using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Yaat.Client.Models;

namespace Yaat.Client.Views;

internal sealed class TerminalColorizer : DocumentColorizingTransformer
{
    private readonly List<TerminalEntryKind> _lineKinds;

    public TerminalColorizer(List<TerminalEntryKind> lineKinds)
    {
        _lineKinds = lineKinds;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var index = line.LineNumber - 1;
        if (index < 0 || index >= _lineKinds.Count)
        {
            return;
        }

        var brush = GetBrush(_lineKinds[index]);
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
    }

    private static IBrush GetBrush(TerminalEntryKind kind) =>
        kind switch
        {
            TerminalEntryKind.Command => Brushes.White,
            TerminalEntryKind.Response => Brushes.LightGray,
            TerminalEntryKind.System => Brushes.Gray,
            TerminalEntryKind.Say => Brushes.Green,
            TerminalEntryKind.Warning => Brushes.Orange,
            TerminalEntryKind.Error => Brushes.Red,
            TerminalEntryKind.Chat => Brushes.Cyan,
            _ => Brushes.White,
        };
}
