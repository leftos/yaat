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

        string[] parts = combo.Split('+');
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
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

    /// <summary>
    /// True when <paramref name="key"/> pressed with <paramref name="modifiers"/> matches the binding. A modifier-only binding
    /// (e.g. RightCtrl) matches by key alone, since pressing it also sets its own flag in the event's modifiers.
    /// </summary>
    public static bool MatchesKeybind(Key key, KeyModifiers modifiers, Key bindKey, KeyModifiers bindModifiers) =>
        (key == bindKey) && (IsModifierOnlyKey(bindKey) || (modifiers == bindModifiers));

    public static bool IsModifierOnlyKey(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
