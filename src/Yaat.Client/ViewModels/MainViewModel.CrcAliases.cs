using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// CRC alias support: reads the alias files from the user's CRC installation and runs the subset of CRC
/// dot commands that have a YAAT equivalent. Entirely client-side — nothing here reaches the server.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Dot commands YAAT owns. They take precedence over a CRC alias of the same name; the <c>CRC</c>
    /// input prefix reaches the shadowed alias.
    /// </summary>
    private static readonly HashSet<string> BuiltInDotCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ff",
        ".marker",
        ".markers",
        ".nomarkers",
        ".reloadaliases",
    };

    private readonly CrcAliasStore _crcAliases = new();

    /// <summary>Re-reads the alias files, e.g. after the alias directory changes in Settings.</summary>
    public void ReloadCrcAliases() => _ = LoadCrcAliasesAsync();

    /// <summary>
    /// Reloads the alias table off the UI thread. Called at startup and whenever the ARTCC changes, since
    /// the ARTCC determines which alias file CRC pairs with the personal one.
    /// </summary>
    private async Task LoadCrcAliasesAsync()
    {
        var overrideDirectory = _preferences.CrcAliasDirectory;
        var artccId = _preferences.ArtccId;

        try
        {
            var result = await Task.Run(() => _crcAliases.Load(overrideDirectory, artccId, BuiltInDotCommands));
            if (result.AliasCount == 0)
            {
                return;
            }

            var message = $"CRC aliases: {result.AliasCount} loaded from {string.Join(", ", result.FilesLoaded)}";
            if (result.ShadowedByBuiltins.Count > 0)
            {
                message += $" ({result.ShadowedByBuiltins.Count} shadowed by YAAT commands: {string.Join(", ", result.ShadowedByBuiltins)})";
            }

            Dispatcher.UIThread.Post(() => AddSystemEntry(message));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CRC alias load failed");
        }
    }

    /// <summary>
    /// Expands <paramref name="text" /> against the CRC alias table and runs the result.
    /// </summary>
    /// <returns>False when nothing in the text matched an alias, so the caller can report it as unknown.</returns>
    private bool TryHandleCrcAlias(string text)
    {
        if (text.Equals(".reloadaliases", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadCrcAliasesAsync();
            StatusText = "Reloading CRC aliases";
            return true;
        }

        var verb = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (verb is null || !_crcAliases.Contains(verb))
        {
            return false;
        }

        if (!_crcAliases.TryExpand(text, out var expanded, out var expansionError))
        {
            StatusText = $"CRC alias {verb}: {expansionError}";
            return true;
        }

        RunCrcAlias(verb, CrcAliasExecutor.Plan(expanded, BuildCrcAliasContext()));
        return true;
    }

    private void RunCrcAlias(string verb, CrcAliasExecution execution)
    {
        switch (execution.Action)
        {
            case CrcAliasAction.Echo:
                foreach (var line in execution.EchoLines)
                {
                    AddSystemEntry(line);
                }

                return;
            case CrcAliasAction.ScopeMarkers:
                TryHandleScopeMarkerCommand(execution.CommandText);
                return;
            case CrcAliasAction.OpenUrl:
                UrlLauncher.OpenInBrowser(execution.Url);
                StatusText = $"Opened {execution.Url}";
                return;
            case CrcAliasAction.Unsupported:
            case CrcAliasAction.Failed:
                StatusText = $"CRC alias {verb}: {execution.Message}";
                return;
            default:
                _log.LogError("Unhandled CRC alias action {Action} for {Verb}", execution.Action, verb);
                StatusText = $"CRC alias {verb}: unhandled result";
                return;
        }
    }

    /// <summary>
    /// Flight-plan fields the alias variables resolve against. CRC binds these to whichever track the
    /// controller has selected, and YAAT's terminal already selects an aircraft when you type its callsign.
    /// </summary>
    private CrcAliasContext BuildCrcAliasContext()
    {
        var aircraft = SelectedAircraft;
        return aircraft is null ? CrcAliasContext.None : new CrcAliasContext(aircraft.Departure, aircraft.Destination, aircraft.Route);
    }
}
