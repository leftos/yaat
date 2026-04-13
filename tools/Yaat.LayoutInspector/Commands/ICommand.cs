namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// One execution mode of the LayoutInspector CLI. Each command reads everything
/// it needs from <see cref="CliOptions"/> and the loaded <see cref="LayoutAnalyzer"/>,
/// writes its output to stdout/stderr, and returns a process exit code.
/// </summary>
public interface ICommand
{
    int Execute(LayoutAnalyzer analyzer, CliOptions options);
}
