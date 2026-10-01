// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Tree;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation.Selectors;

/// <summary>
/// Resolves a request's selector to the one element a handler acts on, answering every failure as a coded
/// <see cref="HandlerErrorResult"/>: <c>MISSING_SELECTOR</c>, <c>INVALID_SELECTOR</c> (with the parse position),
/// <c>STALE_NODE</c> (a <c>#42</c> whose node is gone), <c>NO_MATCH</c> or <c>AMBIGUOUS_SELECTOR</c> (with the match
/// count and an <c>:nth</c> hint). Runs on the UI thread.
/// </summary>
public sealed class SelectorRequestHelper(SelectorEngine engine, NodeRegistry registry)
{
    private static readonly ILogger Log = AppLog.CreateLogger("SelectorRequestHelper");

    /// <summary>The single element <paramref name="selector"/> matches, or the coded error saying why there is not one.</summary>
    public bool TryResolveSingle(string? selector, [NotNullWhen(true)] out Visual? visual, [NotNullWhen(false)] out HandlerErrorResult? error)
    {
        visual = null;
        if (string.IsNullOrWhiteSpace(selector))
        {
            error = HandlerResult.MissingSelector();
            return false;
        }

        if (!TryParse(selector, out ParsedSelector? parsed, out error))
        {
            return false;
        }

        if (FindStaleNode(parsed) is { } stale)
        {
            error = stale;
            return false;
        }

        IReadOnlyList<Visual> matches = engine.Resolve(parsed);
        if (matches.Count == 1)
        {
            visual = matches[0];
            error = null;
            return true;
        }

        error = (matches.Count == 0) ? HandlerResult.NoMatch(selector) : HandlerResult.AmbiguousSelector(selector, matches.Count);
        return false;
    }

    private static bool TryParse(string selector, [NotNullWhen(true)] out ParsedSelector? parsed, [NotNullWhen(false)] out HandlerErrorResult? error)
    {
        try
        {
            parsed = SelectorParser.Parse(selector);
            error = null;
            return true;
        }
        catch (SelectorParseException ex)
        {
            Log.LogDebug(ex, "Automation selector {Selector} does not parse", selector);
            parsed = null;
            error = HandlerResult.InvalidSelector(selector, ex.Message, ex.Position);
            return false;
        }
    }

    /// <summary>A <c>STALE_NODE</c> error for the first <c>#42</c> in the selector whose node is gone or detached, else null.</summary>
    private HandlerErrorResult? FindStaleNode(ParsedSelector parsed)
    {
        foreach (CompoundSelector compound in parsed.Alternatives.SelectMany(path => path.Steps).Select(step => step.Compound))
        {
            if (compound.NodeId is not int nodeId)
            {
                continue;
            }

            (Visual? resolved, string? reason) = registry.ResolveChecked(nodeId);
            if (resolved is null)
            {
                return (reason is null) ? HandlerResult.StaleNode(nodeId) : HandlerResult.StaleNode(nodeId, reason);
            }
        }

        return null;
    }
}
