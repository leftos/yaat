using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Preset taxi route flyout over the host's answer (<see cref="IMenuHost.GetPresetTaxiChoices"/>), one builder for the
/// All Commands submenu and the quick-command strip's flyout: one flat two-line row per preset, nearest first, led by the
/// preset glyph, its name over its taxiways in the monospace font, then its distance from the aircraft and the
/// <c>TAXI</c> command it sends. A row stays within <see cref="MenuCommandRow.InlineRowMaxWidth"/>: its name, taxiway line
/// and command each trim with an ellipsis, and while any is trimmed the row carries its whole text in its tooltip. A row
/// pointed at or focused previews its route (<see cref="IMenuHost.SetRoutePreview"/>), sent again with each live refill
/// below and cleared when the flyout closes. With no preset the entry is not built. While the quick-command strip's
/// flyout is open the list refills once a second (<see cref="FlyoutLiveTick"/>): with the pointer outside it the rows are
/// re-sorted nearest first as the host answers; with the pointer inside it the order freezes, each row's distance updating
/// in place, a preset the host no longer returns staying as a dimmed <see cref="GroundTrafficFlyout.GoneState"/> row that
/// sends nothing and the keyboard skips, and a new preset joining the bottom; when the pointer leaves the list catches up
/// at once. The All Commands submenu refills as it opens.
/// </summary>
public static class TaxiRouteFlyout
{
    /// <summary>The muted line the flyout shows once every preset it listed has gone while open.</summary>
    public const string NoPresetsText = "No preset taxi routes from here.";

    /// <summary>
    /// The Preset taxi route entry labelled <paramref name="label"/>: the presets that resolve from the aircraft, or null
    /// when none does.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the presets come from and the rows send through.</param>
    public static MenuItem? BuildPreset(string label, MenuContext context, IMenuHost host)
    {
        IReadOnlyList<TaxiRouteRow> presets = host.GetPresetTaxiChoices(context.Callsign);
        if (presets.Count == 0)
        {
            return null;
        }

        var entry = new MenuItem { Header = label };
        var list = new PresetList(entry, context, host);
        list.Attach(presets);
        QuickCommandStrip.FollowInFlyout(entry, list);
        return entry;
    }

    /// <summary>
    /// The row a preset shows: the preset glyph, the name semibold over the taxiways, the distance (<c>~2,300 ft</c>, or
    /// <see cref="GroundTrafficFlyout.GoneState"/> once the host no longer returns it) and the command.
    /// </summary>
    private static MenuCommandRow PresetRow(TaxiRouteRow preset, bool isGone) =>
        new()
        {
            Badge = null,
            Glyph = QuickCommandGlyphs.For(MenuIds.GroundTaxiPreset),
            Name = preset.Name,
            EmphasizeName = true,
            IsHighlighted = false,
            Detail = preset.Via,
            DetailPlacement = MenuDetailPlacement.Route,
            Distance = isGone ? GroundTrafficFlyout.GoneState : HoldShortLadder.FeetText(preset.DistanceFt),
            Command = preset.Command,
        };

    /// <summary>What keeps a preset's row across a refill: its name and its command.</summary>
    private static (string Name, string Command) KeyOf(TaxiRouteRow preset) => (preset.Name, preset.Command);

    /// <summary>The presets nearest first, presets at the same distance in the host's order.</summary>
    private static List<PresetEntry> NearestFirst(IReadOnlyList<TaxiRouteRow> presets) =>
        [.. presets.OrderBy(preset => preset.DistanceFt).Select(preset => new PresetEntry(preset, false))];

    /// <summary>A preset as the flyout lists it: the preset as last answered, and whether the host has dropped it since.</summary>
    private sealed record PresetEntry(TaxiRouteRow Preset, bool IsGone);

    /// <summary>
    /// A list held in place while the pointer is inside its flyout: the presets shown when the pointer entered, each keeping
    /// its place while its distance follows the host; a preset the host no longer returns stays as a gone entry, and a new
    /// one joins the bottom.
    /// </summary>
    private sealed class FrozenPresets(IEnumerable<PresetEntry> shown)
    {
        private readonly List<PresetEntry> _entries = [.. shown];

        /// <summary>The held entries, in order.</summary>
        public List<PresetEntry> Entries => [.. _entries];

