using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Input;

namespace Yaat.Client.Automation.Handlers;

/// <summary>One keystroke of a <c>send_keys</c> string: typed text, or a key pressed with modifiers.</summary>
public sealed record KeyStroke(string? Text, Key Key, KeyModifiers Modifiers)
{
    public static KeyStroke Typed(string text) => new(text, Key.None, KeyModifiers.None);

    public static KeyStroke Pressed(Key key, KeyModifiers modifiers) => new(null, key, modifiers);
}

/// <summary>Why a <c>send_keys</c> string does not parse, and the zero-based position of the fault.</summary>
public sealed record KeysFault(string Message, int Position);

/// <summary>
/// Parses the client driver's SendKeys syntax: a plain character is typed text; <c>{ENTER}</c>, <c>{ESC}</c>,
/// <c>{TAB}</c>, <c>{BACKSPACE}</c>/<c>{BS}</c>, <c>{DEL}</c>/<c>{DELETE}</c>, <c>{HOME}</c>, <c>{END}</c>, the arrows
/// and <c>{F1}</c>–<c>{F12}</c> are keys (any case), as is <c>~</c> (Enter); <c>^</c> (Ctrl), <c>+</c> (Shift) and
/// <c>%</c> (Alt) prefix a key, a letter or a digit; a literal <c>+ ^ % ~ ( ) { } [ ]</c> is wrapped in braces, as <c>{+}</c>.
/// <list type="bullet">
/// <item>A Shift-only prefix on a letter or a digit types the shifted character as text (<c>+a</c> is <c>A</c>, <c>+1</c>
/// is <c>!</c> on the US layout), as WinForms SendKeys does; a prefix holding Ctrl or Alt makes a key chord.</item>
/// <item>A control character or half of a surrogate pair cannot be typed and is a fault; Enter and Tab are <c>{ENTER}</c>
/// and <c>{TAB}</c>.</item>
/// </list>
/// </summary>
public static class SendKeysParser
{
    private const string ModifierPrefixes = "^+%";
    private const string ReservedCharacters = "(){}[]";
    private const string BracedLiterals = "+^%~(){}[]";
    private const string ShiftedDigits = ")!@#$%^&*(";
    private const string KnownKeys = "{ENTER} {ESC} {TAB} {BACKSPACE}/{BS} {DEL}/{DELETE} {HOME} {END} {LEFT} {RIGHT} {UP} {DOWN} {F1}–{F12}";

    private static readonly Dictionary<string, Key> NamedKeys = BuildNamedKeys();

    public static bool TryParse(string keys, [NotNullWhen(true)] out List<KeyStroke>? strokes, [NotNullWhen(false)] out KeysFault? fault)
    {
        List<KeyStroke> parsed = [];
        int index = 0;
        while (index < keys.Length)
        {
            (index, fault) = ParseStroke(keys, index, parsed);
            if (fault is not null)
            {
                strokes = null;
                return false;
            }
        }

        strokes = parsed;
        fault = null;
        return true;
    }

    private static (int Next, KeysFault? Fault) ParseStroke(string keys, int start, List<KeyStroke> strokes)
    {
        (KeyModifiers modifiers, int index) = ReadPrefixes(keys, start);
        if (index >= keys.Length)
        {
            return (index, new KeysFault($"The modifier prefix at position {start} is not followed by a key.", start));
        }

        if (!TryReadKey(keys, index, out KeyStroke? stroke, out int next, out KeysFault? fault))
        {
            return (next, fault);
        }

        if (modifiers != KeyModifiers.None)
        {
            stroke = WithModifiers(stroke, modifiers);
            if (stroke is null)
            {
                return (next, new KeysFault($"The modifier prefix at position {start} must be followed by a letter, a digit or a {{KEY}}.", start));
            }
        }

        strokes.Add(stroke);
        return (next, null);
    }

    private static (KeyModifiers Modifiers, int Next) ReadPrefixes(string keys, int start)
    {
        KeyModifiers modifiers = KeyModifiers.None;
        int index = start;
        while ((index < keys.Length) && ModifierPrefixes.Contains(keys[index]))
        {
            modifiers |= keys[index] switch
            {
                '^' => KeyModifiers.Control,
                '+' => KeyModifiers.Shift,
                _ => KeyModifiers.Alt,
            };
            index++;
        }

        return (modifiers, index);
    }

