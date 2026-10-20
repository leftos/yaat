using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>Which command a helicopter destination flyout sends.</summary>
public enum AirTaxiVerb
{
    /// <summary><c>ATXI</c>, from the ground: pads and parking, spots and runway holding positions.</summary>
    AirTaxi,

    /// <summary><c>LAND</c>, from the air: pads and parking and spots, with the Keep NODEL box.</summary>
    Land,
}

/// <summary>
/// The helicopter Air taxi and Land flyout over the host's destinations (<see cref="IMenuHost.GetAirTaxiChoices"/>), one
/// builder for both verbs and for the All Commands submenu and the quick-command strip's flyout. On top, the verb's glyph,
/// an "Air taxi to" or "Land at" filter box and, for Land, a Keep NODEL box, over a hint line; under them a
/// <see cref="FlyoutTabStrip"/> with one tab per group (Pads · parking, Spots and, for Air taxi, Runways; an empty group's
/// tab dimmed <c>none</c>) above the selected tab's rows, nearest first: helipads, then the nearest
/// <see cref="ParkingShown"/> parking positions and a line naming how many more the filter reaches; spots; runway ends, each
/// one row for the holding position its bare designator names with its other bars as chips under it, each chip sending its
/// located form; a runway row whose bar the aircraft holds at reads <see cref="HeldAtText"/> and sends nothing, only its
/// chips do, and the filter leaves it out. Every row shows its name, what it is, its straight-line distance and the
/// command it sends. Typing in the box hides the tabs and lists every group's matches by name prefix (a runway end by its
/// designator or its row's taxiway), pads and parking, then spots, then runways, each an exact name first, then nearest;
/// Enter sends the top match, and nothing with the box empty or nothing matched. While Keep NODEL is ticked every row and Enter send the command with
/// <c> NODEL</c>. Land's box label names the airport the rows are at (<c>Land at OAK</c>); when that airport is not the
/// aircraft's filed destination, every row and Enter send <c>APT</c> of it first (<c>APT OAK, LAND @HELI1</c>), as the sim
/// otherwise resolves the name at the airport it picks for the aircraft. The box and the tick start clear and the Pads ·
/// parking tab selected each time the flyout opens, focus in the box. Down from the box focuses the first row; Up/Down and
/// Tab/Shift+Tab walk the rows and chips, Up from the first back to the box; Left/Right choose the previous or next tab
/// without wrapping and focus its first row. A row or chip sends its command and closes the flyout and the menu. While
/// the strip's flyout is open the rows refill once a second (<see cref="FlyoutLiveTick"/>), re-sorted nearest first with
/// the pointer outside; with the pointer inside the order holds and only the distances update, catching up once the
/// pointer leaves. The All Commands submenu fills as it opens.
/// </summary>
public static class AirTaxiFlyout
{
    /// <summary>The parking rows the Pads · parking tab lists before its "more" line.</summary>
    public const int ParkingShown = 8;

    /// <summary>What a tab's panel reads when its group has no destination.</summary>
    public const string NoDestinationText = "No destination found";

    /// <summary>What the panel reads when the box matches no destination.</summary>
    public const string NoMatchText = "No destination matches";

    /// <summary>The Land flyout's check box, which appends <c>NODEL</c> to every command.</summary>
    public const string KeepNodelText = "Keep NODEL";

    /// <summary>The tallest the rows panel grows below the tabs before it scrolls.</summary>
    public const double RowsMaxHeight = 420;

    /// <summary>What a runway row whose bar the aircraft holds at says in place of a command; only its chips send.</summary>
    public const string HeldAtText = "holding short here";

    private const string LiveOrder = "nearest first · live";

    /// <summary>What a row's key starts with among a render's element keys, beside <c>t:</c> for a text line and <c>c:</c> for chips.</summary>
    private const string RowKeyPrefix = "b:";

    /// <summary>What a held runway row's key starts with: a text line, ranked with the rows while the pointer holds the order.</summary>
    private const string HeldRowKeyPrefix = "h:";

    /// <summary>The class of the menu row that holds the whole panel, kept at its resting background.</summary>
    private const string PanelRowClass = "air-taxi-panel";

