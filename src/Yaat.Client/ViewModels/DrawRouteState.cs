using System.Diagnostics.CodeAnalysis;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One view's draw-route mode: the aircraft a route is being drawn for, the waypoints placed so far and the condition at each,
/// and the <c>DCTF</c> command a confirm sends. Each view model owns its own; <see cref="Changed"/> fires after every change.
/// </summary>
/// <param name="fixes">The fixes a placed point is named from by FRD; null while none are loaded.</param>
/// <param name="send">Sends a command for a callsign with the user's initials: (callsign, command, initials).</param>
public sealed class DrawRouteState(Func<IReadOnlyList<(string Name, double Lat, double Lon)>?> fixes, Func<string, string, string, Task> send)
{
    private readonly List<DrawnWaypoint> _waypoints = [];
    private readonly Dictionary<int, WaypointCondition> _conditions = [];

    /// <summary>Fired after every change to the mode, the waypoints or the conditions.</summary>
    public event Action? Changed;

    /// <summary>Whether a route is being drawn.</summary>
    public bool IsDrawing { get; private set; }

    /// <summary>The callsign the route is drawn for, or null while none is.</summary>
    public string? Callsign { get; private set; }

    /// <summary>The waypoints placed so far, a copy never changed after it is set; null while there are none.</summary>
    public IReadOnlyList<DrawnWaypoint>? Waypoints { get; private set; }

    /// <summary>The condition at each waypoint by index, a copy never changed after it is set; null while there are none.</summary>
    public IReadOnlyDictionary<int, WaypointCondition>? ConditionsSnapshot { get; private set; }

    /// <summary>Starts drawing a route for <paramref name="callsign"/>, dropping any route in progress.</summary>
    public void Enter(string callsign)
    {
        Callsign = callsign;
        _waypoints.Clear();
        _conditions.Clear();
        IsDrawing = true;
        Publish();
    }

    /// <summary>Places a waypoint at the point, named by FRD from the fixes, else <c>lat,lon</c> to three decimals.</summary>
    public void Place(double lat, double lon)
    {
        if (!IsDrawing)
        {
            return;
        }

        string name = ScopeMapSource.ToFrd(fixes(), lat, lon) ?? $"{lat:F3},{lon:F3}";
        _waypoints.Add(new DrawnWaypoint(name, lat, lon));
        Publish();
    }

    /// <summary>Removes the last waypoint and its condition.</summary>
    public void Undo()
    {
        if (!IsDrawing || (_waypoints.Count == 0))
        {
            return;
        }

        int lastIdx = _waypoints.Count - 1;
        _conditions.Remove(lastIdx);
        _waypoints.RemoveAt(lastIdx);
        Publish();
    }

    /// <summary>Removes the waypoint at <paramref name="index"/> and its condition, moving each later condition down one.</summary>
    public void RemoveAt(int index)
    {
        if (!IsDrawing || (index < 0) || (index >= _waypoints.Count))
        {
            return;
        }

        var shifted = new Dictionary<int, WaypointCondition>();
        foreach ((int idx, WaypointCondition cond) in _conditions)
        {
            if (idx != index)
            {
                shifted[(idx < index) ? idx : (idx - 1)] = cond;
            }
        }

        _conditions.Clear();
        foreach ((int idx, WaypointCondition cond) in shifted)
        {
            _conditions[idx] = cond;
        }

        _waypoints.RemoveAt(index);
        Publish();
    }

    /// <summary>Removes every waypoint after <paramref name="index"/> and their conditions.</summary>
    public void RemoveAfter(int index)
    {
        if (!IsDrawing || (index < 0) || (index >= (_waypoints.Count - 1)))
        {
            return;
        }

        for (int i = _waypoints.Count - 1; i > index; i--)
        {
            _conditions.Remove(i);
            _waypoints.RemoveAt(i);
        }

        Publish();
    }

    /// <summary>Leaves the mode, dropping the route.</summary>
    public void Cancel()
    {
        Callsign = null;
        _waypoints.Clear();
        _conditions.Clear();
        IsDrawing = false;
        Publish();
    }