    private static bool TryReadKey(
        string keys,
        int index,
        [NotNullWhen(true)] out KeyStroke? stroke,
        out int next,
        [NotNullWhen(false)] out KeysFault? fault
    )
    {
        char character = keys[index];
        if (character == '{')
        {
            return TryReadBraced(keys, index, out stroke, out next, out fault);
        }

        next = index + 1;
        stroke = null;
        fault = TypedCharacterFault(keys, index);
        if (fault is not null)
        {
            return false;
        }

        if (character == '~')
        {
            stroke = KeyStroke.Pressed(Key.Enter, KeyModifiers.None);
            return true;
        }

        next = char.IsHighSurrogate(character) ? index + 2 : index + 1;
        stroke = KeyStroke.Typed(keys[index..next]);
        return true;
    }

    /// <summary>Why the character at <paramref name="index"/> cannot stand unbraced or be typed; null when it can.</summary>
    private static KeysFault? TypedCharacterFault(string keys, int index)
    {
        char character = keys[index];
        string code = ((int)character).ToString("X4", CultureInfo.InvariantCulture);
        if (ReservedCharacters.Contains(character))
        {
            return new KeysFault($"The '{character}' at position {index} must be wrapped in braces, as {{{character}}}.", index);
        }

        if (character < ' ')
        {
            return new KeysFault($"The control character U+{code} at position {index} cannot be typed; send {{ENTER}} or {{TAB}} instead.", index);
        }

        bool pairedHigh = char.IsHighSurrogate(character) && (index + 1 < keys.Length) && char.IsLowSurrogate(keys[index + 1]);
        return (char.IsSurrogate(character) && !pairedHigh)
            ? new KeysFault($"The unpaired surrogate U+{code} at position {index} is not a character.", index)
            : null;
    }

    private static bool TryReadBraced(
        string keys,
        int start,
        [NotNullWhen(true)] out KeyStroke? stroke,
        out int next,
        [NotNullWhen(false)] out KeysFault? fault
    )
    {
        // "{}}" is the escaped closing brace: the one token whose content is itself a '}'.
        bool escapedClose = (start + 2 < keys.Length) && (keys[start + 1] == '}') && (keys[start + 2] == '}');
        int close = escapedClose ? start + 2 : keys.IndexOf('}', start + 1);
        stroke = null;
        next = start;
        if (close < 0)
        {
            fault = new KeysFault($"The '{{' at position {start} is never closed.", start);
            return false;
        }

        string name = keys[(start + 1)..close];
        fault = null;
        next = close + 1;
        if ((name.Length == 1) && BracedLiterals.Contains(name[0]))
        {
            stroke = KeyStroke.Typed(name);
            return true;
        }

        if (NamedKeys.TryGetValue(name, out Key key))
        {
            stroke = KeyStroke.Pressed(key, KeyModifiers.None);
            return true;
        }

        next = start;
        fault = new KeysFault($"'{{{name}}}' at position {start} is not a key send_keys knows. Keys: {KnownKeys}.", start);
        return false;
    }

    /// <summary>
    /// The stroke with <paramref name="modifiers"/> applied: a key chord, or for Shift alone on a letter or a digit the
    /// shifted character as text. Null when the stroke is text that is not a letter or a digit.
    /// </summary>
    private static KeyStroke? WithModifiers(KeyStroke stroke, KeyModifiers modifiers)
    {
        if (stroke.Text is null)
        {
            return stroke with { Modifiers = modifiers };
        }

        char character = stroke.Text[0];
        bool letter = (stroke.Text.Length == 1) && char.IsAsciiLetter(character);
        bool digit = (stroke.Text.Length == 1) && char.IsAsciiDigit(character);
        if (!letter && !digit)
        {
            return null;
        }

        if (modifiers == KeyModifiers.Shift)
        {
            return KeyStroke.Typed(letter ? char.ToUpperInvariant(character).ToString() : ShiftedDigits[character - '0'].ToString());
        }

        return KeyStroke.Pressed(Enum.Parse<Key>(letter ? char.ToUpperInvariant(character).ToString() : $"D{character}"), modifiers);
    }

    private static Dictionary<string, Key> BuildNamedKeys()
    {
        Dictionary<string, Key> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = Key.Enter,
            ["ESC"] = Key.Escape,
            ["TAB"] = Key.Tab,
            ["BACKSPACE"] = Key.Back,
            ["BS"] = Key.Back,
            ["DEL"] = Key.Delete,
            ["DELETE"] = Key.Delete,
            ["HOME"] = Key.Home,
            ["END"] = Key.End,
            ["LEFT"] = Key.Left,
            ["UP"] = Key.Up,
            ["RIGHT"] = Key.Right,
            ["DOWN"] = Key.Down,
        };
        for (int function = 1; function <= 12; function++)
        {
            keys[$"F{function}"] = Key.F1 + (function - 1);
        }

        return keys;
    }
}
