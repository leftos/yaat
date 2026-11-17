using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's feature hooks: each feature's wiring at construction, its attachment to and detachment from the view
/// model, its rows in the aircraft menu's view section, its canvas keys and the Esc chain it takes part in.
/// </summary>
public partial class ScopeView
{
    /// <summary>Wires every feature to the canvas and the view, once, at construction.</summary>
    private void WireFeatures()
    {
        WireMenus();
        WireDataBlocks();
        WireMapTools();
        WireDrawRoute();
        WireHeadingVector();
        WireOverlays();
    }

    partial void WireMenus();

    partial void WireDataBlocks();

    partial void WireMapTools();

    partial void WireDrawRoute();

    partial void WireHeadingVector();

    partial void WireOverlays();

    /// <summary>Attaches every feature to <paramref name="vm"/>, the view's new view model.</summary>
    private void AttachFeatures(ScopeViewModel vm)
    {
        AttachMenus(vm);
        AttachDataBlocks(vm);
        AttachMapTools(vm);
        AttachDrawRoute(vm);
        AttachHeadingVector(vm);
        AttachOverlays(vm);
    }

    partial void AttachMenus(ScopeViewModel vm);

    partial void AttachDataBlocks(ScopeViewModel vm);

    partial void AttachMapTools(ScopeViewModel vm);

    partial void AttachDrawRoute(ScopeViewModel vm);

    partial void AttachHeadingVector(ScopeViewModel vm);

    partial void AttachOverlays(ScopeViewModel vm);

    /// <summary>Detaches every feature from <paramref name="vm"/>, the view model the view is leaving.</summary>
    private void DetachFeatures(ScopeViewModel vm)
    {
        DetachMenus(vm);
        DetachDataBlocks(vm);
        DetachMapTools(vm);
        DetachDrawRoute(vm);
        DetachHeadingVector(vm);
        DetachOverlays(vm);
    }

    partial void DetachMenus(ScopeViewModel vm);

    partial void DetachDataBlocks(ScopeViewModel vm);

    partial void DetachMapTools(ScopeViewModel vm);

    partial void DetachDrawRoute(ScopeViewModel vm);

    partial void DetachHeadingVector(ScopeViewModel vm);

    partial void DetachOverlays(ScopeViewModel vm);

    /// <summary>The Scope's own view section of the aircraft menu for <paramref name="ac"/>: the display rows, then the route rows.</summary>
    private List<Control> BuildViewSection(ScopeViewModel vm, AircraftModel? ac, MenuContext context)
    {
        var items = new List<Control>();
        AddDisplaySection(items, vm, ac, context);
        AddDrawRouteSection(items, vm, ac, context);
        return items;
    }

    partial void AddDisplaySection(List<Control> items, ScopeViewModel vm, AircraftModel? ac, MenuContext context);

    partial void AddDrawRouteSection(List<Control> items, ScopeViewModel vm, AircraftModel? ac, MenuContext context);

    /// <summary>Offers a canvas key to each feature ahead of the entry's keys. Returns whether one took it.</summary>
    private bool RunKeyHooks(KeyEventArgs e)
    {
        KeyDrawRoute(e);
        return e.Handled;
    }

    partial void KeyDrawRoute(KeyEventArgs e);

    /// <summary>
    /// Esc's chain, first taker wins: the measure prompt, the canvas's and the view model's modes, the entry, an Alt-drag in
    /// flight, the Scope's measurement, the map tools, then the Rings tool. Returns whether a step took it.
    /// </summary>
    private bool HandleEscape(ScopeViewModel vm) =>
        vm.TakeMeasurePrompt()
        || Canvas.EscapeModes()
        || vm.EscapeModes()
        || vm.ClearEntry()
        || Canvas.CancelMeasureDrag()
        || vm.CancelMeasure()
        || vm.EscapeTools()
        || vm.DisarmRingTool();
}
