// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>click</c>: clicks one element, given as <c>nodeId</c> or <c>selector</c>. A TextBlock stands for its interactive
/// ancestor when it has one; a disabled or hidden element is <c>ELEMENT_DISABLED</c>.
/// <list type="bullet">
/// <item>A plain left click focuses a focusable element, as a real click does, then runs its meaningful action: a button's
/// own click through its automation peer (Avalonia's order: the toggle, the flyout, the <c>Click</c> event, then the
/// command), a menu item, or the selection of an item container.</item>
/// <item>Only when there is none — or for another button, modifiers or a double click — a synthetic pointer press and
/// release go to the element's centre. Never both, so a button wired with a <c>Click</c> handler and a command runs each once.</item>
/// </list>
/// Params: <c>button</c> (left, right, middle), <c>modifiers</c> (e.g. "ctrl+shift"), <c>clickCount</c> (1 or 2).
/// The result names what ran: <c>command</c>, <c>toggle</c>, <c>flyout</c>, <c>click_event</c>, <c>menu_item</c>,
/// <c>select</c> or <c>pointer</c>.
/// </summary>
public sealed class ClickHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    private const string CommandAction = "command";
    private const string ToggleAction = "toggle";
    private const string FlyoutAction = "flyout";
    private const string ClickEventAction = "click_event";
    private const string MenuItemAction = "menu_item";
    private const string SelectAction = "select";
    private const string PointerAction = "pointer";

    public string Method => ProtocolMethods.Click;

    public async Task<object> Handle(AutomationRequest request)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(request.Params);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget target, HandlerErrorResult? targetError) = InputParams.ReadTarget(element, "nodeId", "selector");
        (PointerClick click, HandlerErrorResult? clickError) = InputParams.ReadPointerClick(element);
        HandlerErrorResult? error = targetError ?? clickError;
        if (error is not null)
        {
            return error;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => Click(target, click));
    }

    private object Click(ElementTarget target, PointerClick click)
    {
        if (!targets.TryResolve(target, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        Visual clicked = InteractiveTargetOf(visual);
        int nodeId = registry.GetOrRegister(clicked);
        HandlerErrorResult? refusal = Refusal(clicked, nodeId);
        if (refusal is not null)
        {
            return refusal;
        }

        if (clicked is not Control control)
        {
            return HandlerResult.Unsupported(ProtocolMethods.Click, clicked.GetType().Name);
        }

        if (!click.IsPlainLeftClick)
        {
            return ClickWithPointer(control, click, nodeId);
        }

        // A real click moves the focus before the control acts, so an edit committed on LostFocus commits first.
        if (control.Focusable)
        {
            control.Focus();
        }

        return RunMeaningfulAction(control, nodeId) ?? ClickWithPointer(control, click, nodeId);
    }

    /// <summary>The refusal for an element a real click could not reach: disabled, or not visible; null when it can be clicked.</summary>
    private static HandlerErrorResult? Refusal(Visual clicked, int nodeId)
    {
        string elementType = clicked.GetType().Name;
        if (clicked is InputElement { IsEffectivelyEnabled: false })
        {
            return HandlerResult.ElementDisabled(nodeId, elementType);
        }

        return clicked.IsEffectivelyVisible ? null : HandlerResult.ElementDisabled(nodeId, elementType, "It is not visible.");
    }

    /// <summary>A TextBlock's interactive ancestor when it has one; any other element, or a TextBlock without one, itself.</summary>
    private static Visual InteractiveTargetOf(Visual visual) =>
        (visual is TextBlock) ? (visual.GetVisualAncestors().FirstOrDefault(IsSemanticClickTarget) ?? visual) : visual;

    private static bool IsSemanticClickTarget(Visual visual) =>
        visual is Button or MenuItem or ListBoxItem or TabItem or ComboBoxItem or TreeViewItem;

    /// <summary>The click's meaningful action as a <see cref="ClickResult"/> or an error, or null when the element has none.</summary>
    private static object? RunMeaningfulAction(Control control, int nodeId) =>
        control switch
        {
            Button button => ClickButton(button, nodeId),
            MenuItem menuItem => RunMenuItem(menuItem, nodeId),
            _ => RunSelection(control, nodeId),
        };

    /// <summary>
    /// Avalonia's own click, through the button's automation peer: <see cref="IToggleProvider.Toggle"/> for a toggle button
    /// and <see cref="IInvokeProvider.Invoke"/> for any other, both of which run <c>Button.OnClick</c> (a toggle button
    /// toggles first, then the flyout opens or closes, the <c>Click</c> event is raised and, unless a handler marked it
    /// handled, the command runs). Null when the peer offers neither.
    /// </summary>
    private static object? ClickButton(Button button, int nodeId)
    {
        if ((button.Command is { } command) && !command.CanExecute(button.CommandParameter))
        {
            return HandlerResult.ElementDisabled(nodeId, button.GetType().Name, "Its command cannot execute.");
        }

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(button);
        if (peer.GetProvider<IToggleProvider>() is { } toggle)
        {
            toggle.Toggle();
            return new ClickResult(nodeId, ToggleAction);
        }

        if (peer.GetProvider<IInvokeProvider>() is not { } invoke)
        {
            return null;
        }

        string action =
            (button.Flyout is not null) ? FlyoutAction
            : (button.Command is not null) ? CommandAction
            : ClickEventAction;
        invoke.Invoke();
        return new ClickResult(nodeId, action);
    }

    private static object RunMenuItem(MenuItem menuItem, int nodeId)
    {
        bool hasCommand = menuItem.Command is not null;
        bool commandExecuted = ActivateMenuItem(menuItem);
        if (hasCommand && !commandExecuted)
        {
            return HandlerResult.ElementDisabled(nodeId, menuItem.GetType().Name, "Its command did not execute.");
        }

        return new ClickResult(nodeId, MenuItemAction);
    }

    private static bool ActivateMenuItem(MenuItem menuItem)
    {
        // SetCurrentValue, as the control itself does, so a OneWay binding on IsChecked survives the click.
        if (!menuItem.HasSubMenu)
        {
            if (menuItem.ToggleType == MenuItemToggleType.CheckBox)
            {
                menuItem.SetCurrentValue(MenuItem.IsCheckedProperty, !menuItem.IsChecked);
            }
            else if ((menuItem.ToggleType == MenuItemToggleType.Radio) && !menuItem.IsChecked)
            {
                menuItem.SetCurrentValue(MenuItem.IsCheckedProperty, true);
            }
        }

        var clickArgs = new RoutedEventArgs(MenuItem.ClickEvent);
        menuItem.RaiseEvent(clickArgs);
        if (!menuItem.StaysOpenOnClick)
        {
            MenuBase? menu = menuItem.FindLogicalAncestorOfType<MenuBase>() ?? menuItem.FindAncestorOfType<MenuBase>();
            menu?.Close();
        }

        return clickArgs.Handled;
    }

    /// <summary>
    /// Selects an item container in its list, tab control, combo box (closing its dropdown) or tree; null when the control
    /// is not one. The selection is written with SetCurrentValue, so a OneWay binding on it survives.
    /// </summary>
    private static ClickResult? RunSelection(Control control, int nodeId)
    {
        if (control is TreeViewItem treeViewItem)
        {
            // The tree view follows its containers' IsSelected through the routed IsSelectedChanged event.
            treeViewItem.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
            return new ClickResult(nodeId, SelectAction);
        }

        SelectingItemsControl? host =
            control.FindAncestorOfType<SelectingItemsControl>() ?? control.FindLogicalAncestorOfType<SelectingItemsControl>();
        int index = host?.IndexFromContainer(control) ?? -1;
        if ((host is null) || (index < 0))
        {
            return null;
        }

        host.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, index);
        if (host is ComboBox comboBox)
        {
            comboBox.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        }

        return new ClickResult(nodeId, SelectAction);
    }

    private static object ClickWithPointer(Control control, PointerClick click, int nodeId)
    {
        if (TopLevel.GetTopLevel(control) is not { } topLevel)
        {
            return HandlerResult.StaleNode(nodeId, $"Node {nodeId} is not in a window. Call get_tree or list_windows for fresh node ids.");
        }

        var centre = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        Point rootPosition = control.TranslatePoint(centre, topLevel) ?? centre;
        SyntheticPointer.Click(control, topLevel, rootPosition, click);
        return new ClickResult(nodeId, PointerAction);
    }
}