        /// <summary>Brings every held entry up to <paramref name="presets"/> and adds the new ones at the bottom.</summary>
        public void Update(IReadOnlyList<TaxiRouteRow> presets)
        {
            var listed = new Dictionary<(string Name, string Command), TaxiRouteRow>();
            foreach (TaxiRouteRow preset in presets)
            {
                listed.TryAdd(KeyOf(preset), preset);
            }

            var held = new HashSet<(string Name, string Command)>();
            for (int i = 0; i < _entries.Count; i++)
            {
                (string Name, string Command) key = KeyOf(_entries[i].Preset);
                held.Add(key);
                _entries[i] = listed.TryGetValue(key, out TaxiRouteRow? now) ? new PresetEntry(now, false) : _entries[i] with { IsGone = true };
            }

            _entries.AddRange(NearestFirst(presets).Where(entry => held.Add(KeyOf(entry.Preset))));
        }
    }

    /// <summary>
    /// The flyout's rows on whichever surface shows them: filled when built, refilled when the All Commands submenu opens
    /// and, while the strip's flyout is open, once a second (<see cref="FlyoutLiveTick"/>), held in place while the pointer
    /// is inside the flyout. A preset keeps its row, by its name and command, across a refill; a row gone sends nothing,
    /// even on a click already on its way.
    /// </summary>
    private sealed class PresetList(MenuItem entry, MenuContext context, IMenuHost host) : IStripFlyoutContent
    {
        private readonly MenuItem _empty = new() { Header = NoPresetsText, IsEnabled = false };
        private readonly HashSet<MenuItem> _live = [];
        private Dictionary<(string Name, string Command), MenuItem> _rows = [];
        private List<PresetEntry> _shown = [];
        private ItemCollection? _flyoutItems;
        private Action? _closeFlyout;
        private FlyoutLiveTick? _tick;
        private FrozenPresets? _frozen;
        private (string Name, string Command)? _previewed;

        /// <summary>Fills the submenu with <paramref name="presets"/> and refills it whenever the submenu opens.</summary>
        public void Attach(IReadOnlyList<TaxiRouteRow> presets)
        {
            Show(NearestFirst(presets));
            entry.PropertyChanged += (_, e) =>
            {
                if ((e.Property == MenuItem.IsSubMenuOpenProperty) && entry.IsSubMenuOpen)
                {
                    Refill();
                }
            };
        }

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            _flyoutItems = items;
            _closeFlyout = close;
            (_tick ??= new FlyoutLiveTick(host.Clock, Refill)).Start();
            Refill();
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if ((_tick?.IsRunning != true) || (inside == (_frozen is not null)))
            {
                return;
            }

            _frozen = inside ? new FrozenPresets(_shown) : null;
            if (!inside)
            {
                Refill();
            }
        }

        /// <inheritdoc />
        public void Closed()
        {
            _tick?.Stop();
            _frozen = null;
            _closeFlyout = null;
            Refill();
            _previewed = null;
            host.SetRoutePreview(null);
        }

        private void Refill()
        {
            IReadOnlyList<TaxiRouteRow> presets = host.GetPresetTaxiChoices(context.Callsign);
            if (_frozen is null)
            {
                Show(NearestFirst(presets));
                return;
            }

            _frozen.Update(presets);
            Show(_frozen.Entries);
        }

        private void Show(List<PresetEntry> entries)
        {
            MenuItem? focused = _rows.Values.FirstOrDefault(row => row.IsKeyboardFocusWithin);
            _shown = entries;
            List<Control> wanted = [.. Rows(entries, focused)];
            if (wanted.Count == 0)
            {
                wanted.Add(_empty);
            }

            FlyoutItemSync.Sync(_flyoutItems ?? entry.Items, wanted, FlyoutItemSync.FocusedIn(wanted));
            HandFocusOnIfGone(focused, wanted);
            // The preview follows the live refill alone, as Hold short of's and Give way's do.
            if (_tick?.IsRunning == true)
            {
                RefreshPreview();
            }
        }

        /// <summary>
        /// Sends the last previewed preset's route again as the host now answers it, a taxiing aircraft's path having moved;
        /// a preset gone or no longer listed previews nothing.
        /// </summary>
        private void RefreshPreview()
        {
            if (_previewed is not { } key)
            {
                return;
            }

            if (Live(key) is { } shown)
            {
                host.SetRoutePreview(shown.Preset.Preview);
                return;
            }

            _previewed = null;
            host.SetRoutePreview(null);
        }

