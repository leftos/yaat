using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The command alias a catalog row shows beside its label (<see cref="MenuAliases.Display"/>), recorded on the row's
/// <see cref="MenuItem"/> and drawn by <see cref="MenuAliasTemplate"/>, so a mouse user learns the typed command. The
/// row's header stays its string label.
/// </summary>
public sealed class MenuAlias
{
    /// <summary>The alias the row shows; null on a row that shows none.</summary>
    public static readonly AttachedProperty<string?> TextProperty = AvaloniaProperty.RegisterAttached<MenuAlias, Control, string?>("Text");

    private MenuAlias() { }

    /// <summary>The alias <paramref name="item"/> shows, or null when it shows none.</summary>
    public static string? GetText(Control item) => item.GetValue(TextProperty);

    /// <summary>Records <paramref name="text"/> as the alias <paramref name="item"/> shows.</summary>
    public static void SetText(Control item, string? text) => item.SetValue(TextProperty, text);

    /// <summary>
    /// Gives <paramref name="item"/>, built from the catalog entry <paramref name="id"/>, its alias under the controller's
    /// command scheme and the template that draws it, when its header is a plain string label with no template of its own
    /// and the entry sends a verb of its own; leaves it as it is otherwise. An alias the entry's builder already set, from
    /// the one verb that build sends, is kept. The row's automation name stays its label.
    /// </summary>
    public static void Apply(string id, MenuItem item, MenuContext context)
    {
        if ((item.Header is not string label) || (item.HeaderTemplate is not null))
        {
            return;
        }

        if ((GetText(item) ?? MenuAliases.Display(id, context.Scheme)) is not { } alias)
        {
            return;
        }

        SetText(item, alias);
        item.HeaderTemplate = MenuAliasTemplate.Instance;
        AutomationProperties.SetName(item, label);
    }
}

/// <summary>
/// The view of a row carrying a <see cref="MenuAlias"/>: the label on the left, then the alias read from the owning
/// <see cref="MenuItem"/>, right-aligned in the dimmed monospace font <see cref="MenuCommandRowTemplate"/> draws a command in,
/// so the verbs of a submenu line up in a column.
/// </summary>
public sealed class MenuAliasTemplate : FuncDataTemplate<string>
{
    /// <summary>The one template every aliased row uses.</summary>
    public static MenuAliasTemplate Instance { get; } = new();

    private MenuAliasTemplate()
        : base((label, _) => Build(label)) { }

    private static Grid Build(string label)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var alias = new TextBlock
        {
            FontSize = MenuCommandRow.CommandFontSize,
            Opacity = MenuCommandRow.CommandOpacity,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        alias.Bind(TextBlock.FontFamilyProperty, alias.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        IDisposable? binding = null;
        alias.AttachedToVisualTree += (_, _) =>
        {
            binding?.Dispose();
            binding =
                (alias.FindAncestorOfType<MenuItem>() is { } item)
                    ? alias.Bind(TextBlock.TextProperty, item.GetObservable(MenuAlias.TextProperty))
                    : null;
        };
        alias.DetachedFromVisualTree += (_, _) =>
        {
            binding?.Dispose();
            binding = null;
        };
        Grid.SetColumn(alias, 1);
        grid.Children.Add(alias);
        return grid;
    }
}
