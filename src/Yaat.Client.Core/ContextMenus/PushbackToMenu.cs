using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Push back to… rows over the host's <see cref="PushTargetList"/>, one builder for the All Commands submenu and the
/// quick-command strip's flyout: the taxilanes and taxiways behind the aircraft, then the taxi spots, each section under
/// its header and apart from the next by a separator, an empty section left out, every section by planned tug-path
/// length. A row shows the target's badge, name, note, distance hint and command, and under the name the facing chips
/// (<c>then face N S</c>), the one facing it ends on (<c>faces E</c>) or its blocker with a push-anyway chip. Clicking
/// the row sends the bare push, a facing chip the push ending on that facing, the push-anyway chip the forced push; a
/// blocked row's own click sends nothing. While the list is computing the rows give way to <see cref="ComputingText"/>;
/// while a seed shows, <see cref="RefiningText"/> leads them; once the list has settled with no target,
/// <see cref="NoTargetsText"/>. Either surface follows the list while it is open, refilling in place when the host's
/// live plan lands and keeping the row of every target that survives.
/// </summary>
public static class PushbackToMenu
{
    /// <summary>The header of the taxilane and taxiway section.</summary>
    public const string TaxiwaysHeader = "Behind the aircraft";

    /// <summary>The header of the spot section.</summary>
    public const string SpotsHeader = "Taxi spots";

    /// <summary>The disabled row a computing list shows.</summary>
    public const string ComputingText = "Computing targets…";

    /// <summary>The disabled row leading the seed's rows while the live plan is still running.</summary>
    public const string RefiningText = "Refining targets…";

    /// <summary>The disabled row a list shows once its live plan has left no target.</summary>
    public const string NoTargetsText = "No push targets from here";

    /// <summary>
    /// The submenu labelled <paramref name="label"/> over <paramref name="targets"/>, or null when the list is already
    /// settled with no targets: nothing was or will be planned for it. Its items follow the list as a submenu while it is
    /// open, and as the strip button's flyout while that is open (<see cref="QuickCommandStrip.FollowInFlyout"/>).
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
        var live = new LiveTargets(menu, targets, context, host);
        live.Attach();
        QuickCommandStrip.FollowInFlyout(menu, live);
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

    /// <summary>
    /// Moves <paramref name="items"/> to read <paramref name="wanted"/>, in order, touching only what differs: an item in
    /// both stays where it is unless the order moved it, so a surviving row keeps its pointer-over and its focus.
    /// </summary>
    private static void Sync(ItemCollection items, IReadOnlyList<Control> wanted)
    {
        var keep = new HashSet<object?>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
            {
                items.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if ((i < items.Count) && ReferenceEquals(items[i], wanted[i]))
            {
                continue;
            }

            int at = items.IndexOf(wanted[i]);
            if (at >= 0)
            {
                items.RemoveAt(at);
            }

            items.Insert(i, wanted[i]);
        }
    }

    private static MenuItem Placeholder(string text) => new() { Header = text, IsEnabled = false };

    private static MenuItem SectionHeader(string text) =>
        new()
        {
            Header = text,
            IsEnabled = false,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };

    /// <summary>
    /// The rows over a list that may still be filling, on whichever surface shows them: filled from the list when built,
    /// refilled when a surface opens if the list changed since, and refilled in place while one is open when the live plan
    /// lands. As a submenu it follows the list through <see cref="MenuItem.IsSubMenuOpen"/>; as the strip button's flyout,
    /// which moves the items out of the submenu, through the flyout's own opening and closing. A closed surface drops a
    /// plan that lands after it closed, and nothing keeps it subscribed. A target keeps its row, by its command, across a
    /// refill; a row whose target has gone sends nothing, even on a click already on its way.
    /// </summary>
    private sealed class LiveTargets(MenuItem entry, PushTargetList list, MenuContext context, IMenuHost host) : IStripFlyoutContent
    {
        private readonly MenuItem _computing = Placeholder(ComputingText);
        private readonly MenuItem _refining = Placeholder(RefiningText);
        private readonly MenuItem _noTargets = Placeholder(NoTargetsText);
        private readonly MenuItem _lanesHeader = SectionHeader(TaxiwaysHeader);
        private readonly MenuItem _spotsHeader = SectionHeader(SpotsHeader);
        private readonly Separator _separator = new();
        private readonly HashSet<MenuItem> _live = [];
        private Dictionary<string, MenuItem> _rows = new(StringComparer.Ordinal);
        private ItemCollection? _flyoutItems;
        private Action? _closeFlyout;
        private IReadOnlyList<MenuPushTarget>? _shownTargets;
        private bool _shownSettled;
        private bool _following;

        /// <summary>Fills the submenu and starts watching it open and close.</summary>
        public void Attach()
        {
            Show(list.Settled.IsCompleted);
            entry.PropertyChanged += (_, e) =>
            {
                if (e.Property != MenuItem.IsSubMenuOpenProperty)
                {
                    return;
                }

                if (entry.IsSubMenuOpen)
                {
                    Follow();
                }
                else
                {
                    Unfollow();
                }
            };
        }

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            _flyoutItems = items;
            _closeFlyout = close;
            Follow();
        }

