using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Push back to… submenu over the host's <see cref="PushTargetList"/>: the taxilanes and taxiways behind the
/// aircraft, then the taxi spots, each section under its header and apart from the next by a separator, an empty section
/// left out, every section by planned tug-path length. A row shows the target's badge, name, note, distance hint and
/// command; clicking it sends the command, and a taxiway row's arrow opens its facings. A blocked row is disabled and
/// names its blocker. While the list is computing the submenu shows <see cref="ComputingText"/>, and once it has
/// settled with no target, <see cref="NoTargetsText"/>. The submenu follows the list while it is open: when the host's
/// live plan lands it rebuilds its items in place, and a submenu opened after that shows the landed targets.
/// </summary>
public static class PushbackToMenu
{
    /// <summary>The header of the taxilane and taxiway section.</summary>
    public const string TaxiwaysHeader = "Taxilanes and taxiways behind the aircraft";

    /// <summary>The header of the spot section.</summary>
    public const string SpotsHeader = "Taxi spots";

    /// <summary>The disabled row a computing list shows.</summary>
    public const string ComputingText = "Computing targets…";

    /// <summary>The disabled row a list shows once its live plan has left no target.</summary>
    public const string NoTargetsText = "No push targets from here";

    private const string SubtleTextBrushKey = "SubtleTextBrush";
    private const string MonoFontKey = "MonoFont";

    /// <summary>
    /// The submenu labelled <paramref name="label"/> over <paramref name="targets"/>, or null when the list is already
    /// settled with no targets: nothing was or will be planned for it.
    /// </summary>
    /// <param name="label">The submenu's text.</param>
    /// <param name="targets">The host's targets, which may still be filling.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the rows send through.</param>
    /// <returns>The submenu, or null.</returns>
    public static MenuItem? Build(string label, PushTargetList targets, MenuContext context, IMenuHost host)
    {
        if (targets.Settled.IsCompleted && (targets.Targets.Count == 0))
        {
            return null;
        }

        var menu = new MenuItem { Header = label };
        new LiveSubmenu(menu, targets, context, host).Attach();
        return menu;
    }

    /// <summary>The distance hint a row shows: the tow rounded to 5 ft with a thousands separator, <c>~1,360 ft</c>.</summary>
    public static string DistanceHint(double pathLengthFt) =>
        $"~{(Math.Round(pathLengthFt / 5.0, MidpointRounding.AwayFromZero) * 5.0).ToString("N0", CultureInfo.InvariantCulture)} ft";

    /// <summary>The name a row shows: a spot as <c>Spot E</c>, a taxilane or taxiway by its own name.</summary>
    public static string DisplayName(MenuPushTarget target) => (target.Kind == MenuPushTargetKind.Spot) ? $"Spot {target.Name}" : target.Name;

    /// <summary>The letters of a target kind's badge: TL, TW or S.</summary>
    public static string BadgeText(MenuPushTargetKind kind) =>
        kind switch
        {
            MenuPushTargetKind.Taxilane => "TL",
            MenuPushTargetKind.Taxiway => "TW",
            MenuPushTargetKind.Spot => "S",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown push target kind."),
        };

    /// <summary>The facing items a taxiway row's arrow opens, in order; empty for a row with no arrow.</summary>
    internal static IReadOnlyList<MenuItem> Facings(MenuItem row) =>
        (row.Header is PushTargetRow { FacingsButton: { } arrow } && (FlyoutBase.GetAttachedFlyout(arrow) is MenuFlyout flyout))
            ? [.. flyout.Items.OfType<MenuItem>()]
            : [];

