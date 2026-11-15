using System.Globalization;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The <c>.scope</c> dot commands: client-only, never sent to the server. They act on the open YAAT Scope; its toolbar
/// runs the same commands, so a typed command and a button click change the scope and echo in the status line alike.
/// </summary>
public partial class MainViewModel
{
    private const string ScopeUsage = "Usage: .scope range <1-256> | .scope template <stars|eram|mixed|euroscope>";

    private const string ScopeRangeUsage = "Usage: .scope range <1-256>";

    /// <summary>The open YAAT Scope's view model, or null when no Scope window is open.</summary>
    public ScopeViewModel? OpenScope { get; set; }

    /// <summary>Runs a <c>.scope</c> command from the Scope's toolbar; nothing is added to the command history.</summary>
    public void RunScopeCommand(string text) => TryHandleScopeCommand(text);

    /// <summary>
    /// Handles <c>.scope range &lt;1-256&gt;</c> and <c>.scope template &lt;id&gt;</c> against <see cref="OpenScope"/>,
    /// writing the echo or the error to the status line.
    /// </summary>
    /// <returns>False when the first token is not <c>.scope</c>, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleScopeCommand(string text)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length == 0) || !string.Equals(parts[0], ".scope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StatusText = RunScopeVerb(parts);
        return true;
    }

    /// <summary>Runs the verb in <paramref name="parts"/>[1] and returns the status text.</summary>
    private string RunScopeVerb(string[] parts)
    {
        string verb = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
        return verb switch
        {
            "RANGE" => OpenScope is { } rangeScope ? SetScopeRange(rangeScope, parts) : "No YAAT Scope is open",
            "TEMPLATE" => OpenScope is { } templateScope ? PickScopeTemplate(templateScope, parts) : "No YAAT Scope is open",
            _ => ScopeUsage,
        };
    }

    private static string SetScopeRange(ScopeViewModel scope, string[] parts)
    {
        if (
            (parts.Length != 3)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int rangeNm)
            || (rangeNm is < 1 or > 256)
        )
        {
            return ScopeRangeUsage;
        }

        scope.RangeNm = rangeNm;
        return ScopeToolbarButtons.RangeCommand(rangeNm);
    }

    private static string PickScopeTemplate(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 3)
        {
            return ScopeUsage;
        }

        string id = parts[2].ToLowerInvariant();
        if (!ScopeTemplates.IsSelectable(id))
        {
            return $"Unknown scope template: {parts[2]}";
        }

        scope.PickTemplateCommand.Execute(id);
        return ScopeToolbarButtons.TemplateCommand(id);
    }
}
