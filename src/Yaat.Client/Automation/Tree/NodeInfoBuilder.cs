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
            IsVisible = visual.IsVisible,
            Text = ElementDescription.TextOf(visual),
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
