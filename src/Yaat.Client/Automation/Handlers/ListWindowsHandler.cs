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
/// open in each window's overlay layer, each with its stable node id, its owner's and its native window handle. The host runs only where popups
/// are overlay popups (automation mode on Windows), so there are no popup windows to list.
/// </summary>
public sealed class ListWindowsHandler(NodeRegistry registry) : IRequestHandler
{
    public string Method => ProtocolMethods.ListWindows;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync<object>(ListWindows);

    /// <summary>The native window handle the platform gives <paramref name="root"/> (an HWND on Windows); 0 when it gives none.</summary>
    public static long HwndOf(TopLevel root) => ToHwnd(root.TryGetPlatformHandle()?.Handle ?? 0);

    /// <summary>
    /// <paramref name="handle"/> as an HWND's value: its low 32 bits, unsigned. An HWND fits in 32 bits even in a 64-bit
    /// process and may arrive sign-extended; UI Automation's reading and the recorder's <c>--hwnd</c> both take it unsigned.
    /// </summary>
    public static long ToHwnd(nint handle) => (long)(uint)handle;

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
                window.IsVisible,
                HwndOf(window)
            ),
            _ => new WindowInfo(
                nodeId,
                null,
                root.GetType().Name,
                IsPopup: false,
                OwnerId: null,
                bounds,
                IsActive: false,
                root.IsVisible,
                HwndOf(root)
            ),
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
            // The open popup is attached to the window, so the translation succeeds; the fallback covers a host torn down mid-walk.
            BoundsInfo bounds =
                NodeInfoBuilder.GetWindowBounds(host)
                ?? new BoundsInfo
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
                    host.IsVisible,
                    Hwnd: 0
                )
            );
        }
    }
}
