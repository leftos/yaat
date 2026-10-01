// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>list_windows</c>: every root the registry reports (the host's windows and the windows they own), plus the popups
/// open in each window's overlay layer, each with its stable node id and its owner's. The host runs only where popups
/// are overlay popups (automation mode on Windows), so there are no popup windows to list.
/// </summary>
public sealed class ListWindowsHandler(NodeRegistry registry) : IRequestHandler
{
    public string Method => ProtocolMethods.ListWindows;

    public async Task<object> Handle(AutomationRequest request) => await Dispatcher.UIThread.InvokeAsync<object>(ListWindows);

    private List<WindowInfo> ListWindows()
    {
        List<WindowInfo> entries = [];
        foreach (TopLevel root in registry.GetRoots())
        {
            entries.Add(Describe(root));
            AddOverlayPopups(root, entries);
        }

        return entries;
    }

    private WindowInfo Describe(TopLevel root)
    {
        int nodeId = registry.GetOrRegister(root);
        var bounds = new BoundsInfo
        {
            X = 0,
            Y = 0,
            Width = root.Bounds.Width,
            Height = root.Bounds.Height,
        };

        return root switch
        {
            Window window => new WindowInfo(
                nodeId,
                string.IsNullOrEmpty(window.Title) ? null : window.Title,
                window.GetType().Name,
                IsPopup: false,
                OwnerId: window.Owner is { } owner ? registry.GetOrRegister(owner) : null,
                bounds,
                window.IsActive,
                window.IsVisible
            ),
            _ => new WindowInfo(nodeId, null, root.GetType().Name, IsPopup: false, OwnerId: null, bounds, IsActive: false, root.IsVisible),
        };
    }

    private void AddOverlayPopups(TopLevel root, List<WindowInfo> entries)
    {
        if (root is not Window window)
        {
            return;
        }

        // Avalonia 12 keeps overlay popups in a popup overlay layer with no public accessor, so they are found by type.
        int ownerId = registry.GetOrRegister(window);
        foreach (OverlayPopupHost host in window.GetVisualDescendants().OfType<OverlayPopupHost>())
        {
            var bounds = new BoundsInfo
            {
                X = host.Bounds.X,
                Y = host.Bounds.Y,
                Width = host.Bounds.Width,
                Height = host.Bounds.Height,
            };
            entries.Add(
                new WindowInfo(
                    registry.GetOrRegister(host),
                    null,
                    host.GetType().Name,
                    IsPopup: true,
                    ownerId,
                    bounds,
                    IsActive: false,
                    host.IsVisible
                )
            );
        }
    }
}
