using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The <c>.view</c> dot command: client-only, never sent to the server. It opens or closes a view of the open YAAT Scope;
/// the Scope's VIEWS toggles and its cards' close buttons run the same command, so every path echoes alike.
/// </summary>
public partial class MainViewModel
{
    private const string ViewUsage = "Usage: .view <ssa|ca> <on|off>";

    /// <summary>Runs a <c>.view</c> command from the Scope; nothing is added to the command history.</summary>
    public void RunViewCommand(string text) => TryHandleViewCommand(text);

    /// <summary>
    /// Handles <c>.view &lt;ssa|ca&gt; &lt;on|off&gt;</c> against <see cref="OpenScope"/>, writing the echo or the error to the
    /// status line.
    /// </summary>
    /// <returns>False when the first token is not <c>.view</c>, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleViewCommand(string text)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length == 0) || !string.Equals(parts[0], ".view", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StatusText = RunViewVerb(parts);
        return true;
    }

    /// <summary>Sets the view <paramref name="parts"/> names to the state it names, and returns the status text.</summary>
    private string RunViewVerb(string[] parts)
    {
        if (OpenScope is not { } scope)
        {
            return "No YAAT Scope is open";
        }

        if (parts.Length != 3)
        {
            return ViewUsage;
        }

        string name = parts[1].ToLowerInvariant();
        bool? open = parts[2].ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
        if (!ScopeViewModel.IsViewName(name) || (open is not { } isOpen))
        {
            return ViewUsage;
        }

        scope.SetViewOpen(name, isOpen);
        return ScopeToolbarButtons.ViewCommand(name, isOpen);
    }
}
