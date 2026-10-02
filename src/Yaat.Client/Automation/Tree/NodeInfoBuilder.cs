// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Tree;

/// <summary>Describes one element as a <see cref="NodeInfo"/>, with ids from the host's registry. Runs on the UI thread.</summary>
public sealed class NodeInfoBuilder(NodeRegistry registry)
{
    /// <param name="visual">The element to describe.</param>
    /// <param name="children">Its already-described children, or null when the walk stopped above them or it has none.</param>
    public NodeInfo Create(Visual visual, List<NodeInfo>? children)
    {
        Visual? parent = visual.GetVisualParent();
        return new NodeInfo
        {
            NodeId = registry.GetOrRegister(visual),
            Type = visual.GetType().Name,
            Name = (visual as Control)?.Name,
            Bounds = new BoundsInfo
            {
                X = visual.Bounds.X,
                Y = visual.Bounds.Y,
                Width = visual.Bounds.Width,
                Height = visual.Bounds.Height,
            },
            WindowBounds = GetWindowBounds(visual),
            IsVisible = visual.IsVisible,
            Text = ElementDescription.TextOf(visual),
            Value = (visual is TextBox textBox) ? (textBox.Text ?? string.Empty) : null,
            IsEnabled = (visual as InputElement)?.IsEffectivelyEnabled,
            IsFocused = (visual as InputElement)?.IsFocused,
            IsInteractive = GetIsInteractive(visual),
            AutomationId = GetAutomationId(visual),
            Role = ElementDescription.RoleOf(visual),
            ClassName = GetClassName(visual),
            ParentId = (parent is null) ? null : registry.GetOrRegister(parent),
            OwnerId = GetOwnerId(visual),
            Children = children,
        };
    }

    private int? GetOwnerId(Visual visual) =>
        visual switch
        {
            Window { Owner: { } owner } => registry.GetOrRegister(owner),
            OverlayPopupHost host when TopLevel.GetTopLevel(host) is { } window => registry.GetOrRegister(window),
            _ => null,
        };

    /// <summary>
    /// The element's bounds in its top-level's coordinates, or null when it is detached or the translation fails. An overlay
    /// popup lives in its window's overlay layer, so its top-level is that window: <c>list_windows</c> reports a popup's
    /// rectangle with this same rule, and its <c>get_tree</c> node agrees by construction.
    /// </summary>
    public static BoundsInfo? GetWindowBounds(Visual visual)
    {
        if (TopLevel.GetTopLevel(visual) is not { } topLevel)
        {
            return null;
        }

        if (visual.TranslatePoint(new Point(0, 0), topLevel) is not { } origin)
        {
            return null;
        }

        return new BoundsInfo
        {
            X = origin.X,
            Y = origin.Y,
            Width = visual.Bounds.Width,
            Height = visual.Bounds.Height,
        };
    }

    // Button covers RepeatButton, ToggleButton, CheckBox and RadioButton; TextBox covers MaskedTextBox.
    private static bool? GetIsInteractive(Visual visual)
    {
        if (visual is not InputElement input)
        {
            return null;
        }

        if (!input.IsEffectivelyEnabled || !input.IsHitTestVisible)
        {
            return false;
        }

        return input.Focusable || (visual is Button or MenuItem or TextBox or AutoCompleteBox or ComboBox or Slider);
    }

    private static string? GetAutomationId(Visual visual)
    {
        string? automationId = (visual is Control control) ? AutomationProperties.GetAutomationId(control) : null;
        return string.IsNullOrEmpty(automationId) ? null : automationId;
    }

    private static string? GetClassName(Visual visual) =>
        ((visual is StyledElement styled) && (styled.Classes.Count > 0)) ? string.Join(" ", styled.Classes) : null;
}
