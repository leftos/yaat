using Avalonia.Controls;
using Avalonia.Input;

namespace Yaat.Client.Views;

internal static class ContextMenuExtensions
{
    public static void AddCommandTextBox(this ContextMenu menu, Func<string, Task> onSubmit)
    {
        var textBox = new TextBox
        {
            Watermark = "Command",
            FontSize = 12,
            MinWidth = 160,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    menu.Close();
                    await onSubmit(text);
                }
            }
        };
        menu.Items.Add(textBox);
    }
}
