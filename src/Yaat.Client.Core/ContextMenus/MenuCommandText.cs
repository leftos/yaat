using Avalonia;
using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The command text a sending menu item sends, recorded on the item by <see cref="MenuCatalog.BuildSend"/> so a surface
/// that shows the item another way (the quick-command strip's tooltip) can name it without restating it.
/// </summary>
public sealed class MenuCommandText
{
    /// <summary>The command the item sends when clicked; null on an item that sends no fixed command.</summary>
    public static readonly AttachedProperty<string?> CommandProperty = AvaloniaProperty.RegisterAttached<MenuCommandText, MenuItem, string?>(
        "Command"
    );

    private MenuCommandText() { }

    /// <summary>The command <paramref name="item"/> sends, or null when it sends no fixed command.</summary>
    public static string? GetCommand(MenuItem item) => item.GetValue(CommandProperty);

    /// <summary>Records <paramref name="command"/> as the command <paramref name="item"/> sends.</summary>
    public static void SetCommand(MenuItem item, string? command) => item.SetValue(CommandProperty, command);
}
