using Yaat.Client.Automation.Protocol;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// One line per element or window a YAAT client reports over its automation pipe, in the shape of the UI Automation rows
/// (<c>eN | type | text | id=… | state | rect=(x,y WxH)</c>). The type is the Avalonia type name and the rectangle is in
/// device-independent pixels relative to the element's window.
/// </summary>
public static class PipeDescribe
{
    /// <summary>A tree node's row: its text (or name), its automation id (or name), whether it is enabled, and its window rectangle.</summary>
    public static string Node(NodeInfo node, string id)
    {
        string text = node.Text ?? node.Name ?? string.Empty;
        string automationId = node.AutomationId ?? node.Name ?? string.Empty;
        bool enabled = node.IsEnabled ?? true;
        return $"{id} | {node.Type} | {text} | id={automationId} | enabled={enabled} | rect={FormatBounds(node.WindowBounds)}";
    }

    /// <summary>A <c>list_windows</c> row: its title, whether it is visible and active, a popup marker, and its rectangle.</summary>
    public static string Window(WindowInfo window, string id)
    {
        string popup = window.IsPopup ? " | popup" : string.Empty;
        string state = $"visible={window.IsVisible} | active={window.IsActive}{popup}";
        return $"{id} | {window.TypeName} | {window.Title ?? string.Empty} | id= | {state} | rect={FormatBounds(window.Bounds)}";
    }

    private static string FormatBounds(BoundsInfo? bounds) =>
        UiaQuery.FormatRect((bounds is null) ? Rect.Empty : new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
}
