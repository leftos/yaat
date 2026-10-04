using System.Text.Json;
using System.Text.RegularExpressions;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Handlers;

/// <summary>The kinds of <c>wait_until</c> condition.</summary>
public enum WaitUntilKind
{
    OnGround,
    Landed,
    PhaseIs,
    PhaseIsNot,
    QueueEmpty,
    LogMatches,
    SimSeconds,
}

/// <summary>
/// One <c>wait_until</c> condition: its kind as the request named it, the kind, and the fields the kind reads (the others
/// are empty, null or zero). <paramref name="Callsign"/> is empty on a <c>log_matches</c> that filters on no aircraft.
/// </summary>
public sealed record WaitUntilCondition(string Name, WaitUntilKind Kind, string Callsign, string Phase, Regex? Pattern, double AtLeast);

/// <summary>The client actions <c>wait_until</c> runs when its conditions are met.</summary>
public enum WaitUntilActionKind
{
    Pause,
    SetRate,
}

/// <summary>One <c>then</c> action: its name as the request gave it, the action, and the rate a <c>set_rate</c> sets.</summary>
public sealed record WaitUntilAction(string Name, WaitUntilActionKind Kind, int Rate);

/// <summary>
/// The params of <c>wait_until</c>: the conditions, whether all of them must hold (<c>mode: all</c>) or any one
/// (<c>mode: any</c>, the default), the wait's wall-time limit in milliseconds, the target to capture at the match, and the
/// actions to run at the match.
/// </summary>
public sealed record WaitUntilParams(
    IReadOnlyList<WaitUntilCondition> Conditions,
    bool RequireAll,
    int TimeoutMs,
    ScreenshotTarget? Screenshot,
    IReadOnlyList<WaitUntilAction> Then
)
{
    public const int DefaultTimeoutMs = 30000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 600000;

    private static readonly Dictionary<string, WaitUntilKind> KindNames = new(StringComparer.Ordinal)
    {
        ["on_ground"] = WaitUntilKind.OnGround,
        ["landed"] = WaitUntilKind.Landed,
        ["phase_is"] = WaitUntilKind.PhaseIs,
        ["phase_is_not"] = WaitUntilKind.PhaseIsNot,
        ["queue_empty"] = WaitUntilKind.QueueEmpty,
        ["log_matches"] = WaitUntilKind.LogMatches,
        ["sim_seconds"] = WaitUntilKind.SimSeconds,
    };

    /// <summary>Reads the params from a request, or returns the <see cref="HandlerErrorResult"/> naming the first bad one.</summary>
    /// <param name="raw">The request's <c>params</c>.</param>
    /// <returns>A <see cref="WaitUntilParams"/>, or a <see cref="HandlerErrorResult"/>.</returns>
    public static object Read(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (List<WaitUntilCondition> conditions, HandlerErrorResult? conditionsError) = ReadConditions(element);
        (bool requireAll, HandlerErrorResult? modeError) = ReadMode(element);
        (int timeoutMs, HandlerErrorResult? timeoutError) = ReadTimeout(element);
        (ScreenshotTarget? screenshot, HandlerErrorResult? screenshotError) = ReadScreenshot(element);
        (List<WaitUntilAction> then, HandlerErrorResult? thenError) = ReadThen(element);
        HandlerErrorResult? error = conditionsError ?? modeError ?? timeoutError ?? screenshotError ?? thenError;
        return (error is not null) ? error : new WaitUntilParams(conditions, requireAll, timeoutMs, screenshot, then);
    }

    private static (List<WaitUntilCondition> Conditions, HandlerErrorResult? Error) ReadConditions(JsonElement element)
    {
        if (!element.TryGetProperty("conditions", out JsonElement array) || (array.ValueKind != JsonValueKind.Array) || (array.GetArrayLength() == 0))
        {
            return ([], HandlerResult.InvalidParam("conditions", "'conditions' is required: a non-empty array of {kind, ...} objects."));
        }

        List<WaitUntilCondition> conditions = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            object read = ReadCondition(item, $"conditions[{conditions.Count}]");
            if (read is HandlerErrorResult error)
            {
                return ([], error);
            }

            conditions.Add((WaitUntilCondition)read);
        }

        return (conditions, null);
    }

    private static object ReadCondition(JsonElement item, string path)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return HandlerResult.InvalidParam(path, $"'{path}' must be an object with a 'kind'.");
        }

        (string name, HandlerErrorResult? nameError) = RequiredString(item, path, "kind");
        if (nameError is not null)
        {
            return nameError;
        }

        string normalized = name.ToLowerInvariant();
        if (!KindNames.TryGetValue(normalized, out WaitUntilKind kind))
        {
            return HandlerResult.InvalidParam($"{path}.kind", $"Unknown kind '{name}'. Use one of: {string.Join(", ", KindNames.Keys)}.");
        }

        return kind switch
        {
            WaitUntilKind.SimSeconds => ReadSimSeconds(item, path),
            WaitUntilKind.LogMatches => ReadLogMatches(item, path),
            WaitUntilKind.PhaseIs or WaitUntilKind.PhaseIsNot => ReadPhase(item, path, normalized, kind),
            _ => ReadAircraft(item, path, normalized, kind),
        };
    }

    private static object ReadAircraft(JsonElement item, string path, string name, WaitUntilKind kind)
    {
        (string callsign, HandlerErrorResult? error) = RequiredString(item, path, "callsign");
        return (error is not null) ? error : new WaitUntilCondition(name, kind, callsign, "", null, 0);
    }

    private static object ReadPhase(JsonElement item, string path, string name, WaitUntilKind kind)
    {
        (string callsign, HandlerErrorResult? callsignError) = RequiredString(item, path, "callsign");
        (string phase, HandlerErrorResult? phaseError) = RequiredString(item, path, "phase");
        HandlerErrorResult? error = callsignError ?? phaseError;
        return (error is not null) ? error : new WaitUntilCondition(name, kind, callsign, phase, null, 0);
    }

    private static object ReadSimSeconds(JsonElement item, string path)
    {
        bool valid =
            item.TryGetProperty("atLeast", out JsonElement value)
            && (value.ValueKind == JsonValueKind.Number)
            && value.TryGetDouble(out double atLeast)
            && double.IsFinite(atLeast);
        return valid
            ? new WaitUntilCondition("sim_seconds", WaitUntilKind.SimSeconds, "", "", null, value.GetDouble())
            : HandlerResult.InvalidParam($"{path}.atLeast", $"'{path}.atLeast' is required and must be a number of seconds.");
    }

    /// <summary>A <c>log_matches</c>: <c>pattern</c>, a .NET regex matched case-insensitively, and an optional <c>callsign</c>.</summary>
    private static object ReadLogMatches(JsonElement item, string path)
    {
        (string pattern, HandlerErrorResult? patternError) = RequiredString(item, path, "pattern");
        (string? callsign, HandlerErrorResult? callsignError) = InputParams.ReadOptionalString(item, "callsign");
        HandlerErrorResult? error = patternError ?? callsignError;
        if (error is not null)
        {
            return error;
        }

        // The non-backtracking engine matches in time linear in the line's length, so a pattern needs no match timeout; it
        // refuses the constructs that need backtracking (backreferences, lookarounds, atomic groups).
        try
        {
            var regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return new WaitUntilCondition("log_matches", WaitUntilKind.LogMatches, callsign ?? "", "", regex, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return HandlerResult.InvalidParam(
                $"{path}.pattern",
                $"'{path}.pattern' must be a .NET regex the non-backtracking engine accepts (no backreferences or lookarounds): {ex.Message}"
            );
        }
    }

    /// <summary>The non-blank string <paramref name="name"/> of the object at <paramref name="path"/>.</summary>
    private static (string Value, HandlerErrorResult? Error) RequiredString(JsonElement item, string path, string name)
    {
        if (item.TryGetProperty(name, out JsonElement value) && (StringOf(value) is { } text) && !string.IsNullOrWhiteSpace(text))
        {
            return (text, null);
        }

        return ("", HandlerResult.InvalidParam($"{path}.{name}", $"'{path}.{name}' is required and must be a non-empty string."));
    }

    private static string? StringOf(JsonElement value) => (value.ValueKind == JsonValueKind.String) ? value.GetString() : null;

    private static (bool RequireAll, HandlerErrorResult? Error) ReadMode(JsonElement element)
    {
        (string? mode, HandlerErrorResult? error) = InputParams.ReadOptionalString(element, "mode");
        if (error is not null)
        {
            return (false, error);
        }

        return mode?.ToLowerInvariant() switch
        {
            null or "any" => (false, null),
            "all" => (true, null),
            _ => (false, HandlerResult.InvalidParam("mode", "'mode' must be \"any\" (the default) or \"all\".")),
        };
    }

    private static (int TimeoutMs, HandlerErrorResult? Error) ReadTimeout(JsonElement element)
    {
        if (!element.TryGetProperty("timeoutMs", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (DefaultTimeoutMs, null);
        }

        return ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int timeoutMs))
            ? (Math.Clamp(timeoutMs, MinTimeoutMs, MaxTimeoutMs), null)
            : (
                DefaultTimeoutMs,
                HandlerResult.InvalidParam("timeoutMs", $"'timeoutMs' must be a whole number of milliseconds ({MinTimeoutMs}–{MaxTimeoutMs}).")
            );
    }

    /// <summary>The optional <c>screenshot</c> target, the same one the <c>screenshot</c> method takes; null when absent.</summary>
    private static (ScreenshotTarget? Target, HandlerErrorResult? Error) ReadScreenshot(JsonElement element)
    {
        if (!element.TryGetProperty("screenshot", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        object parsed = ScreenshotHandler.ParseTarget(value);
        return (parsed as ScreenshotTarget, parsed as HandlerErrorResult);
    }

    private static (List<WaitUntilAction> Then, HandlerErrorResult? Error) ReadThen(JsonElement element)
    {
        if (!element.TryGetProperty("then", out JsonElement array) || (array.ValueKind == JsonValueKind.Null))
        {
            return ([], null);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return ([], HandlerResult.InvalidParam("then", "'then' must be an array of {action, ...} objects."));
        }

        List<WaitUntilAction> actions = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            object read = ReadAction(item, $"then[{actions.Count}]");
            if (read is HandlerErrorResult error)
            {
                return ([], error);
            }

            actions.Add((WaitUntilAction)read);
        }

        return (actions, null);
    }

    private static object ReadAction(JsonElement item, string path)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return HandlerResult.InvalidParam(path, $"'{path}' must be an object with an 'action'.");
        }

        (string name, HandlerErrorResult? nameError) = RequiredString(item, path, "action");
        if (nameError is not null)
        {
            return nameError;
        }

        return name.ToLowerInvariant() switch
        {
            "pause" => new WaitUntilAction("pause", WaitUntilActionKind.Pause, 0),
            "set_rate" => ReadSetRate(item, path),
            _ => HandlerResult.InvalidParam($"{path}.action", $"Unknown action '{name}'. Use one of: pause, set_rate."),
        };
    }

    private static object ReadSetRate(JsonElement item, string path)
    {
        bool valid =
            item.TryGetProperty("rate", out JsonElement value)
            && (value.ValueKind == JsonValueKind.Number)
            && value.TryGetInt32(out int rate)
            && MainViewModel.SimRateOptions.Contains(rate);
        return valid
            ? new WaitUntilAction("set_rate", WaitUntilActionKind.SetRate, value.GetInt32())
            : HandlerResult.InvalidParam(
                $"{path}.rate",
                $"'{path}.rate' is required and must be one of the client's sim rates: {string.Join(", ", MainViewModel.SimRateOptions)}."
            );
    }
}