        /// <summary>The shown, not gone, entry of the preset keyed <paramref name="key"/>; null for none.</summary>
        private PresetEntry? Live((string Name, string Command) key) =>
            _shown.FirstOrDefault(candidate => (KeyOf(candidate.Preset) == key) && !candidate.IsGone);

        /// <summary>
        /// The rows <paramref name="entries"/> call for, reusing the row of every preset shown before;
        /// <paramref name="focused"/> stays in reach for now even when gone.
        /// </summary>
        private List<MenuItem> Rows(List<PresetEntry> entries, MenuItem? focused)
        {
            var rows = new Dictionary<(string Name, string Command), MenuItem>();
            List<MenuItem> ordered = [];
            _live.Clear();
            foreach (PresetEntry shown in entries)
            {
                (string Name, string Command) key = KeyOf(shown.Preset);
                MenuItem item = (!rows.ContainsKey(key) && _rows.TryGetValue(key, out MenuItem? kept)) ? kept : NewRow(key);
                rows.TryAdd(key, item);
                ordered.Add(item);
                Present(item, shown, ReferenceEquals(item, focused));
                if (!shown.IsGone)
                {
                    _live.Add(item);
                }
            }

            _rows = rows;
            return ordered;
        }

        /// <summary>
        /// Hands keyboard focus from <paramref name="focused"/> once its preset can no longer hold it, gone or no longer
        /// listed, to the nearest live row below it, else above it, else the first live row, then takes it out of reach.
        /// </summary>
        private void HandFocusOnIfGone(MenuItem? focused, List<Control> wanted)
        {
            if ((focused is null) || _live.Contains(focused))
            {
                return;
            }

            var listed = new HashSet<MenuItem>(_rows.Values);
            List<MenuItem> rows = [.. wanted.OfType<MenuItem>().Where(listed.Contains)];
            int at = rows.IndexOf(focused);
            MenuItem? next =
                (at >= 0)
                    ? rows.Skip(at + 1).FirstOrDefault(_live.Contains) ?? rows.Take(at).LastOrDefault(_live.Contains)
                    : rows.FirstOrDefault(_live.Contains);
            next?.Focus(NavigationMethod.Directional);
            focused.IsEnabled = false;
        }

        private MenuItem NewRow((string Name, string Command) key)
        {
            var item = new MenuItem { HeaderTemplate = MenuCommandRowTemplate.Instance };
            item.Click += async (_, e) =>
            {
                if (!_live.Contains(item) || (item.Header is not MenuCommandRow row))
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(row.Command);
            };
            item.PointerEntered += (_, _) => Preview(key);
            // A menu row marks its focus events handled before handlers added the plain way would see them.
            item.AddHandler(InputElement.GotFocusEvent, (_, _) => Preview(key), handledEventsToo: true);
            return item;
        }

        /// <summary>Previews the route of the preset keyed <paramref name="key"/> while it is listed and not gone.</summary>
        private void Preview((string Name, string Command) key)
        {
            if (Live(key) is { } shown)
            {
                _previewed = key;
                host.SetRoutePreview(shown.Preset.Preview);
            }
        }

        /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
        private async Task ChooseAsync(string command)
        {
            Task sending = host.SendAsync(context.Callsign, command, context.Initials);
            if (_closeFlyout is { } close)
            {
                close();
            }
            else
            {
                entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            }

            await sending;
        }

        /// <summary>
        /// Shows <paramref name="shown"/> in <paramref name="item"/>, dimmed and out of reach once gone unless it
        /// <paramref name="holdsFocus"/>.
        /// </summary>
        private static void Present(MenuItem item, PresetEntry shown, bool holdsFocus)
        {
            MenuCommandRow row = PresetRow(shown.Preset, shown.IsGone);
            if (!Equals(item.Header, row))
            {
                item.Header = row;
                MenuCommandText.SetCommand(item, row.Command);
                AutomationProperties.SetName(item, row.ToString());
            }

            item.IsEnabled = (!shown.IsGone) || holdsFocus;
            item.Opacity = shown.IsGone ? MenuGlyphRowTemplate.DimmedOpacity : 1;
        }
    }
}