    /// <summary>The submenu's items for the list as it stands: the placeholder while computing, else the sections.</summary>
    private static void Fill(ItemCollection items, PushTargetList list, MenuContext context, IMenuHost host)
    {
        if (list.Computing)
        {
            items.Add(Placeholder(ComputingText));
            return;
        }

        AddSection(
            items,
            TaxiwaysHeader,
            [.. list.Targets.Where(t => t.Kind != MenuPushTargetKind.Spot).OrderBy(t => t.PathLengthFt)],
            context,
            host
        );
        AddSection(items, SpotsHeader, [.. list.Targets.Where(t => t.Kind == MenuPushTargetKind.Spot).OrderBy(t => t.PathLengthFt)], context, host);
        if (items.Count == 0)
        {
            items.Add(Placeholder(NoTargetsText));
        }
    }

    private static MenuItem Placeholder(string text) => new() { Header = text, IsEnabled = false };

    private static void AddSection(ItemCollection items, string header, List<MenuPushTarget> targets, MenuContext context, IMenuHost host)
    {
        if (targets.Count == 0)
        {
            return;
        }

        if (items.Count > 0)
        {
            items.Add(new Separator());
        }

        items.Add(
            new MenuItem
            {
                Header = header,
                IsEnabled = false,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            }
        );
        foreach (MenuPushTarget target in targets)
        {
            items.Add(Row(target, context, host));
        }
    }

    /// <summary>One target's row: it sends the target's command when clicked, unless a neighbour blocks it.</summary>
    private static MenuItem Row(MenuPushTarget target, MenuContext context, IMenuHost host)
    {
        var row = new PushTargetRow(target);
        var item = new MenuItem { Header = row, Tag = target };
        MenuCommandText.SetCommand(item, target.Command);
        AutomationProperties.SetName(item, row.ToString());
        if (target.BlockedBy is not null)
        {
            item.IsEnabled = false;
        }
        else
        {
            item.Click += async (_, _) => await host.SendAsync(context.Callsign, target.Command, context.Initials);
        }

        if (target.Facings.Count > 0)
        {
            row.AddFacings(FacingsFlyout(item, target.Facings, context, host));
        }

        return item;
    }

    /// <summary>The flyout of facing choices a taxiway row's arrow opens; a choice sends its command and closes the menu.</summary>
    private static MenuFlyout FacingsFlyout(MenuItem row, IReadOnlyList<MenuCommandChoice> facings, MenuContext context, IMenuHost host)
    {
        var flyout = new MenuFlyout();
        foreach (MenuCommandChoice facing in facings.Where(f => f.Command is not null))
        {
            MenuItem item = MenuCatalog.BuildSend(facing.Label, facing.Command!, context, host);
            item.Click += (_, _) =>
            {
                flyout.Hide();
                row.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            };
            flyout.Items.Add(item);
        }

        return flyout;
    }