    /// <summary>Sets the condition at <paramref name="index"/>; with neither an altitude nor commands, removes it.</summary>
    public void SetCondition(int index, string? altitude, string? commands)
    {
        if (string.IsNullOrWhiteSpace(altitude) && string.IsNullOrWhiteSpace(commands))
        {
            _conditions.Remove(index);
        }
        else
        {
            _conditions[index] = new WaypointCondition(altitude?.Trim(), commands?.Trim());
        }

        Publish();
    }

    /// <summary>The condition at <paramref name="index"/>, or null.</summary>
    public WaypointCondition? GetCondition(int index) => _conditions.GetValueOrDefault(index);

    /// <summary>
    /// Sends the route as one <c>DCTF</c> command for the drawn callsign and leaves the mode; with no waypoints it only leaves the
    /// mode.
    /// </summary>
    public async Task ConfirmAsync(string initials)
    {
        if (!IsDrawing || (Callsign is null) || (_waypoints.Count == 0))
        {
            Cancel();
            return;
        }

        string command = BuildCommand();
        string callsign = Callsign;
        Cancel();
        await send(callsign, command, initials);
    }

    /// <summary>
    /// The route as a command: <c>DCTF</c> through every waypoint, a waypoint with only an altitude carrying it inline
    /// (<c>FIX/080</c>), then an <c>AT FIX</c> block for each waypoint with commands (its altitude as <c>CFIX</c>), joined by
    /// <c>;</c>.
    /// </summary>
    public string BuildCommand()
    {
        var fixTokens = new List<string>();
        var atBlocks = new List<string>();
        for (int i = 0; i < _waypoints.Count; i++)
        {
            string fixName = _waypoints[i].ResolvedName;
            WaypointCondition? condition = _conditions.GetValueOrDefault(i);
            if (IsAltitudeOnly(condition))
            {
                fixTokens.Add($"{fixName}/{condition.Altitude}");
                continue;
            }

            fixTokens.Add(fixName);
            if (AtBlockFor(fixName, condition) is { } block)
            {
                atBlocks.Add(block);
            }
        }

        var parts = new List<string> { $"DCTF {string.Join(" ", fixTokens)}" };
        parts.AddRange(atBlocks);
        return string.Join(";", parts);
    }

    private static bool IsAltitudeOnly([NotNullWhen(true)] WaypointCondition? condition) =>
        (condition is not null) && !string.IsNullOrWhiteSpace(condition.Altitude) && string.IsNullOrWhiteSpace(condition.Commands);

    // The AT block a waypoint's condition sends after the DCTF: its altitude as CFIX, then its commands; null for none.
    private static string? AtBlockFor(string fixName, WaypointCondition? condition)
    {
        if (condition is null)
        {
            return null;
        }

        var condParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(condition.Altitude))
        {
            condParts.Add($"CFIX {condition.Altitude}");
        }

        if (!string.IsNullOrWhiteSpace(condition.Commands))
        {
            condParts.Add(condition.Commands);
        }

        return (condParts.Count > 0) ? $"AT {fixName} {string.Join(",", condParts)}" : null;
    }

    private void Publish()
    {
        Waypoints = (_waypoints.Count > 0) ? [.. _waypoints] : null;
        ConditionsSnapshot = (_conditions.Count > 0) ? new Dictionary<int, WaypointCondition>(_conditions) : null;
        Changed?.Invoke();
    }
}

/// <summary>
/// Condition applied to a drawn route waypoint (crossing altitude and/or AT commands).
/// </summary>
public record WaypointCondition(string? Altitude, string? Commands)
{
    /// <summary>
    /// Returns a compact summary for rendering on the map (e.g. "A100 SPD250").
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(Altitude))
        {
            parts.Add(Altitude);
        }

        if (!string.IsNullOrWhiteSpace(Commands))
        {
            parts.Add(Commands.Replace(" ", ""));
        }

        return string.Join(" ", parts);
    }
}