    private static readonly ILogger Log = AppLog.CreateLogger("AirTaxiFlyout");

    private static readonly IImmutableSolidColorBrush ChipLine = new ImmutableSolidColorBrush(Color.Parse("#3A414B"));

    /// <summary>
    /// The <paramref name="verb"/> entry labelled <paramref name="label"/>: one menu row holding the box, the tabs and the
    /// rows, filled when the flyout or the submenu opens.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="verb">The command the rows send.</param>
    /// <param name="aircraft">The menu's aircraft, whose filed destination decides whether Land loads the listed airport first.</param>
    /// <param name="context">The menu's callsign and initials.</param>
    /// <param name="host">The host the destinations come from and the rows send through.</param>
    public static MenuItem Build(string label, AirTaxiVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        QuickCommandStrip.FollowInFlyout(entry, new AirTaxiContent(entry, verb, aircraft, context, host));
        return entry;
    }

    /// <summary>The command word <paramref name="verb"/> sends: <c>ATXI</c> or <c>LAND</c>.</summary>
    public static string CommandWord(AirTaxiVerb verb) => (verb == AirTaxiVerb.Land) ? "LAND" : "ATXI";

    /// <summary>The words one verb's flyout shows.</summary>
    private sealed record Wording(string BoxLabel, string Placeholder, string IdleHint, string IdleHintWithNodel, IReadOnlyList<string> Tabs);

    private static Wording WordingOf(AirTaxiVerb verb) =>
        (verb == AirTaxiVerb.Land)
            ? new Wording(
                "Land at",
                "Filter pads, parking, spots",
                "Type to filter both groups; Enter sends the top match.",
                "Type to filter both groups; Enter sends the top match with NODEL.",
                ["Pads · parking", "Spots"]
            )
            : new Wording(
                "Air taxi to",
                "Filter pads, parking, spots, runways",
                "Type to filter every group; Enter sends the top match.",
                "Type to filter every group; Enter sends the top match.",
                ["Pads · parking", "Spots", "Runways"]
            );

    /// <summary>What a destination is, as its row says it under the name; null for a parking position.</summary>
    private static string? Note(AirTaxiDestination destination) =>
        destination.Kind switch
        {
            AirTaxiDestinationKind.Helipad => "helipad",
            AirTaxiDestinationKind.Spot => "spot",
            AirTaxiDestinationKind.Runway => (destination.BarTaxiway is { } taxiway) ? $"full length at {taxiway}" : "full length",
            _ => null,
        };

    /// <summary>The line after the shown parking rows: how many more there are and the nearest and farthest of them.</summary>
    private static string MoreParkingText(IReadOnlyList<AirTaxiDestination> parking) =>
        $"{parking.Count - ParkingShown} more parking positions below, nearest first ({parking[ParkingShown].Name} at "
        + $"{HoldShortLadder.FeetText(parking[ParkingShown].DistanceFt)} … {parking[^1].Name} at {HoldShortLadder.FeetText(parking[^1].DistanceFt)})";

