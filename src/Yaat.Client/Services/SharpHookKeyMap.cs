using Avalonia.Input;
using SharpHook.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Static lookup that converts SharpHook's native <see cref="KeyCode"/> enum into the matching
/// Avalonia <see cref="Key"/> so the rest of the client (which speaks Avalonia keys) can reuse
/// its existing keybind storage and matching logic without knowing SharpHook exists.
///
/// Only the keys a user is likely to bind as a PTT key or custom hotkey are mapped — letters,
/// digits, F-row, arrows, modifiers, and the common punctuation keys. Unmapped codes return
/// <see cref="Key.None"/> so consumers can cheaply ignore irrelevant events.
/// </summary>
internal static class SharpHookKeyMap
{
    private static readonly Dictionary<KeyCode, Key> Map = Build();

    public static Key ToAvaloniaKey(KeyCode code)
    {
        return Map.TryGetValue(code, out var key) ? key : Key.None;
    }

    private static Dictionary<KeyCode, Key> Build()
    {
        return new Dictionary<KeyCode, Key>
        {
            // Letters
            [KeyCode.VcA] = Key.A,
            [KeyCode.VcB] = Key.B,
            [KeyCode.VcC] = Key.C,
            [KeyCode.VcD] = Key.D,
            [KeyCode.VcE] = Key.E,
            [KeyCode.VcF] = Key.F,
            [KeyCode.VcG] = Key.G,
            [KeyCode.VcH] = Key.H,
            [KeyCode.VcI] = Key.I,
            [KeyCode.VcJ] = Key.J,
            [KeyCode.VcK] = Key.K,
            [KeyCode.VcL] = Key.L,
            [KeyCode.VcM] = Key.M,
            [KeyCode.VcN] = Key.N,
            [KeyCode.VcO] = Key.O,
            [KeyCode.VcP] = Key.P,
            [KeyCode.VcQ] = Key.Q,
            [KeyCode.VcR] = Key.R,
            [KeyCode.VcS] = Key.S,
            [KeyCode.VcT] = Key.T,
            [KeyCode.VcU] = Key.U,
            [KeyCode.VcV] = Key.V,
            [KeyCode.VcW] = Key.W,
            [KeyCode.VcX] = Key.X,
            [KeyCode.VcY] = Key.Y,
            [KeyCode.VcZ] = Key.Z,

            // Digits (top row)
            [KeyCode.Vc0] = Key.D0,
            [KeyCode.Vc1] = Key.D1,
            [KeyCode.Vc2] = Key.D2,
            [KeyCode.Vc3] = Key.D3,
            [KeyCode.Vc4] = Key.D4,
            [KeyCode.Vc5] = Key.D5,
            [KeyCode.Vc6] = Key.D6,
            [KeyCode.Vc7] = Key.D7,
            [KeyCode.Vc8] = Key.D8,
            [KeyCode.Vc9] = Key.D9,

            // Function row
            [KeyCode.VcF1] = Key.F1,
            [KeyCode.VcF2] = Key.F2,
            [KeyCode.VcF3] = Key.F3,
            [KeyCode.VcF4] = Key.F4,
            [KeyCode.VcF5] = Key.F5,
            [KeyCode.VcF6] = Key.F6,
            [KeyCode.VcF7] = Key.F7,
            [KeyCode.VcF8] = Key.F8,
            [KeyCode.VcF9] = Key.F9,
            [KeyCode.VcF10] = Key.F10,
            [KeyCode.VcF11] = Key.F11,
            [KeyCode.VcF12] = Key.F12,
            [KeyCode.VcF13] = Key.F13,
            [KeyCode.VcF14] = Key.F14,
            [KeyCode.VcF15] = Key.F15,
            [KeyCode.VcF16] = Key.F16,
            [KeyCode.VcF17] = Key.F17,
            [KeyCode.VcF18] = Key.F18,
            [KeyCode.VcF19] = Key.F19,
            [KeyCode.VcF20] = Key.F20,
            [KeyCode.VcF21] = Key.F21,
            [KeyCode.VcF22] = Key.F22,
            [KeyCode.VcF23] = Key.F23,
            [KeyCode.VcF24] = Key.F24,

            // Modifiers
            [KeyCode.VcLeftControl] = Key.LeftCtrl,
            [KeyCode.VcRightControl] = Key.RightCtrl,
            [KeyCode.VcLeftShift] = Key.LeftShift,
            [KeyCode.VcRightShift] = Key.RightShift,
            [KeyCode.VcLeftAlt] = Key.LeftAlt,
            [KeyCode.VcRightAlt] = Key.RightAlt,
            [KeyCode.VcLeftMeta] = Key.LWin,
            [KeyCode.VcRightMeta] = Key.RWin,

            // Whitespace + control
            [KeyCode.VcSpace] = Key.Space,
            [KeyCode.VcEnter] = Key.Enter,
            [KeyCode.VcTab] = Key.Tab,
            [KeyCode.VcBackspace] = Key.Back,
            [KeyCode.VcEscape] = Key.Escape,
            [KeyCode.VcCapsLock] = Key.CapsLock,

            // Navigation
            [KeyCode.VcLeft] = Key.Left,
            [KeyCode.VcRight] = Key.Right,
            [KeyCode.VcUp] = Key.Up,
            [KeyCode.VcDown] = Key.Down,
            [KeyCode.VcHome] = Key.Home,
            [KeyCode.VcEnd] = Key.End,
            [KeyCode.VcPageUp] = Key.PageUp,
            [KeyCode.VcPageDown] = Key.PageDown,
            [KeyCode.VcInsert] = Key.Insert,
            [KeyCode.VcDelete] = Key.Delete,

            // Punctuation (top-row and right side of main block)
            [KeyCode.VcMinus] = Key.OemMinus,
            [KeyCode.VcEquals] = Key.OemPlus,
            [KeyCode.VcOpenBracket] = Key.OemOpenBrackets,
            [KeyCode.VcCloseBracket] = Key.OemCloseBrackets,
            [KeyCode.VcBackslash] = Key.OemBackslash,
            [KeyCode.VcSemicolon] = Key.OemSemicolon,
            [KeyCode.VcQuote] = Key.OemQuotes,
            [KeyCode.VcComma] = Key.OemComma,
            [KeyCode.VcPeriod] = Key.OemPeriod,
            [KeyCode.VcSlash] = Key.OemQuestion,
            [KeyCode.VcBackQuote] = Key.OemTilde,

            // Numpad
            [KeyCode.VcNumPad0] = Key.NumPad0,
            [KeyCode.VcNumPad1] = Key.NumPad1,
            [KeyCode.VcNumPad2] = Key.NumPad2,
            [KeyCode.VcNumPad3] = Key.NumPad3,
            [KeyCode.VcNumPad4] = Key.NumPad4,
            [KeyCode.VcNumPad5] = Key.NumPad5,
            [KeyCode.VcNumPad6] = Key.NumPad6,
            [KeyCode.VcNumPad7] = Key.NumPad7,
            [KeyCode.VcNumPad8] = Key.NumPad8,
            [KeyCode.VcNumPad9] = Key.NumPad9,
            [KeyCode.VcNumPadMultiply] = Key.Multiply,
            [KeyCode.VcNumPadAdd] = Key.Add,
            [KeyCode.VcNumPadSubtract] = Key.Subtract,
            [KeyCode.VcNumPadDecimal] = Key.Decimal,
            [KeyCode.VcNumPadDivide] = Key.Divide,
            [KeyCode.VcNumPadEnter] = Key.Enter,
            [KeyCode.VcNumLock] = Key.NumLock,

            // Misc system keys
            [KeyCode.VcPrintScreen] = Key.PrintScreen,
            [KeyCode.VcScrollLock] = Key.Scroll,
            [KeyCode.VcPause] = Key.Pause,
        };
    }
}