    private static TextBlock Subtle(string text, double fontSize)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(SubtleTextBrushKey));
        return block;
    }

    /// <summary>
    /// The submenu over a list that may still be filling: filled from the list when built, refilled when it opens if the
    /// list changed since, and refilled in place while it is open when the list changes. It follows the list only while
    /// open, so a closed menu drops a plan that lands after it closed and nothing keeps it subscribed.
    /// </summary>
    private sealed class LiveSubmenu(MenuItem menu, PushTargetList list, MenuContext context, IMenuHost host)
    {
        private IReadOnlyList<MenuPushTarget>? _shownTargets;
        private bool _shownComputing;
        private bool _following;

        /// <summary>Fills the submenu and starts watching it open and close.</summary>
        public void Attach()
        {
            Refill();
            menu.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.IsSubMenuOpenProperty)
                {
                    OnOpenChanged(menu.IsSubMenuOpen);
                }
            };
        }

        private void OnOpenChanged(bool open)
        {
            if (open && !_following)
            {
                if (!ReferenceEquals(_shownTargets, list.Targets) || (_shownComputing != list.Computing))
                {
                    Refill();
                }

                list.Changed += Refill;
                _following = true;
            }
            else if (!open && _following)
            {
                list.Changed -= Refill;
                _following = false;
            }
        }

        private void Refill()
        {
            menu.Items.Clear();
            Fill(menu.Items, list, context, host);
            _shownTargets = list.Targets;
            _shownComputing = list.Computing;
        }
    }

    /// <summary>
    /// A Push back to… row's content, laid out as a grid: the badge, the name with its note and blocker, the distance
    /// hint, the command in a dim monospace face, and for a taxiway the arrow that opens its facings. Its text form,
    /// which the row's automation name and the menu goldens use, reads the same left to right.
    /// </summary>
    internal sealed class PushTargetRow : Grid
    {
        private readonly string _text;

        /// <summary>The row for <paramref name="target"/>.</summary>
        public PushTargetRow(MenuPushTarget target)
        {
            Target = target;
            ColumnDefinitions = new ColumnDefinitions("28,*,Auto,96,Auto");
            ColumnSpacing = 10;
            string name = DisplayName(target);
            string distance = DistanceHint(target.PathLengthFt);
            string note = target.Note is { } shownNote ? $" · {shownNote}" : "";
            string blocked = target.BlockedBy is { } blocker ? $" · blocked by {blocker}" : "";
            _text = $"{BadgeText(target.Kind)} {name}{note} {distance} {target.Command}{blocked}";

            Children.Add(BadgeBorder(target.Kind));
            StackPanel nameLine = NameLine(name, target);
            SetColumn(nameLine, 1);
            Children.Add(nameLine);
            var hint = new TextBlock { Text = distance, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(hint, 2);
            Children.Add(hint);
            TextBlock command = Subtle(target.Command, 12);
            command.HorizontalAlignment = HorizontalAlignment.Right;
            command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(MonoFontKey));
            SetColumn(command, 3);
            Children.Add(command);
        }

        /// <summary>The target the row stands for.</summary>
        public MenuPushTarget Target { get; }

        /// <summary>The badge letters the row shows.</summary>
        public string Badge => BadgeText(Target.Kind);

        /// <summary>The arrow that opens the facings, or null for a row without facings.</summary>
        public Button? FacingsButton { get; private set; }

        /// <inheritdoc />
        public override string ToString() => _text;

        /// <summary>Adds the arrow that opens <paramref name="flyout"/>, disabled when a neighbour blocks the target.</summary>
        internal void AddFacings(MenuFlyout flyout)
        {
            var arrow = new Button
            {
                Content = "›",
                Padding = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = Target.BlockedBy is null,
            };
            AutomationProperties.SetName(arrow, $"Facings for {DisplayName(Target)}");
            FlyoutBase.SetAttachedFlyout(arrow, flyout);
            arrow.Click += (_, e) =>
            {
                e.Handled = true;
                FlyoutBase.ShowAttachedFlyout(arrow);
            };
            SetColumn(arrow, 4);
            Children.Add(arrow);
            FacingsButton = arrow;
        }

        /// <summary>The name in a semibold face, then the note and the blocker in the dim face.</summary>
        private static StackPanel NameLine(string name, MenuPushTarget target)
        {
            var nameLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameLine.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            if (target.Note is { } note)
            {
                nameLine.Children.Add(Subtle($"· {note}", 12));
            }

            if (target.BlockedBy is { } blocker)
            {
                nameLine.Children.Add(Subtle($"· blocked by {blocker}", 12));
            }

            return nameLine;
        }

        private static Border BadgeBorder(MenuPushTargetKind kind)
        {
            (string background, string foreground, double radius) = kind switch
            {
                MenuPushTargetKind.Taxilane => ("#E0A84A", "#1B1B1B", 4.0),
                MenuPushTargetKind.Taxiway => ("#F2D04B", "#1B1B1B", 4.0),
                MenuPushTargetKind.Spot => ("#7FD1B9", "#10241E", 11.0),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown push target kind."),
            };
            return new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.Parse(background)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = BadgeText(kind),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(foreground)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
    }
}
