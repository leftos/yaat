using Avalonia.Input;

namespace Yaat.Client.Views;

/// <summary>
/// Parses user-configured keybind strings like <c>"Ctrl+Shift+T"</c> into
/// <see cref="Key"/> + <see cref="KeyModifiers"/>. Extracted from SettingsViewModel
/// so pop-out windows in Core can resolve keybinds without pulling in the full
/// settings surface.
/// </summary>
public static class KeybindHelper
{
    public static bool ParseKeybind(string combo, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;

        var parts = combo.Split('+');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            switch (trimmed)
            {
                case "Ctrl":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "Alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "Shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    if (!Enum.TryParse(trimmed, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None;
    }
}