        /// <inheritdoc />
        public void Closed()
        {
            Unfollow();
            _closeFlyout = null;
        }

        private void Follow()
        {
            if (_following)
            {
                return;
            }

            if (!ReferenceEquals(_shownTargets, list.Targets) || (_shownSettled != list.Settled.IsCompleted))
            {
                Show(list.Settled.IsCompleted);
            }

            list.Changed += OnLanded;
            _following = true;
        }

        private void Unfollow()
        {
            if (!_following)
            {
                return;
            }

            list.Changed -= OnLanded;
            _following = false;
        }

        private void OnLanded() => Show(list.Settled.IsCompleted);

        private void Show(bool settled)
        {
            // The strip moves the items into its flyout once, for good; until then they are the submenu's own.
            Sync(_flyoutItems ?? entry.Items, Wanted(settled));
            _shownTargets = list.Targets;
            _shownSettled = settled;
        }

        /// <summary>The items the list calls for now, reusing the row of every target whose command was shown before.</summary>
        private List<Control> Wanted(bool settled)
        {
            IReadOnlyList<MenuPushTarget> targets = list.Targets;
            if (targets.Count == 0)
            {
                _rows = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
                _live.Clear();
                return [settled ? _noTargets : _computing];
            }

            var rows = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            _live.Clear();
            List<Control> wanted = settled ? [] : [_refining];
            List<MenuPushTarget> lanes = [.. targets.Where(t => t.Kind != MenuPushTargetKind.Spot).OrderBy(t => t.PathLengthFt)];
            List<MenuPushTarget> spots = [.. targets.Where(t => t.Kind == MenuPushTargetKind.Spot).OrderBy(t => t.PathLengthFt)];
            AddSection(wanted, _lanesHeader, lanes, rows);
            if ((lanes.Count > 0) && (spots.Count > 0))
            {
                wanted.Add(_separator);
            }

            AddSection(wanted, _spotsHeader, spots, rows);
            _rows = rows;
            return wanted;
        }

        private void AddSection(List<Control> wanted, MenuItem header, List<MenuPushTarget> targets, Dictionary<string, MenuItem> rows)
        {
            if (targets.Count == 0)
            {
                return;
            }

            wanted.Add(header);
            foreach (MenuPushTarget target in targets)
            {
                // A command shown twice in one list gets a second row of its own, which no later refill reuses.
                MenuItem item = (!rows.ContainsKey(target.Command) && _rows.TryGetValue(target.Command, out MenuItem? kept)) ? kept : NewRow();
                rows.TryAdd(target.Command, item);
                Present(item, target);
                _live.Add(item);
                wanted.Add(item);
            }
        }

        private MenuItem NewRow()
        {
            var item = new MenuItem { HeaderTemplate = PushTargetRowTemplate.Instance };
            item.Click += async (_, e) =>
            {
                if (!_live.Contains(item) || (item.Header is not PushTargetRow { Target: { BlockedBy: null } target }))
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(target.Command);
            };
            return item;
        }

        /// <summary>Shows <paramref name="target"/> on <paramref name="item"/>: a blocked row stays open on its own click.</summary>
        private void Present(MenuItem item, MenuPushTarget target)
        {
            var row = new PushTargetRow(target, command => _live.Contains(item) ? ChooseAsync(command) : Task.CompletedTask);
            item.Header = row;
            item.StaysOpenOnClick = target.BlockedBy is not null;
            MenuCommandText.SetCommand(item, target.Command);
            AutomationProperties.SetName(item, row.ToString());
        }

