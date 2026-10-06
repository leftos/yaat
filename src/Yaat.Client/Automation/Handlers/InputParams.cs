using System.Text.Json;
using Avalonia.Input;

namespace Yaat.Client.Automation.Handlers;

/// <summary>The element an input method acts on: a node id or a selector, read from the two params named here.</summary>
public sealed record ElementTarget(int? NodeId, string? Selector, string NodeIdParam, string SelectorParam)
{
    public bool IsGiven => (NodeId is not null) || (Selector is not null);

    /// <summary>The param the target came from, for an error about the element it names.</summary>
    public string ParamName => (NodeId is not null) ? NodeIdParam : SelectorParam;
}

/// <summary>A synthetic mouse click: the button, the keyboard modifiers held, and 1 for a click or 2 for a double click.</summary>
public sealed record PointerClick(MouseButton Button, KeyModifiers Modifiers, int ClickCount)
{
    /// <summary>A plain left click, the one the semantic click ladder stands in for.</summary>
    public bool IsPlainLeftClick => (Button == MouseButton.Left) && (Modifiers == KeyModifiers.None) && (ClickCount == 1);
}

/// <summary>
/// Reads the input methods' params. Each reader returns its value and null, or a default and the
/// <see cref="HandlerErrorResult"/> naming the bad param, so a handler reports the first fault it finds.
/// </summary>
public static class InputParams
{
    private const string ModifiersHint = "'modifiers' must be keys joined by '+', each one of ctrl, shift, alt or meta, e.g. \"ctrl+shift\".";

    public static (JsonElement Element, HandlerErrorResult? Error) RequireObject(JsonElement? raw) =>
        (raw is { ValueKind: JsonValueKind.Object } element)
            ? (element, null)
            : (default, HandlerResult.InvalidParam("params", "'params' must be a JSON object."));

    /// <summary>The target in <paramref name="nodeIdParam"/> or <paramref name="selectorParam"/>; giving both is an error, neither is not.</summary>
    public static (ElementTarget Target, HandlerErrorResult? Error) ReadTarget(JsonElement element, string nodeIdParam, string selectorParam)
    {
        (int? nodeId, HandlerErrorResult? nodeIdError) = ReadOptionalNodeId(element, nodeIdParam);
        (string? selector, HandlerErrorResult? selectorError) = ReadOptionalString(element, selectorParam);
        var target = new ElementTarget(nodeId, selector, nodeIdParam, selectorParam);
        HandlerErrorResult? error = nodeIdError ?? selectorError;
        if ((error is null) && (nodeId is not null) && (selector is not null))
        {
            error = HandlerResult.InvalidParam(selectorParam, $"Give either '{nodeIdParam}' or '{selectorParam}', not both.");
        }

        return (target, error);
    }

