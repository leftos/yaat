using System.Text.Json;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Implements the --dump mode: serializes the entire airport layout (nodes,
/// taxiways, runways, exits) as indented JSON to stdout. Used for grepping
/// layout state from the command line.
/// </summary>
public sealed class DumpCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        var dump = analyzer.GetFullDump();
        var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
        Console.Write(json);
        return 0;
    }
}