        /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
        private async Task ChooseAsync(string command)
        {
            Task sending = host.SendAsync(context.Callsign, command, context.Initials);
            if (_closeFlyout is { } closeFlyout)
            {
                closeFlyout();
            }
            else
            {
                entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            }

            await sending;
        }
    }

    /// <summary>A chip under a row's name: its label and the command it sends.</summary>
    /// <param name="Label">What the chip reads: a cardinal (<c>N</c>) or <c>push anyway</c>.</param>
    /// <param name="Command">The command it sends: <c>PUSH TE FACE N</c> or <c>PUSHF TF</c>.</param>
    internal sealed record PushTargetChip(string Label, string Command);

    /// <summary>
    /// One Push back to… row, drawn by <see cref="PushTargetRowTemplate"/>: the target, the line under its name (the
    /// facing chips after <c>then face</c>, the one facing as <c>faces E</c>, or the blocker with a push-anyway chip) and
    /// the send its chips go through. Its text form, which the row's automation name uses, reads
    /// <c>TL TE · alongside ~250 ft PUSH TE</c>, then <c> · blocked by SWA919</c> for a blocked target.
    /// </summary>
    internal sealed class PushTargetRow
    {
        private readonly Func<string, Task> _choose;

        /// <summary>The row for <paramref name="target"/>, its chips sending through <paramref name="choose"/>.</summary>
        public PushTargetRow(MenuPushTarget target, Func<string, Task> choose)
        {
            Target = target;
            _choose = choose;
            if (target.BlockedBy is { } blocker)
            {
                Caption = $"blocked by {blocker}";
                Chips = [new PushTargetChip("push anyway", target.ForcedCommand)];
                return;
            }

            IReadOnlyList<MenuPushFacing> facings = target.Facings;
            if (facings.Count > 1)
            {
                Caption = "then face";
                Chips = [.. facings.Select(f => new PushTargetChip(f.Cardinal, f.Command))];
            }
            else if (facings.Count == 1)
            {
                Caption = $"faces {facings[0].Cardinal}";
            }
        }

        /// <summary>The target the row stands for.</summary>
        public MenuPushTarget Target { get; }

        /// <summary>The badge letters the row shows.</summary>
        public string Badge => BadgeText(Target.Kind);

        /// <summary>Whether a neighbour blocks the target, which fades the row and leaves its own click sending nothing.</summary>
        public bool IsBlocked => Target.BlockedBy is not null;

        /// <summary>The text leading the line under the name, or null for a row with no such line.</summary>
        public string? Caption { get; }

        /// <summary>The chips after <see cref="Caption"/>, in order.</summary>
        public IReadOnlyList<PushTargetChip> Chips { get; } = [];

        /// <summary>Sends <paramref name="command"/> as a chip of the row does, closing the menu.</summary>
        public Task ChooseAsync(string command) => _choose(command);

        /// <inheritdoc />
        public override string ToString()
        {
            var text = new StringBuilder($"{Badge} {DisplayName(Target)}");
            if (Target.Note is { } note)
            {
                text.Append(" · ").Append(note);
            }

            text.Append(' ').Append(DistanceHint(Target.PathLengthFt)).Append(' ').Append(Target.Command);
            if (Target.BlockedBy is { } blocker)
            {
                text.Append(" · blocked by ").Append(blocker);
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// The view of a <see cref="PushTargetRow"/>: the badge, the name in a semibold face with its dim note and the chip
    /// line under it, the distance hint, then the command right-aligned in the dim monospace font. A blocked row is drawn
    /// faded to <see cref="MenuGlyphRowTemplate.DimmedOpacity"/> but for its chip line, which stays clickable.
    /// </summary>
    internal sealed class PushTargetRowTemplate : FuncDataTemplate<PushTargetRow>
    {
        private const string SubtleTextBrushKey = "SubtleTextBrush";

        /// <summary>The one template every push-target row's menu item uses.</summary>
        public static PushTargetRowTemplate Instance { get; } = new();

        private PushTargetRowTemplate()
            : base((row, _) => Build(row)) { }

        private static Grid Build(PushTargetRow row)
        {
            double opacity = row.IsBlocked ? MenuGlyphRowTemplate.DimmedOpacity : 1;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto,96"), ColumnSpacing = 10 };

            Border badge = BadgeBorder(row.Target.Kind);
            badge.Opacity = opacity;
            grid.Children.Add(badge);

            var nameColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            StackPanel nameLine = NameLine(row.Target);
            nameLine.Opacity = opacity;
            nameColumn.Children.Add(nameLine);
            if (row.Caption is { } caption)
            {
                nameColumn.Children.Add(ChipLine(row, caption));
            }

            Grid.SetColumn(nameColumn, 1);
            grid.Children.Add(nameColumn);

            var hint = new TextBlock
            {
                Text = DistanceHint(row.Target.PathLengthFt),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);

            TextBlock command = Subtle(row.Target.Command);
            command.Opacity = opacity;
            command.HorizontalAlignment = HorizontalAlignment.Right;
            command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
            Grid.SetColumn(command, 3);
            grid.Children.Add(command);
            return grid;
        }

        /// <summary>The name in a semibold face, then the note in the dim face.</summary>
        private static StackPanel NameLine(MenuPushTarget target)
        {
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            nameLine.Children.Add(new TextBlock { Text = DisplayName(target), FontWeight = FontWeight.SemiBold });
            if (target.Note is { } note)
            {
                nameLine.Children.Add(Subtle($"· {note}"));
            }

            return nameLine;
        }

        /// <summary>The line under the name: <paramref name="caption"/> in the dim face, then the row's chips.</summary>
        private static StackPanel ChipLine(PushTargetRow row, string caption)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            line.Children.Add(Subtle(caption));
            foreach (PushTargetChip chip in row.Chips)
            {
                line.Children.Add(ChipButton(row, chip));
            }

            return line;
        }

        /// <summary>A chip: a small monospace button that sends its command, its click kept from the row underneath.</summary>
        private static Button ChipButton(PushTargetRow row, PushTargetChip chip)
        {
            var button = new Button
            {
                Content = chip.Label,
                FontSize = 12,
                Padding = new Thickness(6, 0),
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Bind(TemplatedControl.FontFamilyProperty, button.GetResourceObservable(QuickCommandStrip.MonoFontKey));
            AutomationProperties.SetName(button, chip.Command);
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                await row.ChooseAsync(chip.Command);
            };
            return button;
        }

        private static TextBlock Subtle(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(SubtleTextBrushKey));
            return block;
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