    public static (int? Value, HandlerErrorResult? Error) ReadOptionalNodeId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        return ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int number))
            ? (number, null)
            : (null, HandlerResult.InvalidParam(name, $"'{name}' must be a node id from list_windows or get_tree."));
    }

    public static (string? Value, HandlerErrorResult? Error) ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        return (value.ValueKind == JsonValueKind.String)
            ? (value.GetString(), null)
            : (null, HandlerResult.InvalidParam(name, $"'{name}' must be a string."));
    }

    public static (string Value, HandlerErrorResult? Error) ReadRequiredString(JsonElement element, string name)
    {
        (string? value, HandlerErrorResult? error) = ReadOptionalString(element, name);
        if (error is not null)
        {
            return ("", error);
        }

        return (value is null) ? ("", HandlerResult.InvalidParam(name, $"'{name}' is required.")) : (value, null);
    }

    public static (double Value, HandlerErrorResult? Error) ReadRequiredNumber(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) && (value.ValueKind == JsonValueKind.Number) && value.TryGetDouble(out double number))
        {
            return (number, null);
        }

        return (0, HandlerResult.InvalidParam(name, $"'{name}' is required and must be a number."));
    }

    /// <summary>
    /// The whole number in <paramref name="name"/>, which is required and must be from <paramref name="min"/> to <paramref name="max"/>.
    /// </summary>
    public static (int Value, HandlerErrorResult? Error) ReadRequiredInt(JsonElement element, string name, int min, int max)
    {
        bool valid =
            element.TryGetProperty(name, out JsonElement value)
            && (value.ValueKind == JsonValueKind.Number)
            && value.TryGetInt32(out int number)
            && (number >= min)
            && (number <= max);
        return valid
            ? (value.GetInt32(), null)
            : (0, HandlerResult.InvalidParam(name, $"'{name}' is required and must be a whole number from {min} to {max}."));
    }

    /// <summary><c>button</c>, which is required: left, right or middle.</summary>
    public static (MouseButton Button, HandlerErrorResult? Error) ReadRequiredButton(JsonElement element)
    {
        (string button, HandlerErrorResult? error) = ReadRequiredString(element, "button");
        return (error is null) ? ParseButton(button) : (MouseButton.Left, error);
    }

    /// <summary>
    /// <c>button</c> (left, right or middle; default left), <c>modifiers</c> (default none) and <c>clickCount</c> (1 or 2;
    /// default 1).
    /// </summary>
    public static (PointerClick Click, HandlerErrorResult? Error) ReadPointerClick(JsonElement element)
    {
        (string? button, HandlerErrorResult? buttonError) = ReadOptionalString(element, "button");
        (string? modifiers, HandlerErrorResult? modifiersError) = ReadOptionalString(element, "modifiers");
        (int? clickCount, HandlerErrorResult? countError) = ReadOptionalCount(element);
        (MouseButton mouseButton, HandlerErrorResult? parsedButtonError) = ParseButton(button);
        (KeyModifiers keyModifiers, HandlerErrorResult? parsedModifiersError) = ParseModifiers(modifiers);
        HandlerErrorResult? error = buttonError ?? modifiersError ?? countError ?? parsedButtonError ?? parsedModifiersError;
        return (new PointerClick(mouseButton, keyModifiers, clickCount ?? 1), error);
    }

    private static (int? Count, HandlerErrorResult? Error) ReadOptionalCount(JsonElement element)
    {
        if (!element.TryGetProperty("clickCount", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        return ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int count) && (count is 1 or 2))
            ? (count, null)
            : (null, HandlerResult.InvalidParam("clickCount", "'clickCount' must be 1 (a click) or 2 (a double click)."));
    }

    /// <summary><paramref name="button"/> as a mouse button: left, right or middle in any case, and left when it is null.</summary>
    public static (MouseButton Button, HandlerErrorResult? Error) ParseButton(string? button) =>
        button?.ToLowerInvariant() switch
        {
            null or "left" => (MouseButton.Left, null),
            "right" => (MouseButton.Right, null),
            "middle" => (MouseButton.Middle, null),
            _ => (MouseButton.Left, HandlerResult.InvalidParam("button", "'button' must be \"left\", \"right\" or \"middle\".")),
        };

    // Derived from Zafiro.Avalonia.Mcp's KeyModifierParser (MIT License, Copyright (c) 2026 José Manuel Nieto), made strict:
    // an unknown key is an error rather than ignored.
    private static (KeyModifiers Modifiers, HandlerErrorResult? Error) ParseModifiers(string? modifiers)
    {
        KeyModifiers result = KeyModifiers.None;
        foreach (string part in (modifiers ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            KeyModifiers? modifier = ModifierNamed(part);
            if (modifier is null)
            {
                return (KeyModifiers.None, HandlerResult.InvalidParam("modifiers", $"Unknown modifier '{part}'. {ModifiersHint}"));
            }

            result |= modifier.Value;
        }

        return (result, null);
    }

    private static KeyModifiers? ModifierNamed(string part) =>
        part.ToLowerInvariant() switch
        {
            "ctrl" or "control" => KeyModifiers.Control,
            "shift" => KeyModifiers.Shift,
            "alt" => KeyModifiers.Alt,
            "meta" or "win" or "cmd" => KeyModifiers.Meta,
            _ => null,
        };
}
