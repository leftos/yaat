using System.ComponentModel;
using System.Reflection;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Tools;

/// <summary>
/// The client actions an agent calls by name over the automation pipe (<c>list_app_tools</c>, <c>call_app_tool</c>): every
/// public method marked <see cref="AutomationToolAttribute"/>, bound to the main window's view model. Every member runs on the
/// UI thread. Each tool names an availability method that says why it cannot run now, or null when it can; the pipe asks it
/// before every call and reports it in the list.
/// </summary>
/// <param name="viewModel">The main window's view model the tools act on.</param>
/// <param name="state">The client actions the pipe shares with <c>wait_until</c>, such as the <c>SIMRATE</c> send.</param>
public sealed partial class AutomationTools(MainViewModel viewModel, IAutomationState state)
{
    /// <summary>The reason every tool gives while the main window is not up.</summary>
    public const string NoMainWindowReason = "The main window is not up.";

    private static readonly Dictionary<Type, string> TypeNames = new()
    {
        [typeof(string)] = "string",
        [typeof(int)] = "int",
        [typeof(double)] = "double",
        [typeof(bool)] = "bool",
    };

    private static readonly Lazy<IReadOnlyList<AppTool>> LazyCatalog = new(Discover);

    private readonly MainViewModel _viewModel = viewModel;
    private readonly IAutomationState _state = state;

    /// <summary>Every app tool, ordered by name.</summary>
    /// <exception cref="InvalidOperationException">A tool's signature breaks the <see cref="AutomationToolAttribute"/> rules.</exception>
    public static IReadOnlyList<AppTool> Catalog => LazyCatalog.Value;

    /// <summary>The protocol name of an allowed parameter type (<c>string</c>, <c>int</c>, <c>double</c>, <c>bool</c>), else null.</summary>
    public static string? TypeName(Type type) => TypeNames.GetValueOrDefault(type);

    /// <summary>Why a tool that needs a server cannot run now, or null.</summary>
    public string? NotConnected() => _viewModel.IsConnected ? null : "Not connected to a server: connect first.";

    /// <summary>Why a tool that needs a room cannot run now, or null.</summary>
    public string? NotInRoom() => (_viewModel.IsConnected && _viewModel.IsInRoom) ? null : "Not in a room: connect, then create or join one.";

    /// <summary>Why a tool that needs a loaded scenario cannot run now, or null.</summary>
    public string? NoScenario() => NotInRoom() ?? (_viewModel.HasScenario ? null : "No scenario is loaded in the room.");

    /// <summary>Why a tool that needs the scenario's video map list cannot run now, or null.</summary>
    public string? MapsNotLoaded() => NoScenario() ?? (_viewModel.Radar.VideoMapsReady ? null : "The video maps are still loading.");

    /// <summary>
    /// Why a tool that frames the primary radar cannot run now, or null: until the radar has restored the scenario's saved settings,
    /// that restore would overwrite a centre or range set now.
    /// </summary>
    public string? RadarNotReady() => NoScenario() ?? (_viewModel.Radar.VideoMapsReady ? null : "The radar is still loading its maps and settings.");

    /// <summary>The client's aircraft with <paramref name="callsign"/>, matched case-insensitively, or null.</summary>
    private AircraftModel? FindAircraft(string callsign) =>
        _viewModel.Aircraft.FirstOrDefault(candidate => string.Equals(candidate.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

    /// <summary>The answer of every tool given a callsign the client has no aircraft for.</summary>
    private static AppToolOutcome NoAircraft(string callsign) => AppToolOutcome.Unavailable($"No aircraft with callsign {callsign} in the client.");

    private static IReadOnlyList<AppTool> Discover()
    {
        List<AppTool> tools = [];
        foreach (MethodInfo method in typeof(AutomationTools).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.GetCustomAttribute<AutomationToolAttribute>() is { } attribute)
            {
                tools.Add(Describe(method, attribute));
            }
        }

        return [.. tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)];
    }

    private static AppTool Describe(MethodInfo method, AutomationToolAttribute attribute)
    {
        if (method.ReturnType != typeof(Task<AppToolOutcome>))
        {
            throw new InvalidOperationException($"App tool {attribute.Name} ({method.Name}) must return Task<AppToolOutcome>.");
        }

        MethodInfo? availability = typeof(AutomationTools).GetMethod(
            attribute.Availability,
            BindingFlags.Instance | BindingFlags.Public,
            Type.EmptyTypes
        );
        if (availability?.ReturnType != typeof(string))
        {
            throw new InvalidOperationException(
                $"App tool {attribute.Name} names the availability method '{attribute.Availability}', which must be a public parameterless "
                    + "AutomationTools method returning string?."
            );
        }

        return new AppTool(
            attribute.Name,
            attribute.Description,
            [.. method.GetParameters().Select(parameter => DescribeParameter(attribute.Name, parameter))],
            method,
            availability
        );
    }

    private static AppToolParameter DescribeParameter(string tool, ParameterInfo parameter)
    {
        string type =
            TypeName(parameter.ParameterType)
            ?? throw new InvalidOperationException(
                $"App tool {tool}: parameter '{parameter.Name}' is a {parameter.ParameterType.Name}; use string, int, double or bool."
            );
        string? description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"App tool {tool}: parameter '{parameter.Name}' has no [Description].");
        }

        return new AppToolParameter(parameter.Name!, type, description);
    }
}

/// <summary>One app tool: its name, description and parameters, the method that runs it, and its availability method.</summary>
public sealed record AppTool(string Name, string Description, IReadOnlyList<AppToolParameter> Parameters, MethodInfo Method, MethodInfo Availability)
{
    /// <summary>Why the tool cannot run now on <paramref name="tools"/>, or null when it can. Call on the UI thread.</summary>
    public string? UnavailableReason(AutomationTools tools) =>
        (string?)Availability.Invoke(tools, BindingFlags.DoNotWrapExceptions, null, null, null);

    /// <summary>Starts the tool on <paramref name="tools"/> with <paramref name="arguments"/> in parameter order. Call on the UI thread.</summary>
    public Task<AppToolOutcome> Invoke(AutomationTools tools, object?[] arguments) =>
        (Task<AppToolOutcome>)Method.Invoke(tools, BindingFlags.DoNotWrapExceptions, null, arguments, null)!;
}

/// <summary>One parameter of an app tool: its argument name, protocol type name, description and CLR type.</summary>
public sealed record AppToolParameter(string Name, string Type, string Description);

/// <summary>
/// How an app tool call ended: it ran, and <see cref="Message"/> says what it did or how the client refused it; or it could not
/// run now, and <see cref="Reason"/> says why.
/// </summary>
public sealed record AppToolOutcome(bool Available, string? Reason, string? Message)
{
    /// <summary>The tool ran; <paramref name="message"/> says what it did, or how the client or the room refused it.</summary>
    public static AppToolOutcome Done(string message) => new(true, null, message);

    /// <summary>The tool cannot run now; <paramref name="reason"/> says why.</summary>
    public static AppToolOutcome Unavailable(string reason) => new(false, reason, null);
}

/// <summary>An app tool argument the tool itself rejects (a range, a missing file); the pipe answers it as <c>INVALID_PARAM</c>.</summary>
/// <param name="parameter">The argument's name.</param>
/// <param name="message">What is wrong with it and what to give instead.</param>
public sealed class AppToolArgumentException(string parameter, string message) : Exception(message)
{
    public string Parameter { get; } = parameter;
}
