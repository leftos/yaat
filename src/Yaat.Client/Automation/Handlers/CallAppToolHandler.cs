using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tools;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>call_app_tool</c>: binds <c>arguments</c> to the named app tool's parameters by name, then, on the UI thread, runs the tool
/// when its availability check passes and waits for it to finish. An unknown tool, a missing or unknown argument, a value of the
/// wrong JSON kind, or an argument the tool itself rejects is <c>INVALID_PARAM</c> naming the culprit; a tool that cannot run now
/// (including while the main window is not up) answers <c>available: false</c> with its reason. Absent or null <c>arguments</c>
/// is an empty object.
/// </summary>
/// <param name="toolsProvider">The tools bound to the main window's view model, or null while it is not up; read on the UI thread.</param>
public sealed class CallAppToolHandler(Func<AutomationTools?> toolsProvider) : IRequestHandler
{
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    /// <summary>A tool and its arguments in parameter order, ready to run.</summary>
    private sealed record BoundCall(AppTool Tool, object?[] Arguments);

    public string Method => ProtocolMethods.CallAppTool;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object bound = Bind(request.Params);
        if (bound is not BoundCall call)
        {
            return bound;
        }

        AppToolOutcome outcome;
        try
        {
            outcome = await Dispatcher.UIThread.InvokeAsync<AppToolOutcome>(StartCall).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppToolArgumentException ex)
        {
            return HandlerResult.InvalidParam(ex.Parameter, ex.Message);
        }

        return new CallAppToolResult(outcome.Available, outcome.Reason, outcome.Message);

        Task<AppToolOutcome> StartCall() => Start(call);
    }

    /// <summary>Runs on the UI thread: the tool's own task when it can run now, else its unavailable outcome.</summary>
    private Task<AppToolOutcome> Start(BoundCall call)
    {
        AutomationTools? tools = toolsProvider();
        if (tools is null)
        {
            return Task.FromResult(AppToolOutcome.Unavailable(AutomationTools.NoMainWindowReason));
        }

        string? reason = call.Tool.UnavailableReason(tools);
        return (reason is null) ? call.Tool.Invoke(tools, call.Arguments) : Task.FromResult(AppToolOutcome.Unavailable(reason));
    }

    /// <summary>The <see cref="BoundCall"/> the params name, or the <see cref="HandlerErrorResult"/> for their first fault.</summary>
    private static object Bind(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (string name, HandlerErrorResult? toolError) = InputParams.ReadRequiredString(element, "tool");
        if (toolError is not null)
        {
            return toolError;
        }

        AppTool? tool = AutomationTools.Catalog.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            string known = string.Join(", ", AutomationTools.Catalog.Select(candidate => candidate.Name));
            return HandlerResult.InvalidParam(
                "tool",
                $"Unknown app tool '{name}'. {ProtocolMethods.ListAppTools} lists this client's tools: {known}."
            );
        }

        (JsonElement arguments, HandlerErrorResult? argumentsError) = ReadArguments(element);
        return argumentsError ?? BindArguments(tool, arguments);
    }

    private static (JsonElement Arguments, HandlerErrorResult? Error) ReadArguments(JsonElement element)
    {
        if (!element.TryGetProperty("arguments", out JsonElement arguments) || (arguments.ValueKind == JsonValueKind.Null))
        {
            return (EmptyObject, null);
        }

        return (arguments.ValueKind == JsonValueKind.Object)
            ? (arguments, null)
            : (default, HandlerResult.InvalidParam("arguments", "'arguments' must be a JSON object of the tool's arguments by name."));
    }

    private static object BindArguments(AppTool tool, JsonElement arguments)
    {
        HandlerErrorResult? unknown = FindUnknownArgument(tool, arguments);
        if (unknown is not null)
        {
            return unknown;
        }

        object?[] values = new object?[tool.Parameters.Count];
        for (int index = 0; index < tool.Parameters.Count; index++)
        {
            (object? value, HandlerErrorResult? error) = ReadArgument(tool, tool.Parameters[index], arguments);
            if (error is not null)
            {
                return error;
            }

            values[index] = value;
        }

        return new BoundCall(tool, values);
    }

    private static HandlerErrorResult? FindUnknownArgument(AppTool tool, JsonElement arguments)
    {
        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (!tool.Parameters.Any(parameter => string.Equals(parameter.Name, property.Name, StringComparison.Ordinal)))
            {
                return HandlerResult.InvalidParam(
                    property.Name,
                    $"'{tool.Name}' takes no argument '{property.Name}'; its arguments are {ParameterList(tool)}."
                );
            }
        }

        return null;
    }

    private static (object? Value, HandlerErrorResult? Error) ReadArgument(AppTool tool, AppToolParameter parameter, JsonElement arguments)
    {
        if (!arguments.TryGetProperty(parameter.Name, out JsonElement value))
        {
            return (null, HandlerResult.InvalidParam(parameter.Name, $"'{tool.Name}' needs the argument '{parameter.Name}' ({parameter.Type})."));
        }

        object? converted = Convert(value, parameter.Type);
        if (converted is null)
        {
            string message = $"Argument '{parameter.Name}' of '{tool.Name}' must be {Expected(parameter.Type, value)}, not {value.GetRawText()}.";
            return (null, HandlerResult.InvalidParam(parameter.Name, message));
        }

        return (converted, null);
    }

    private static string ParameterList(AppTool tool) =>
        (tool.Parameters.Count == 0) ? "none" : string.Join(", ", tool.Parameters.Select(parameter => $"'{parameter.Name}' ({parameter.Type})"));

    /// <summary>The value of <paramref name="value"/> as the protocol <paramref name="type"/>, or null when its JSON kind does not fit.</summary>
    private static object? Convert(JsonElement value, string type) =>
        type switch
        {
            "string" => (value.ValueKind == JsonValueKind.String) ? value.GetString() : null,
            "int" => ReadInt(value),
            "double" => ((value.ValueKind == JsonValueKind.Number) && value.TryGetDouble(out double number)) ? number : null,
            "bool" => value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            },
            _ => throw new UnreachableException($"App tool parameter type '{type}' has no JSON binding."),
        };

    /// <summary>A JSON number with a whole value that fits <see cref="int"/>, however written (<c>2</c>, <c>2.0</c>, <c>2e0</c>); else null.</summary>
    private static object? ReadInt(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt32(out int whole))
        {
            return whole;
        }

        return (WholeValue(value) is { } number && (number >= int.MinValue) && (number <= int.MaxValue)) ? (int)number : null;
    }

    /// <summary>The value of a JSON number with no fractional part, else null.</summary>
    private static double? WholeValue(JsonElement value) =>
        (
            (value.ValueKind == JsonValueKind.Number)
            && value.TryGetDouble(out double number)
            && double.IsFinite(number)
            && (Math.Floor(number) == number)
        )
            ? number
            : null;

    private static string Expected(string type, JsonElement value) =>
        type switch
        {
            "string" => "a JSON string",
            "int" when WholeValue(value) is not null => IntRange,
            "int" => "an int (a whole JSON number)",
            "double" => "a double (a JSON number)",
            _ => "a bool (true or false)",
        };

    private static readonly string IntRange = string.Create(CultureInfo.InvariantCulture, $"a whole number from {int.MinValue} to {int.MaxValue}");
}
