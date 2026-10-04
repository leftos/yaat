// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

namespace Yaat.Client.Automation.Selectors;

/// <summary>How a selector step relates to the step before it.</summary>
public enum Combinator
{
    /// <summary>The first step of a path: matched against every element in scope.</summary>
    Self,

    /// <summary>Whitespace or <c>&gt;&gt;</c>: any visual descendant of the previous step's match.</summary>
    Descendant,

    /// <summary><c>&gt;</c>: a direct visual child of the previous step's match.</summary>
    Child,
}

/// <summary>An attribute filter's comparison; every comparison ignores case.</summary>
public enum AttrOp
{
    /// <summary><c>=</c></summary>
    Equal,

    /// <summary><c>*=</c></summary>
    Contains,

    /// <summary><c>^=</c></summary>
    StartsWith,

    /// <summary><c>$=</c></summary>
    EndsWith,
}

/// <summary>One filter of a compound selector.</summary>
public abstract record SelectorFilter;

/// <summary>
/// <c>[Path op Value]</c>: a property path on the element, or on its <c>DataContext</c> when the path starts with
/// <c>dc.</c> (<paramref name="IsDataContext"/>, with the prefix stripped from <paramref name="Path"/>).
/// </summary>
public sealed record AttributeFilter(string Path, AttrOp Op, string Value, bool IsDataContext) : SelectorFilter;

/// <summary><c>:name</c> or <c>:name(argument)</c>, e.g. <c>:enabled</c>, <c>:has-text("Save")</c>, <c>:nth(2)</c>.</summary>
public sealed record PseudoFilter(string Name, string? Argument) : SelectorFilter;

/// <summary>The pseudo-classes the selector grammar accepts; any other name is a parse error.</summary>
public static class PseudoClassNames
{
    public const string Visible = "visible";
    public const string Hidden = "hidden";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Focused = "focused";
    public const string Checked = "checked";
    public const string HasText = "has-text";
    public const string Role = "role";
    public const string Nth = "nth";

    /// <summary>Every accepted name.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Visible,
        Hidden,
        Enabled,
        Disabled,
        Focused,
        Checked,
        HasText,
        Role,
        Nth,
    };

    /// <summary>Whether <paramref name="name"/> takes a required argument: <c>:has-text(...)</c>, <c>:role(...)</c>, <c>:nth(N)</c>.</summary>
    public static bool NeedsArgument(string name) => name is HasText or Role or Nth;
}

/// <summary>A type name (null for <c>*</c> or none), an optional <c>#nodeId</c>, and the filters that follow.</summary>
public sealed record CompoundSelector(string? TypeName, int? NodeId, IReadOnlyList<SelectorFilter> Filters);

/// <summary>One compound selector and the combinator that joins it to the step before.</summary>
public sealed record SelectorStep(Combinator Combinator, CompoundSelector Compound);

/// <summary>A chain of steps, e.g. <c>ListBox &gt; ListBoxItem</c>.</summary>
public sealed record SelectorPath(IReadOnlyList<SelectorStep> Steps);

/// <summary>A comma-separated list of alternative paths; an element matching any of them matches.</summary>
public sealed record ParsedSelector(IReadOnlyList<SelectorPath> Alternatives);
