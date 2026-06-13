using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The controller's current operating TCP (the server's persistent "active position"), shown as a
/// dropdown in the terminal input bar. Selecting a TCP runs <c>AS [TCP]</c>. The selection mirrors
/// the server: it follows a standalone <c>AS [TCP]</c> (delivered by the <c>PositionDisplayChanged</c>
/// broadcast) but NOT a one-shot <c>AS [TCP] [command]</c>, which never changes the active position.
/// The option list is the online controllers' TCPs plus the current one. The default is the scenario's
/// student position, delivered in the scenario bootstrap's <c>PositionDisplayConfig.TcpCode</c>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The TCP the controller is currently operating as (e.g. "3Y"); null before a scenario loads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActiveTcpSelector))]
    private string? _activeTcp;

    /// <summary>TCP codes selectable in the active-position dropdown: online controllers + the current TCP.</summary>
    public ObservableCollection<string> ActiveTcpOptions { get; } = [];

    /// <summary>Suppresses the AS command when <see cref="ActiveTcp"/> is updated from the server, not the user.</summary>
    private bool _suppressActiveTcpCommand;

    /// <summary>Whether the active-position dropdown is shown (only once a scenario has supplied a TCP).</summary>
    public bool ShowActiveTcpSelector => !string.IsNullOrEmpty(ActiveTcp);

    /// <summary>
    /// Sets the current operating TCP from the server (bootstrap seed or <c>PositionDisplayChanged</c>)
    /// without issuing an <c>AS</c> command, then refreshes the option list so the value is selectable.
    /// </summary>
    internal void SetActiveTcpFromServer(string? tcp)
    {
        _suppressActiveTcpCommand = true;
        try
        {
            ActiveTcp = string.IsNullOrEmpty(tcp) ? null : tcp;
        }
        finally
        {
            _suppressActiveTcpCommand = false;
        }

        RebuildActiveTcpOptions();
    }

    /// <summary>
    /// Reconciles <see cref="ActiveTcpOptions"/> with the online controllers' TCPs plus the current TCP.
    /// Updates incrementally (the current TCP is always retained), so the ComboBox selection is never
    /// transiently cleared by a list reset.
    /// </summary>
    private void RebuildActiveTcpOptions()
    {
        var desired = new List<string>();
        if (!string.IsNullOrEmpty(ActiveTcp))
        {
            desired.Add(ActiveTcp);
        }

        var others = OnlineControllers
            .Select(c => c.Tcp)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Where(t => !string.Equals(t, ActiveTcp, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        desired.AddRange(others);

        for (int i = ActiveTcpOptions.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(ActiveTcpOptions[i], StringComparer.OrdinalIgnoreCase))
            {
                ActiveTcpOptions.RemoveAt(i);
            }
        }

        foreach (var tcp in desired)
        {
            if (!ActiveTcpOptions.Contains(tcp, StringComparer.OrdinalIgnoreCase))
            {
                ActiveTcpOptions.Add(tcp);
            }
        }
    }

    partial void OnActiveTcpChanged(string? value)
    {
        // A user pick from the dropdown switches the active position. Server-originated updates set
        // _suppressActiveTcpCommand first so they don't echo an AS back to the server.
        if (_suppressActiveTcpCommand || string.IsNullOrEmpty(value))
        {
            return;
        }

        _ = SetActivePositionFromDropdownAsync(value);
    }

    private async Task SetActivePositionFromDropdownAsync(string tcp)
    {
        try
        {
            await _connection.SendCommandAsync("", $"AS {tcp}", _preferences.UserInitials);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Active position change failed");
            StatusText = $"AS error: {ex.Message}";
        }
    }
}
