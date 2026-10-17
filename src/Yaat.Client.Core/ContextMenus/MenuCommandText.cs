using Avalonia;
using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The command text a sending menu item, or a designed flyout's row button, sends, recorded on it by
/// <see cref="MenuCatalog.BuildSend"/> and the flyout builders so a surface that shows it another way (the quick-command
/// strip's tooltip) can name it without restating it.
/// </summary>
public sealed class MenuCommandText
{
    /// <summary>The command the item sends when clicked; null on an item that sends no fixed command.</summary>
    public static readonly AttachedProperty<string?> CommandProperty = AvaloniaProperty.RegisterAttached<MenuCommandText, Control, string?>(
        "Command"
    );

    private MenuCommandText() { }

    /// <summary>The command <paramref name="item"/> sends, or null when it sends no fixed command.</summary>
    public static string? GetCommand(Control item) => item.GetValue(CommandProperty);

    /// <summary>Records <paramref name="command"/> as the command <paramref name="item"/> sends.</summary>
    public static void SetCommand(Control item, string? command) => item.SetValue(CommandProperty, command);
}
