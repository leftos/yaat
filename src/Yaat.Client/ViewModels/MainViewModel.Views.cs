using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The <c>.view</c> dot command: client-only, never sent to the server. It opens or closes a view of the open YAAT Scope,
/// floats it at a corner of the scope or returns it to the dock; the Scope's VIEWS toggles, its cards' close and dock
/// buttons and a card's move run the same command, so every path echoes alike.
/// </summary>
public partial class MainViewModel
{
    private const string ViewUsage = "Usage: .view <ssa|ca|rpoq|crr> <on|off|dock> | .view <ssa|ca|rpoq|crr> tear <tl|tr|bl|br> <dx> <dy>";

    /// <summary>
    /// Runs a <c>.view</c> command against <see cref="OpenScope"/>, as typed; nothing is added to the command history. Test
    /// seam: the app's own paths name their Scope.
    /// </summary>
    internal void RunViewCommand(string text) => RunViewCommand(OpenScope, text);

    /// <summary>
    /// Runs a <c>.view</c> command from <paramref name="scope"/>'s toolbar or cards against that Scope, whichever Scope has
    /// the focus; nothing is added to the command history.
    /// </summary>
    public void RunViewCommand(ScopeViewModel scope, string text) => TryHandleViewCommand(text, new CommandLineSink(this), scope);

    /// <summary>
    /// Handles <c>.view &lt;ssa|ca|rpoq|crr&gt; &lt;on|off|dock&gt;</c> and <c>.view &lt;ssa|ca|rpoq|crr&gt; tear &lt;corner&gt;
    /// &lt;dx&gt; &lt;dy&gt;</c> against <paramref name="scope"/> (<see cref="OpenScope"/> for the typed line), reporting the
    /// echo or the error to <paramref name="sink"/>.
    /// </summary>
    /// <returns>False when the first token is not <c>.view</c>, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleViewCommand(string text, ICommandTextSink sink, ScopeViewModel scope)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length == 0) || !string.Equals(parts[0], ".view", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sink.ReportStatus(RunViewVerb(scope, parts));
        return true;
    }

    /// <summary>Applies the <c>.view</c> command in <paramref name="parts"/> to its view on <paramref name="scope"/>; returns the status.</summary>
    private static string RunViewVerb(ScopeViewModel scope, string[] parts)
    {
        string name = (parts.Length >= 3) ? parts[1].ToLowerInvariant() : "";
        return ScopeViewModel.IsViewName(name) ? RunViewAction(scope, name, parts) : ViewUsage;
    }

    /// <summary>The action <c>parts[2]</c> on the view named <paramref name="name"/>; an error changes nothing.</summary>
    private static string RunViewAction(ScopeViewModel scope, string name, string[] parts)
    {
        string action = parts[2].ToLowerInvariant();
        if (action == "tear")
        {
            return TearView(scope, name, parts);
        }

        if (parts.Length != 3)
        {
            return ViewUsage;
        }

        switch (action)
        {
            case "on":
            case "off":
                bool open = action == "on";
                scope.SetViewOpen(name, open);
                return ScopeToolbarButtons.ViewCommand(name, open);
            case "dock":
                scope.SetViewPlace(name, null);
                return ScopeToolbarButtons.ViewDockCommand(name);
            default:
                return ViewUsage;
        }
    }

    /// <summary><c>.view &lt;name&gt; tear tl|tr|bl|br &lt;dx&gt; &lt;dy&gt;</c>: opens the view if closed and floats it there.</summary>
    private static string TearView(ScopeViewModel scope, string name, string[] parts)
    {
        if (!TryParsePlacement(parts, 3, out ScopePlacement? place))
        {
            return ViewUsage;
        }

        scope.SetViewPlace(name, place);
        return ScopeToolbarButtons.ViewTearCommand(name, place);
    }
}
