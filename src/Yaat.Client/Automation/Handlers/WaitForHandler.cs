// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>wait_for</c>: polls a <c>selector</c> on the UI thread every 100 ms until a <c>condition</c> holds or <c>timeoutMs</c>
/// (default 5000, clamped to 100–30000) runs out. Conditions: <c>exists</c>, <c>not_exists</c>, <c>visible</c>,
/// <c>enabled</c>, <c>text_equals</c> and <c>text_contains</c> (with <c>text</c>; equals is case-sensitive, contains is not)
/// and <c>count_equals</c> (with <c>count</c>). The result is the elapsed time and the number of matches that met the
/// condition; expiry is <c>TIMEOUT</c> with the selector's last match count, on time even when the UI thread is busy.
/// The poll stops when the client disconnects or the host stops.
/// </summary>
public sealed class WaitForHandler(SelectorEngine engine) : IRequestHandler
{
    public const int DefaultTimeoutMs = 5000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly Dictionary<string, WaitCondition> ConditionNames = new(StringComparer.Ordinal)
    {
        ["exists"] = WaitCondition.Exists,
        ["not_exists"] = WaitCondition.NotExists,
        ["visible"] = WaitCondition.Visible,
        ["enabled"] = WaitCondition.Enabled,
        ["text_equals"] = WaitCondition.TextEquals,
        ["text_contains"] = WaitCondition.TextContains,
        ["count_equals"] = WaitCondition.CountEquals,
    };

    private enum WaitCondition
    {
        Exists,
        NotExists,
        Visible,
        Enabled,
        TextEquals,
        TextContains,
        CountEquals,
    }

    /// <summary>The condition and the argument it needs: <c>text</c> for the text conditions, <c>count</c> for count_equals.</summary>
    private sealed record ConditionParams(string Name, WaitCondition Condition, string Text, int Count);

    private sealed record WaitForParams(string Selector, ParsedSelector Parsed, ConditionParams Condition, int TimeoutMs);

    /// <summary>One poll: how many elements the selector matched, how many of them met the condition, and whether it holds.</summary>
    private readonly record struct Evaluation(int SelectorMatches, int ConditionMatches, bool Met);