    /// <summary>Whether <paramref name="destination"/> matches <paramref name="filter"/> by name prefix, and exactly.</summary>
    private static (bool Matches, bool Exact) Match(AirTaxiDestination destination, string filter)
    {
        bool isRunway = destination.Kind == AirTaxiDestinationKind.Runway;
        List<string> names = [isRunway ? destination.Token : destination.Name];
        if (isRunway && (destination.BarTaxiway is { } taxiway))
        {
            names.Add(taxiway);
        }

        bool exact = names.Any(name => string.Equals(name, filter, StringComparison.OrdinalIgnoreCase));
        return (exact || names.Any(name => name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)), exact);
    }

    /// <summary>
    /// The flyout's content on whichever surface shows it: the host's destinations, asked for as it opens and once a second
    /// while the strip's flyout is open, the box, the selected tab's rows, the keys and the sends.
    /// </summary>
    private sealed class AirTaxiContent : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly AirTaxiVerb _verb;
        private readonly IMenuAircraft? _aircraft;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly Wording _wording;
        private readonly QuickCommandGlyph _glyph;
        private readonly AirTaxiPanel _panel;
        private readonly FlyoutLiveTick _tick;
        private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TextBlock> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Chips, WrapPanel Line)> _chipLines = new(StringComparer.Ordinal);

        private AirTaxiMenu _menu = AirTaxiMenu.Empty;
        private List<string> _shownKeys = [];
        private Dictionary<string, int>? _frozenRank;
        private Action? _closeFlyout;
        private bool _open;

        public AirTaxiContent(MenuItem entry, AirTaxiVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _verb = verb;
            _aircraft = aircraft;
            _context = context;
            _host = host;
            _wording = WordingOf(verb);
            string glyphId = (verb == AirTaxiVerb.Land) ? MenuIds.GroundLandHelicopter : MenuIds.GroundAirTaxi;
            _glyph = QuickCommandGlyphs.For(glyphId) ?? throw new InvalidOperationException($"No glyph for {glyphId}");
            _panel = new AirTaxiPanel(_glyph, _wording.BoxLabel, _wording.Placeholder, verb == AirTaxiVerb.Land);
            _tick = new FlyoutLiveTick(host.Clock, Refill);
            _panel.Strip.SelectionChanged += (_, _) =>
            {
                Render(resetScroll: true);
                FocusFirstRowLater();
            };
            _panel.Box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    Render(resetScroll: true);
                }
            };
            if (_panel.KeepNodel is { } keep)
            {
                keep.IsCheckedChanged += (_, _) => Render(resetScroll: false);
            }

            _panel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            // A menu row takes keyboard focus back from whatever inside it gets it, so the row holding the panel is not
            // focusable, and focus stays on the box, the check box or a row button.
            var panelRow = new MenuItem
            {
                Header = _panel,
                StaysOpenOnClick = true,
                Focusable = false,
            };
            panelRow.Classes.Add(PanelRowClass);
            panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(PanelRowClass));
            panelRow.Click += (_, e) => e.Handled = true;
            entry.Items.Add(panelRow);
            entry.PropertyChanged += (_, e) =>
            {
                if (e.Property != MenuItem.IsSubMenuOpenProperty)
                {
                    return;
                }

                if (entry.IsSubMenuOpen)
                {
                    Open(null);
                }
                else
                {
                    Closed();
                }
            };
        }

        private bool KeepsAfterLanding => _panel.KeepNodel?.IsChecked == true;

        private string Filter => (_panel.Box.Text ?? "").Trim();

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            Open(close);
            _tick.Start();
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if (!_tick.IsRunning || (inside == (_frozenRank is not null)))
            {
                return;
            }

            _frozenRank = inside
                ? _shownKeys
                    .Where(key =>
                        key.StartsWith(RowKeyPrefix, StringComparison.Ordinal) || key.StartsWith(HeldRowKeyPrefix, StringComparison.Ordinal)
                    )
                    .Select((key, index) => (Key: key[(key.IndexOf(':') + 1)..], Index: index))
                    .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal)
                : null;
            if (!inside)
            {
                Render(resetScroll: false);
            }
        }

        /// <inheritdoc />
        public void Closed()
        {
            _open = false;
            _tick.Stop();
            _frozenRank = null;
            _closeFlyout = null;
        }

        private void Open(Action? close)
        {
            _closeFlyout = close;
            _open = true;
            _frozenRank = null;
            _menu = _host.GetAirTaxiChoices(_context.Callsign);
            _panel.SetBoxLabel(BoxLabelOf(_menu));
            _panel.Box.Text = "";
            if (_panel.KeepNodel is { } keep)
            {
                keep.IsChecked = false;
            }

            _panel.Strip.SetTabs([.. _wording.Tabs.Select((label, index) => new FlyoutTab(label, StateOf(index)))]);
            if (_panel.Strip.SelectedIndex > 0)
            {
                // Choosing the first tab renders it.
                _panel.Strip.Select(0);
            }
            else
            {
                Render(resetScroll: true);
            }

            FocusBoxLater();
        }

        private void Refill()
        {
            _menu = _host.GetAirTaxiChoices(_context.Callsign);
            _panel.SetBoxLabel(BoxLabelOf(_menu));
            for (int index = 0; index < _wording.Tabs.Count; index++)
            {
                _panel.Strip.SetState(index, StateOf(index));
            }

            Render(resetScroll: false);
        }

        private FlyoutTabState StateOf(int tab) => (GroupOf(tab).Count == 0) ? FlyoutTabState.Empty : FlyoutTabState.Ready;

        private List<AirTaxiDestination> GroupOf(int tab) =>
            tab switch
            {
                0 => [.. _menu.Pads, .. _menu.Parking],
                1 => [.. _menu.Spots],
                _ => [.. _menu.Runways],
            };

        private void Render(bool resetScroll)
        {
            var shown = new AirTaxiPanelContent();
            string filter = Filter;
            _panel.Strip.IsVisible = (filter.Length == 0) && (_panel.Strip.Tabs.Count > 0);
            if (filter.Length == 0)
            {
                RenderTab(shown);
            }
            else
            {
                RenderMatches(shown, filter);
            }

            _shownKeys = [.. shown.Keys];
            _panel.Show(shown, resetScroll);
            _panel.Hint.Text = HintText(filter, shown);
        }

        private string HintText(string filter, AirTaxiPanelContent shown)
        {
            if (filter.Length == 0)
            {
                return KeepsAfterLanding ? _wording.IdleHintWithNodel : _wording.IdleHint;
            }

            return (TopMatch(shown) is { } command) ? $"Enter sends {command}, the first match." : "Nothing to send.";
        }

        private static string? TopMatch(AirTaxiPanelContent shown) => (shown.Buttons.Count > 0) ? MenuCommandText.GetCommand(shown.Buttons[0]) : null;

        private void RenderTab(AirTaxiPanelContent shown)
        {
            switch (_panel.Strip.SelectedIndex)
            {
                case 0 when (_menu.Pads.Count + _menu.Parking.Count) > 0:
                    if (_menu.Pads.Count > 0)
                    {
                        AddTitle(shown, "Helipads", "");
                        AddRows(shown, Held(_menu.Pads), withChips: false);
                    }

                    if (_menu.Parking.Count > 0)
                    {
                        AddTitle(shown, "Parking", LiveOrder);
                        List<AirTaxiDestination> parking = Held(_menu.Parking);
                        AddRows(shown, parking.Take(ParkingShown), withChips: false);
                        if (parking.Count > ParkingShown)
                        {
                            AddText(shown, "more", MoreParkingText(parking), 11.5);
                        }
                    }

                    break;
                case 1 when _menu.Spots.Count > 0:
                    AddTitle(shown, "Spots", LiveOrder);
                    AddRows(shown, Held(_menu.Spots), withChips: false);
                    break;
                case 2 when _menu.Runways.Count > 0:
                    AddTitle(shown, "Runway holding positions", LiveOrder);
                    AddRows(shown, Held(_menu.Runways), withChips: true);
                    break;
                default:
                    AddText(shown, "status", NoDestinationText, 12.5);
                    break;
            }
        }

        private void RenderMatches(AirTaxiPanelContent shown, string filter)
        {
            List<AirTaxiDestination> stands = Matching([.. _menu.Pads, .. _menu.Parking], filter);
            List<AirTaxiDestination> spots = Matching(_menu.Spots, filter);
            List<AirTaxiDestination> runways =
                (_verb == AirTaxiVerb.AirTaxi) ? [.. Matching(_menu.Runways, filter).Where(runway => !runway.IsHeldAt)] : [];
            AddSection(shown, "Helipads and parking", "@", stands);
            AddSection(shown, "Spots", "$", spots);
            AddSection(shown, "Runway holding positions", "", runways);
            if (shown.Keys.Count == 0)
            {
                AddText(shown, "status", NoMatchText, 12.5);
            }
        }

        private void AddSection(AirTaxiPanelContent shown, string title, string marker, List<AirTaxiDestination> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }

            AddTitle(shown, title, marker);
            AddRows(shown, rows, withChips: false);
        }

        /// <summary>
        /// The destinations of <paramref name="group"/> <paramref name="filter"/> matches, an exact name first, then in held
        /// order.
        /// </summary>
        private List<AirTaxiDestination> Matching(IReadOnlyList<AirTaxiDestination> group, string filter) =>
            [
                .. Held(group)
                    .Select(destination => (Destination: destination, Match: Match(destination, filter)))
                    .Where(candidate => candidate.Match.Matches)
                    .OrderBy(candidate => candidate.Match.Exact ? 0 : 1)
                    .Select(candidate => candidate.Destination),
            ];

        /// <summary>
        /// <paramref name="group"/> nearest first, as the host answers it; while the pointer holds the order, the rows shown
        /// when it entered keep their places and the rest follow nearest first.
        /// </summary>
        private List<AirTaxiDestination> Held(IReadOnlyList<AirTaxiDestination> group)
        {
            List<AirTaxiDestination> nearest = [.. group.OrderBy(destination => destination.DistanceFt)];
            if (_frozenRank is not { } rank)
            {
                return nearest;
            }

            return [.. nearest.OrderBy(destination => rank.TryGetValue(RowKey(destination.Token), out int at) ? at : int.MaxValue)];
        }

        private string RowKey(string token) => $"{CommandWord(_verb)} {token}";

        /// <summary>
        /// What the row keyed <paramref name="key"/> sends: <c>NODEL</c> appended while ticked and, for a Land whose listed
        /// airport is not the aircraft's filed destination, <c>APT</c> of that airport first, as the sim would otherwise
        /// resolve the name at the airport it picks for the aircraft.
        /// </summary>
        private string CommandOf(string key)
        {
            string command = KeepsAfterLanding ? $"{key} NODEL" : key;
            return (AirportToLoad is { } airport) ? $"APT {airport}, {command}" : command;
        }

        /// <summary>The airport a Land loads before landing: the listed one when the aircraft is not bound there, else null.</summary>
        private string? AirportToLoad =>
            (
                (_verb == AirTaxiVerb.Land)
                && (_menu.AirportId.Length > 0)
                && (NavigationDatabase.InstanceOrNull?.AirportIdsMatchResolved(_aircraft?.Destination, _menu.AirportId) != true)
            )
                ? _menu.AirportId
                : null;

        /// <summary>The label beside the box; Land's names the airport its rows are at, <c>Land at OAK</c>.</summary>
        private string BoxLabelOf(AirTaxiMenu menu) =>
            ((_verb == AirTaxiVerb.Land) && (menu.AirportId.Length > 0)) ? $"{_wording.BoxLabel} {menu.AirportId}" : _wording.BoxLabel;

        private void AddTitle(AirTaxiPanelContent shown, string title, string right)
        {
            string text = (right.Length > 0) ? $"{title} · {right}" : title;
            TextBlock line = TextFor($"title:{title}", text, 11.5);
            line.Margin = new Thickness(11, 7, 11, 2);
            shown.Add(line, "t:" + title, text);
        }

        private void AddText(AirTaxiPanelContent shown, string key, string text, double fontSize)
        {
            TextBlock line = TextFor(key, text, fontSize);
            line.Margin = new Thickness(11, 6, 11, 6);
            shown.Add(line, "t:" + key, text);
        }

        /// <summary>The text line kept under <paramref name="key"/>, reused across refills so the panel keeps its children.</summary>
        private TextBlock TextFor(string key, string text, double fontSize)
        {
            if (!_texts.TryGetValue(key, out TextBlock? line))
            {
                line = new TextBlock
                {
                    FontSize = fontSize,
                    Foreground = FlyoutPanelParts.TitleBrush,
                    TextWrapping = TextWrapping.Wrap,
                };
                _texts[key] = line;
            }

            line.Text = text;
            return line;
        }

        private void AddRows(AirTaxiPanelContent shown, IEnumerable<AirTaxiDestination> destinations, bool withChips)
        {
            foreach (AirTaxiDestination destination in destinations)
            {
                string key = RowKey(destination.Token);
                if (destination.IsHeldAt)
                {
                    string text = $"{destination.Name} · {Note(destination)} · {HeldAtText}";
                    TextBlock line = TextFor($"held:{key}", text, 12.5);
                    line.Margin = new Thickness(11, 6, 11, 6);
                    shown.Add(line, HeldRowKeyPrefix + key, text);
                    AddChips(shown, key, destination.Chips);
                    continue;
                }

                var row = new MenuCommandRow
                {
                    Glyph = _glyph,
                    Name = destination.Name,
                    EmphasizeName = false,
                    IsHighlighted = false,
                    Detail = Note(destination),
                    DetailPlacement = MenuDetailPlacement.Stacked,
                    Distance = HoldShortLadder.FeetText(destination.DistanceFt),
                    Command = CommandOf(key),
                };
                Button button = ButtonFor(key);
                button.ContentTemplate ??= MenuCommandRowTemplate.Instance;
                if (!Equals(button.Content, row))
                {
                    button.Content = row;
                }

                MenuCommandText.SetCommand(button, row.Command);
                AutomationProperties.SetName(button, row.ToString());
                shown.Add(button, RowKeyPrefix + key, row.ToString());
                shown.Buttons.Add(button);
                if (withChips && (destination.Chips.Count > 0))
                {
                    AddChips(shown, key, destination.Chips);
                }
            }
        }

        /// <summary>
        /// The chips under a runway row, on a line kept for the row while its chips stay the same, each chip sending its
        /// located form (and <c>NODEL</c> while ticked, which only Land offers).
        /// </summary>
        private void AddChips(AirTaxiPanelContent shown, string rowKey, IReadOnlyList<AirTaxiChip> chips)
        {
            List<string> keys = [.. chips.Select(chip => RowKey(chip.Token))];
            string signature = string.Join(",", keys);
            if (!_chipLines.TryGetValue(rowKey, out (string Chips, WrapPanel Line) kept) || (kept.Chips != signature))
            {
                kept = (signature, ChipLineOf(keys, chips));
                _chipLines[rowKey] = kept;
            }

            List<Button> chipButtons = [.. kept.Line.Children.OfType<Button>()];
            for (int index = 0; index < chipButtons.Count; index++)
            {
                Button chip = chipButtons[index];
                string command = CommandOf(keys[index]);
                MenuCommandText.SetCommand(chip, command);
                AutomationProperties.SetName(chip, command);
                ToolTip.SetTip(chip, command);
                shown.Buttons.Add(chip);
            }

            shown.Add(kept.Line, $"c:{rowKey}:{signature}", $"at {string.Join(" ", chips.Select(chip => chip.Taxiway))}");
        }

        private WrapPanel ChipLineOf(List<string> keys, IReadOnlyList<AirTaxiChip> chips)
        {
            var line = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(41, 0, 11, 5),
                ItemSpacing = 4,
                LineSpacing = 4,
            };
            line.Children.Add(
                new TextBlock
                {
                    Text = "at",
                    FontSize = 11.5,
                    Foreground = FlyoutPanelParts.TitleBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0),
                }
            );
            for (int index = 0; index < chips.Count; index++)
            {
                Button chip = ButtonFor(keys[index]);
                chip.Content = chips[index].Taxiway;
                chip.Padding = new Thickness(7, 1);
                chip.MinWidth = 26;
                chip.BorderBrush = ChipLine;
                chip.BorderThickness = new Thickness(1);
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                chip.HorizontalContentAlignment = HorizontalAlignment.Center;
                (chip.Parent as Panel)?.Children.Remove(chip);
                line.Children.Add(chip);
            }

            return line;
        }

        /// <summary>The button kept for the row or chip keyed <paramref name="key"/>, made the first time it is shown.</summary>
        private Button ButtonFor(string key)
        {
            if (_buttons.TryGetValue(key, out Button? kept))
            {
                return kept;
            }

            var button = new Button
            {
                Padding = new Thickness(11, 5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                if (MenuCommandText.GetCommand(button) is { } command)
                {
                    await ChooseAsync(command);
                }
            };
            _buttons[key] = button;
            return button;
        }

        /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
        private async Task ChooseAsync(string command)
        {
            Task sending = _host.SendAsync(_context.Callsign, command, _context.Initials);
            if (_closeFlyout is { } close)
            {
                close();
            }
            else
            {
                _entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            }

            await sending;
        }

        private void FocusBoxLater() =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_open)
                    {
                        _panel.Box.Focus(NavigationMethod.Directional);
                    }
                },
                DispatcherPriority.Loaded
            );

        private void FocusFirstRowLater() =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_open)
                    {
                        return;
                    }

                    if (_panel.Rows.Count > 0)
                    {
                        FocusRow(_panel.Rows[0]);
                    }
                    else
                    {
                        _panel.Box.Focus(NavigationMethod.Directional);
                    }
                },
                DispatcherPriority.Loaded
            );

        private static void FocusRow(Button row)
        {
            row.Focus(NavigationMethod.Directional);
            row.BringIntoView();
        }

        private bool InTopLine => _panel.Box.IsKeyboardFocusWithin || (_panel.KeepNodel?.IsKeyboardFocusWithin == true);

        private async void OnKeyDown(object? sender, KeyEventArgs e)
        {
            try
            {
                await HandleKeyAsync(e);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "{Verb}: handling {Key} in the destination flyout failed", CommandWord(_verb), e.Key);
            }
        }

        private async Task HandleKeyAsync(KeyEventArgs e)
        {
            bool inTopLine = InTopLine;
            switch (e.Key)
            {
                case Key.Enter when _panel.Box.IsKeyboardFocusWithin:
                    e.Handled = true;
                    if ((Filter.Length > 0) && (_panel.Rows.Count > 0) && (MenuCommandText.GetCommand(_panel.Rows[0]) is { } command))
                    {
                        await ChooseAsync(command);
                    }

                    break;
                case Key.Down when inTopLine:
                    e.Handled = true;
                    if (_panel.Rows.Count > 0)
                    {
                        FocusRow(_panel.Rows[0]);
                    }

                    break;
                case Key.Up or Key.Down or Key.Tab when !inTopLine:
                    e.Handled = true;
                    bool forward = (e.Key == Key.Down) || ((e.Key == Key.Tab) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    Step(forward ? 1 : -1);
                    break;
                case Key.Left or Key.Right when (!inTopLine) && (_panel.Strip.IsVisible):
                    e.Handled = true;
                    _panel.Strip.Move((e.Key == Key.Left) ? -1 : 1);
                    break;
            }
        }

        /// <summary>
        /// Focuses the row after (<paramref name="step"/> 1) or before (-1) the focused one, staying put at the last; before
        /// the first, focus goes back to the box.
        /// </summary>
        private void Step(int step)
        {
            IReadOnlyList<Button> rows = _panel.Rows;
            int at = -1;
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index].IsKeyboardFocusWithin)
                {
                    at = index;
                }
            }

            int next = (at < 0) ? 0 : at + step;
            if (next < 0)
            {
                _panel.Box.Focus(NavigationMethod.Directional);
            }
            else if (next < rows.Count)
            {
                FocusRow(rows[next]);
            }
        }
    }
}

