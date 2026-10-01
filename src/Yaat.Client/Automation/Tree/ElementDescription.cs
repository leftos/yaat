// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Yaat.Client.Automation.Tree;

/// <summary>
/// The role and text the automation host reports for an element. <c>get_tree</c> and the selectors (<c>[Role=...]</c>,
/// <c>:role(...)</c>, <c>[Text=...]</c>, <c>:has-text(...)</c>) both read them here, so a node's reported role and text
/// are exactly what a selector matches.
/// </summary>
public static class ElementDescription
{
    // Order matters: CheckBox and RadioButton derive from Button through ToggleButton.
    /// <summary>A coarse role name (<c>button</c>, <c>textbox</c>, ...), or null for an element with no role.</summary>
    public static string? RoleOf(Visual visual) =>
        visual switch
        {
            CheckBox => "checkbox",
            RadioButton => "radio",
            Button => "button",
            TextBox => "textbox",
            AutoCompleteBox => "textbox",
            ListBoxItem => "listitem",
            TreeViewItem => "listitem",
            ComboBox => "combobox",
            Slider => "slider",
            TabItem => "tab",
            MenuItem => "menuitem",
            TextBlock => "text",
            _ => null,
        };

    /// <summary>
    /// The element's own text: a text block's or text box's text, a string header or string content, else its
    /// <see cref="AutomationProperties.NameProperty"/>; null when it has none.
    /// </summary>
    public static string? TextOf(Visual visual)
    {
        string? text = visual switch
        {
            TextBlock textBlock => textBlock.Text,
            HeaderedSelectingItemsControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            HeaderedContentControl { Header: string header } => header,
            ContentControl { Content: string content } => content,
            TextBox textBox => textBox.Text,
            _ => null,
        };
        if (text is not null)
        {
            return text;
        }

        string? automationName = (visual is Control control) ? AutomationProperties.GetName(control) : null;
        return string.IsNullOrEmpty(automationName) ? null : automationName;
    }
}