    public string Method => ProtocolMethods.WaitFor;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not WaitForParams parameters)
        {
            return parsed;
        }

        return await Poll(parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> Poll(WaitForParams parameters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(parameters.TimeoutMs);
        int lastSelectorMatches = 0;
        while (true)
        {
            Evaluation? evaluation = await EvaluateWithin(parameters, timeout - stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            if (evaluation is { Met: true } met)
            {
                return new WaitForResult((int)stopwatch.ElapsedMilliseconds, met.ConditionMatches);
            }

            lastSelectorMatches = evaluation?.SelectorMatches ?? lastSelectorMatches;
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return TimedOut(parameters, lastSelectorMatches);
            }

            await Task.Delay((remaining < PollInterval) ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One poll on the UI thread, or null when the UI thread does not run it within <paramref name="remaining"/>.</summary>
    private async Task<Evaluation?> EvaluateWithin(WaitForParams parameters, TimeSpan remaining, CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            return await Dispatcher
                .UIThread.InvokeAsync(() => Evaluate(parameters))
                .GetTask()
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private Evaluation Evaluate(WaitForParams parameters)
    {
        IReadOnlyList<Visual> matches = engine.Resolve(parameters.Parsed);
        ConditionParams condition = parameters.Condition;
        int conditionMatches = CountMeeting(matches, condition);
        bool met = condition.Condition switch
        {
            WaitCondition.NotExists => conditionMatches == 0,
            WaitCondition.CountEquals => conditionMatches == condition.Count,
            _ => conditionMatches > 0,
        };
        return new Evaluation(matches.Count, conditionMatches, met);
    }

    private static int CountMeeting(IReadOnlyList<Visual> matches, ConditionParams condition) =>
        condition.Condition switch
        {
            WaitCondition.Visible => matches.Count(visual => visual.IsEffectivelyVisible),
            WaitCondition.Enabled => matches.Count(visual => visual is InputElement { IsEffectivelyEnabled: true }),
            WaitCondition.TextEquals => matches.Count(visual =>
                string.Equals(ElementDescription.TextOf(visual), condition.Text, StringComparison.Ordinal)
            ),
            WaitCondition.TextContains => matches.Count(visual =>
                ElementDescription.TextOf(visual)?.Contains(condition.Text, StringComparison.OrdinalIgnoreCase) == true
            ),
            _ => matches.Count,
        };

    private static HandlerErrorResult TimedOut(WaitForParams parameters, int selectorMatches) =>
        HandlerResult.Error(
            AutomationErrorCodes.Timeout,
            $"Condition '{parameters.Condition.Name}' on selector '{parameters.Selector}' did not hold within {parameters.TimeoutMs} ms; "
                + $"the selector last matched {selectorMatches} element(s).",
            $"Raise timeoutMs (up to {MaxTimeoutMs}), or check the selector and the element's state with get_tree.",
            new WaitTimeoutDetails(parameters.Selector, parameters.Condition.Name, parameters.TimeoutMs, selectorMatches)
        );

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (string? selector, HandlerErrorResult? selectorError) = InputParams.ReadOptionalString(element, "selector");
        if (selectorError is not null)
        {
            return selectorError;
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            return HandlerResult.MissingSelector();
        }

        (ConditionParams condition, HandlerErrorResult? conditionError) = ReadConditionParams(element);
        (int timeoutMs, HandlerErrorResult? timeoutError) = ReadTimeout(element);
        HandlerErrorResult? error = conditionError ?? timeoutError;
        if (error is not null)
        {
            return error;
        }

        return SelectorRequestHelper.TryParse(selector, out ParsedSelector? parsed, out HandlerErrorResult? parseError)
            ? new WaitForParams(selector, parsed, condition, timeoutMs)
            : parseError;
    }

    /// <summary><c>condition</c> and the argument it needs, or the error naming the first bad param.</summary>
    private static (ConditionParams Condition, HandlerErrorResult? Error) ReadConditionParams(JsonElement element)
    {
        (string name, WaitCondition condition, HandlerErrorResult? conditionError) = ReadCondition(element);
        bool needsText = condition is WaitCondition.TextEquals or WaitCondition.TextContains;
        (string text, HandlerErrorResult? textError) = needsText ? InputParams.ReadRequiredString(element, "text") : ("", null);
        (int count, HandlerErrorResult? countError) = (condition == WaitCondition.CountEquals) ? ReadCount(element) : (0, null);
        return (new ConditionParams(name, condition, text, count), conditionError ?? textError ?? countError);
    }

    private static (string Name, WaitCondition Condition, HandlerErrorResult? Error) ReadCondition(JsonElement element)
    {
        (string name, HandlerErrorResult? error) = InputParams.ReadRequiredString(element, "condition");
        if (error is not null)
        {
            return (name, WaitCondition.Exists, error);
        }

        string normalized = name.ToLowerInvariant();
        return ConditionNames.TryGetValue(normalized, out WaitCondition condition)
            ? (normalized, condition, null)
            : (
                name,
                WaitCondition.Exists,
                HandlerResult.InvalidParam("condition", $"Unknown condition '{name}'. Use one of: {string.Join(", ", ConditionNames.Keys)}.")
            );
    }

    private static (int Count, HandlerErrorResult? Error) ReadCount(JsonElement element) =>
        (
            element.TryGetProperty("count", out JsonElement value)
            && (value.ValueKind == JsonValueKind.Number)
            && value.TryGetInt32(out int count)
            && (count >= 0)
        )
            ? (count, null)
            : (0, HandlerResult.InvalidParam("count", "'count' is required for count_equals and must be a whole number of 0 or more."));

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
}