/// <summary>A panel's content as one render builds it: the controls in order, each one's key and text, and the sending buttons.</summary>
internal sealed class AirTaxiPanelContent
{
    public List<Control> Controls { get; } = [];

    public List<string> Keys { get; } = [];

    public List<string> Lines { get; } = [];

    public List<Button> Buttons { get; } = [];

    public void Add(Control control, string key, string line)
    {
        Controls.Add(control);
        Keys.Add(key);
        Lines.Add(line);
    }
}

/// <summary>
/// The Air taxi / Land flyout's one menu row: the verb's glyph, the box's label, the filter box and (Land) the Keep NODEL
/// box on one line over the hint, then the tab strip pinned above a scrolling panel of rows, at most
/// <see cref="MenuCommandRow.RowMaxWidth"/> wide and <see cref="AirTaxiFlyout.RowsMaxHeight"/> tall below the tabs.
/// </summary>
public sealed class AirTaxiPanel : StackPanel
{
    /// <summary>The scroll host's name for assistive technology.</summary>
    public const string DestinationsName = "Destinations";

    private readonly StackPanel _rows = new();
    private List<string> _keys = [];
    private List<Button> _buttons = [];
    private List<string> _lines = [];

    private readonly TextBlock _label;

    internal AirTaxiPanel(QuickCommandGlyph glyph, string boxLabel, string placeholder, bool offersNodel)
    {
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Box = new TextBox
        {
            PlaceholderText = placeholder,
            MinWidth = 150,
            MaxLength = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(Box, boxLabel);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(11, 6, 11, 4) };
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(icon);
        _label = new TextBlock
        {
            Text = boxLabel,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_label, 1);
        top.Children.Add(_label);
        Grid.SetColumn(Box, 2);
        top.Children.Add(Box);
        if (offersNodel)
        {
            KeepNodel = new CheckBox
            {
                Content = AirTaxiFlyout.KeepNodelText,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(KeepNodel, "Keep after landing (NODEL): every row and Enter send LAND … NODEL");
            Grid.SetColumn(KeepNodel, 3);
            top.Children.Add(KeepNodel);
        }

        Hint = new TextBlock
        {
            FontSize = 11.5,
            Opacity = 0.75,
            Margin = new Thickness(11, 0, 11, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        Scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = AirTaxiFlyout.RowsMaxHeight,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(Scroll, DestinationsName);
        Children.Add(top);
        Children.Add(Hint);
        Children.Add(Strip);
        Children.Add(Scroll);
    }

    /// <summary>The text beside the box: <c>Air taxi to</c>, or <c>Land at</c> and the listed airport, <c>Land at OAK</c>.</summary>
    public string BoxLabel => _label.Text ?? "";

    /// <summary>Shows <paramref name="label"/> beside the box and names the box by it.</summary>
    internal void SetBoxLabel(string label)
    {
        _label.Text = label;
        AutomationProperties.SetName(Box, label);
    }

    /// <summary>The panel's top line as text, <c>Air taxi to · Filter pads, parking, spots, runways</c>, the box's label and placeholder.</summary>
    public override string ToString() => $"{BoxLabel} · {Box.PlaceholderText}";

    /// <summary>The filter box.</summary>
    public TextBox Box { get; }

    /// <summary>The Keep NODEL box; null on the Air taxi flyout.</summary>
    public CheckBox? KeepNodel { get; }

    /// <summary>The line under the box saying what Enter sends.</summary>
    public TextBlock Hint { get; }

    /// <summary>The group tabs, hidden while the box filters.</summary>
    public FlyoutTabStrip Strip { get; } = new();

    /// <summary>The scrolling host of the rows.</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The sending rows and chips, in order; titles and the other lines are not among them.</summary>
    public IReadOnlyList<Button> Rows => _buttons;

    /// <summary>
    /// The panel as text, one line per element: a title (<c>Parking · nearest first · live</c>), a row
    /// (<see cref="MenuCommandRow.ToString"/>), a runway's chips (<c>at E G H</c>), the "more" line or the panel's status.
    /// </summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Shows <paramref name="shown"/>: the children are replaced only when its elements differ from the ones shown, so a
    /// refill that only moves distances keeps every control, and the pointer and focus on it, in place. A replacement keeps
    /// keyboard focus on the row that had it.
    /// </summary>
    internal void Show(AirTaxiPanelContent shown, bool resetScroll)
    {
        if (!shown.Keys.SequenceEqual(_keys))
        {
            Button? focused = _buttons.FirstOrDefault(button => button.IsKeyboardFocusWithin);
            _rows.Children.Clear();
            _rows.Children.AddRange(shown.Controls);
            if ((focused is not null) && shown.Buttons.Contains(focused))
            {
                Dispatcher.UIThread.Post(() => focused.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
            }
        }

        if (resetScroll)
        {
            Scroll.Offset = default;
        }

        _keys = shown.Keys;
        _buttons = shown.Buttons;
        _lines = shown.Lines;
    }
}
