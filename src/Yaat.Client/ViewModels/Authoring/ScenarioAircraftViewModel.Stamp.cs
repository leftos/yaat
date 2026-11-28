using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The Aircraft tab's side of Apply Route: the selected aircraft as the document holds it, and the one-edit write of a stamp.</summary>
public sealed partial class ScenarioAircraftViewModel
{
    /// <summary>The undo description of an Apply Route write.</summary>
    public const string ApplyRouteStep = "Apply Route";

    /// <summary>The selected aircraft as the document holds it now, with its index in the document.</summary>
    /// <returns>The index and the aircraft; null with nothing selected or the selected aircraft gone from the document.</returns>
    public (int Index, ScenarioAircraft Aircraft)? SelectedForStamp() =>
        (SelectedDocumentIndex(ApplyRouteStep) is { } index) ? (index, _session.Document.Scenario.Aircraft[index]) : null;

    /// <summary>Every callsign in the scenario but that of the aircraft at <paramref name="documentIndex"/>, ignoring case.</summary>
    /// <param name="documentIndex">The index of the aircraft being stamped.</param>
    /// <returns>The other aircraft's callsigns.</returns>
    public IReadOnlySet<string> CallsignsExcept(int documentIndex)
    {
        List<ScenarioAircraft> aircraft = _session.Document.Scenario.Aircraft;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < aircraft.Count; i++)
        {
            if (i != documentIndex)
            {
                taken.Add(aircraft[i].AircraftId);
            }
        }

        return taken;
    }

    /// <summary>
    /// Replaces the aircraft at <paramref name="documentIndex"/> with its stamped copy as one undo step "Apply Route", the rows
    /// rebuilt and <see cref="Edited"/> raised; nothing when that index no longer holds the aircraft the copy was made from.
    /// </summary>
    /// <param name="documentIndex">The aircraft's index in the document.</param>
    /// <param name="stamped">The stamped copy, with the same aircraft id as the one it replaces.</param>
    public void ApplyStamp(int documentIndex, ScenarioAircraft stamped)
    {
        ArgumentNullException.ThrowIfNull(stamped);
        List<ScenarioAircraft> aircraft = _session.Document.Scenario.Aircraft;
        if (
            (documentIndex < 0)
            || (documentIndex >= aircraft.Count)
            || !string.Equals(aircraft[documentIndex].Id, stamped.Id, StringComparison.Ordinal)
        )
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("Scenario editor: Apply Route skipped: aircraft {Id} is not at index {Index}", stamped.Id, documentIndex);
            }

            return;
        }

        Apply(ApplyRouteStep, s => s.Aircraft[documentIndex] = stamped, stamped.Id);
    }
}
