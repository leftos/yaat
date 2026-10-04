// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Tree;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation.Selectors;

/// <summary>
/// Resolves a parsed selector against the live visual trees of the registry's roots (the host's windows, the windows
/// they own, and the overlay popups inside them). Every member runs on the UI thread; callers marshal there first.
/// </summary>
/// <param name="registry">The host's node registry: its roots seed the search and its ids answer <c>#42</c>.</param>
public sealed class SelectorEngine(NodeRegistry registry)
{
    private static readonly ILogger Log = AppLog.CreateLogger("SelectorEngine");

    /// <summary>
    /// Every element <paramref name="parsed"/> matches, in tree order, each once. A <c>#42</c> whose node is gone matches
    /// nothing; callers that must tell a stale id from no match check the ids first.
    /// </summary>
    public IReadOnlyList<Visual> Resolve(ParsedSelector parsed)
    {
        Dispatcher.UIThread.VerifyAccess();
        HashSet<Visual> seen = new(ReferenceEqualityComparer.Instance);
        List<Visual> results = [];
        foreach (SelectorPath path in parsed.Alternatives)
        {
            foreach (Visual match in ResolvePath(path))
            {
                if (seen.Add(match))
                {
                    results.Add(match);
                }
            }
        }

        return results;
    }

    private List<Visual> ResolvePath(SelectorPath path)
    {
        List<Visual> current = [.. registry.GetRoots().SelectMany(root => root.GetVisualDescendants().Prepend(root))];
        foreach (SelectorStep step in path.Steps)
        {
            IEnumerable<Visual> candidates = step.Combinator switch
            {
                Combinator.Descendant => current.SelectMany(visual => visual.GetVisualDescendants()),
                Combinator.Child => current.SelectMany(visual => visual.GetVisualChildren()),
                _ => current,
            };
            // A node id is resolved once per compound, never by registering every candidate to compare ids.
            Visual? target = (step.Compound.NodeId is int nodeId) ? registry.Resolve(nodeId) : null;
            current = [.. candidates.Distinct().Where(visual => MatchesCompound(visual, step.Compound, target))];
        }

        // :nth picks one match of the whole path, zero-based, so it applies after the last step rather than per element.
        PseudoFilter? nth = path.Steps[^1].Compound.Filters.OfType<PseudoFilter>().FirstOrDefault(filter => filter.Name == PseudoClassNames.Nth);
        if ((nth is null) || !int.TryParse(nth.Argument, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return current;
        }

        return (index < current.Count) ? [current[index]] : [];
    }

    private static bool MatchesCompound(Visual visual, CompoundSelector compound, Visual? target)
    {
        if ((compound.NodeId is not null) && !ReferenceEquals(visual, target))
        {
            return false;
        }

        if ((compound.TypeName is { } typeName) && !TypeMatches(visual, typeName))
        {
            return false;
        }

        return compound.Filters.All(filter => MatchesFilter(visual, filter));
    }

    private static bool MatchesFilter(Visual visual, SelectorFilter filter) =>
        filter switch
        {
            AttributeFilter attribute => MatchesAttribute(visual, attribute),
            PseudoFilter { Name: PseudoClassNames.Nth } => true,
            PseudoFilter pseudo => MatchesPseudo(visual, pseudo),
            _ => false,
        };

    private static bool MatchesAttribute(Visual visual, AttributeFilter filter)
    {
        object? value;
        if (filter.IsDataContext)
        {
            value = IntroducesDataContext(visual) ? ReadPropertyPath(visual.DataContext, filter.Path) : null;
        }
        else
        {
            value = ReadAttribute(visual, filter.Path);
        }

        if (value is null)
        {
            return false;
        }

        string actual = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return filter.Op switch
        {
            AttrOp.Equal => string.Equals(actual, filter.Value, StringComparison.OrdinalIgnoreCase),
            AttrOp.Contains => actual.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            AttrOp.StartsWith => actual.StartsWith(filter.Value, StringComparison.OrdinalIgnoreCase),
            AttrOp.EndsWith => actual.EndsWith(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Whether the element brings its own <c>DataContext</c> rather than inheriting it: set on the element itself, and not
    /// the same object its visual parent has. A list item matches its row; the content presenter inside it, which Avalonia
    /// also sets to the row, and every inheriting descendant do not.
    /// </summary>
    private static bool IntroducesDataContext(Visual element) =>
        element.IsSet(StyledElement.DataContextProperty) && !ReferenceEquals(element.DataContext, element.GetVisualParent()?.DataContext);

    /// <summary>An element attribute: the <c>Name</c>, <c>AutomationId</c>, <c>Text</c> and <c>Role</c> shorthands, else a property path.</summary>
    private static object? ReadAttribute(Visual visual, string path)
    {
        if (path.Equals("Name", StringComparison.OrdinalIgnoreCase) && (visual is Control named))
        {
            return named.Name;
        }

        if (path.Equals("AutomationId", StringComparison.OrdinalIgnoreCase) && (visual is Control automated))
        {
            return AutomationProperties.GetAutomationId(automated);
        }

        if (path.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            return ElementDescription.TextOf(visual);
        }

        if (path.Equals("Role", StringComparison.OrdinalIgnoreCase))
        {
            return ElementDescription.RoleOf(visual);
        }

        return ReadPropertyPath(visual, path);
    }

    /// <summary>Follows a dotted path of public instance properties (case-insensitive) by reflection; null when a step is missing.</summary>
    private static object? ReadPropertyPath(object? root, string path)
    {
        if ((root is null) || (path.Length == 0))
        {
            return root;
        }

        object? current = root;
        foreach (string segment in path.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            current = ReadProperty(current, segment);
        }

        return current;
    }

    /// <summary>A public property's value; null (no match) for a missing property, an indexer, or one with no public getter.</summary>
    private static object? ReadProperty(object target, string name)
    {
        try
        {
            PropertyInfo? property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if ((property is null) || (property.GetIndexParameters().Length > 0) || (property.GetGetMethod() is null))
            {
                return null;
            }

            return property.GetValue(target);
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TargetInvocationException)
        {
            Log.LogDebug(ex, "Selector could not read property {Property} of {Type}; treating it as no match", name, target.GetType().Name);
            return null;
        }
    }

    private static bool MatchesPseudo(Visual visual, PseudoFilter filter) =>
        filter.Name switch
        {
            PseudoClassNames.Visible => IsEffectivelyVisible(visual),
            PseudoClassNames.Hidden => !IsEffectivelyVisible(visual),
            PseudoClassNames.Enabled => (visual is not InputElement input) || input.IsEffectivelyEnabled,
            PseudoClassNames.Disabled => (visual is InputElement input) && !input.IsEffectivelyEnabled,
            PseudoClassNames.Focused => (visual is InputElement input) && input.IsKeyboardFocusWithin,
            PseudoClassNames.Checked => (visual is ToggleButton toggle) && (toggle.IsChecked == true),
            PseudoClassNames.HasText => ExtractText(visual)?.Contains(filter.Argument ?? "", StringComparison.OrdinalIgnoreCase) == true,
            PseudoClassNames.Role => string.Equals(ElementDescription.RoleOf(visual), filter.Argument, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool IsEffectivelyVisible(Visual visual)
    {
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (!current.IsVisible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The element's own text, else the text of up to five visible text blocks inside it.</summary>
    private static string? ExtractText(Visual visual) => ElementDescription.TextOf(visual) ?? TextFromDescendants(visual);

    private static string? TextFromDescendants(Visual visual)
    {
        List<string> texts =
        [
            .. visual
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(textBlock => textBlock.IsVisible && !string.IsNullOrWhiteSpace(textBlock.Text))
                .Select(textBlock => textBlock.Text!)
                .Take(5),
        ];
        return (texts.Count == 0) ? null : string.Join(" · ", texts);
    }

    /// <summary>
    /// The element's type or one of its base types has exactly the name <paramref name="typeName"/> (case-insensitive):
    /// <c>ToggleButton</c> matches a <c>CheckBox</c>, <c>Button</c> does not match a <c>ButtonSpinner</c>.
    /// </summary>
    private static bool TypeMatches(Visual visual, string typeName)
    {
        for (Type? type = visual.GetType(); (type is not null) && (type != typeof(object)); type = type.BaseType)
        {
            if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
