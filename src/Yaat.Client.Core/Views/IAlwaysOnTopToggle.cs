namespace Yaat.Client.Views;

/// <summary>
/// Implemented by windows whose always-on-top state the global always-on-top hotkey can toggle.
/// Each window delegates to its own <see cref="WindowGeometryHelper.ToggleTopmost"/> so the toggle
/// is persisted per geometry key. Lets <c>WindowHotkeys</c> drive the toggle centrally instead of
/// every window duplicating the keybind plumbing in its own <c>OnKeyDown</c>.
/// </summary>
public interface IAlwaysOnTopToggle
{
    void ToggleAlwaysOnTop();
}
