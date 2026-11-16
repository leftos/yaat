using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope toolbar's state: which menu's tray is open, and the button actions. Every action runs a dot command through
/// <see cref="RunCommand"/>, which hands it to the handler the command line uses.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The status line's message when a pref-set press needs an active set and there is none.</summary>
    internal const string NoActivePrefSetMessage = "No pref set is active: use .scope pref saveas <name>";

    // One wheel accumulator per spinner id, shared by the bar's spinner and its tear-off and kept across tray rebuilds,
    // so a part-step on one spinner never carries to another.
    private readonly Dictionary<ScopeButtonId, ScrollStepAccumulator> _spinnerScroll = [];

    /// <summary>The menu whose tray shows under the bar, or null when no tray is open. One tray at a time.</summary>
    [ObservableProperty]
    private ScopeButtonId? _openMenu;

    /// <summary>The wheel accumulator of the spinner with <paramref name="id"/>, made on first use.</summary>
    internal ScrollStepAccumulator SpinnerScroll(ScopeButtonId id)
    {
        if (!_spinnerScroll.TryGetValue(id, out ScrollStepAccumulator? scroll))
        {
            scroll = new ScrollStepAccumulator();
            _spinnerScroll[id] = scroll;
        }

        return scroll;
    }

    /// <summary>The Scope's map toggle for the map with STARS id <paramref name="starsId"/>, or null.</summary>
    internal ScopeMapToggle? FindMapToggle(string starsId) =>
        MapToggles.FirstOrDefault(t => t.StarsId.ToString(CultureInfo.InvariantCulture) == starsId);

    /// <summary>
    /// Runs the dot command <paramref name="text"/>, as the toolbar's buttons do: a <c>.view</c> command through
    /// <see cref="MainViewModel.RunViewCommand"/>, any other through <see cref="MainViewModel.RunScopeCommand"/>.
    /// </summary>
    public void RunCommand(string text)
    {
        if (text.StartsWith(".view ", StringComparison.OrdinalIgnoreCase))
        {
            Main.RunViewCommand(text);
        }
        else
        {
            Main.RunScopeCommand(text);
        }
    }

    /// <summary>
    /// Acts on a click of <paramref name="button"/>: a menu opens its tray, or closes it when it is the open one; a
    /// command button closes the tray and runs its command; a toggle runs its command with the opposite of its state and
    /// keeps the tray open; a spinner does nothing on a click. A PREF set or SAVE button runs its command and closes the
    /// tray; SAVE AS and RENAME type their command into the command box and close it; DELETE runs the active set's
    /// delete. RENAME and DELETE with no active set answer in the status line instead.
    /// </summary>
    public void Press(ScopeButton button)
    {
        if (button.Id == ScopeButtonId.DeleteTearoff)
        {
            PressDeleteTearoff();
            return;
        }

        if (TryPressPref(button))
        {
            return;
        }

        if (button.Id == ScopeButtonId.Others)
        {
            OpenMenu = null;
            RunCommand(ScopeToolbarButtons.OthersCommand(FlippedOthersLevel()));
            return;
        }

        switch (button.Kind)
        {
            case ScopeButtonKind.Menu:
                OpenMenu = OpenMenu == button.Id ? null : button.Id;
                break;
            case ScopeButtonKind.Command:
                OpenMenu = null;
                RunButtonCommand(button);
                break;
            case ScopeButtonKind.Toggle:
                RunButtonCommand(button);
                break;
        }
    }

    /// <summary>
    /// Changes the range by <paramref name="delta"/> whole NM from its rounded value, through <c>.scope range</c>; a step
    /// that would leave 1 to 256 NM changes nothing, as the Radar's range does.
    /// </summary>
    public void StepRange(int delta)
    {
        int next = (int)Math.Round(RangeNm) + delta;
        if (next is >= 1 and <= 256)
        {
            RunCommand(ScopeToolbarButtons.RangeCommand(next));
        }
    }

    /// <summary>
    /// Steps the spinner with <paramref name="id"/> by <paramref name="steps"/> wheel steps, positive for wheel up: RANGE
    /// zooms in (<see cref="StepRange"/> by <c>-steps</c>); CHAR grows by one point a step (<see cref="StepCharSize"/>); a
    /// BRITE spinner moves its group by <c>steps</c> times its step, through <c>.scope brite</c>: a step past the group's
    /// limit stops at the limit, so a value typed off the step grid can be wheeled back, and a step at the limit runs no
    /// command.
    /// </summary>
    public void StepSpinner(ScopeButtonId id, int steps)
    {
        if (id == ScopeButtonId.Range)
        {
            StepRange(-steps);
            return;
        }

        if (id == ScopeButtonId.DbChar)
        {
            StepCharSize(steps);
            return;
        }

        if (ScopeToolbarButtons.BriteGroupOf(id) is not { } group)
        {
            return;
        }

        int current = group.Get(Brightness);
        int next = Math.Clamp(current + (steps * group.Step), group.Min, group.Max);
        if (next != current)
        {
            RunCommand(ScopeToolbarButtons.BriteCommand(group.Name, next));
        }
    }

    /// <summary>
    /// Whether <paramref name="button"/> draws in the active state: a menu while its tray is open, a toggle while what it
    /// turns on is on (<see cref="IsToggleOn"/>), a profile pick while its profile is the shown one, a command button while
    /// the template it picks is in use, a pref-set
    /// button while its set is the active one, DEL TEAROFF while its mode is on, OTHERS while others' level is pinned and
    /// AUTO while any band value is.
    /// </summary>
    public bool IsActive(ScopeButton button) =>
        button.Id switch
        {
            ScopeButtonId.DeleteTearoff => IsDeletingTearoffs,
            ScopeButtonId.Others => Pins.OthersLevel is not null,
            ScopeButtonId.Auto => Pins.Any,
            ScopeButtonId.PrefSet => (button.Value is { } name) && (name == ActivePrefSet),
            _ when ScopeToolbarButtons.IsProfilePick(button.Id) => button.Value == ScopeProfilePicker.NameOf(ProfileKind),
            _ => IsActiveByKind(button),
        };

    /// <summary>
    /// The buttons the tray of <paramref name="menu"/> shows: for MAPS, CLR ALL then one toggle per <see cref="MapToggles"/>
    /// item in its order; for PREF, one button per pref set in file order, then SAVE, SAVE AS, RENAME and DELETE; for any
    /// other menu, its own children.
    /// </summary>
    public IReadOnlyList<ScopeButton> TrayChildren(ScopeButton menu) =>
        menu.Id switch
        {
            ScopeButtonId.Maps => [ScopeToolbarButtons.MapClear, .. MapToggles.Select(t => ScopeToolbarButtons.MapToggle(t.StarsId, t.ShortName))],
            ScopeButtonId.Pref => PrefTray(),
            _ => menu.Children,
        };

    /// <summary>
    /// The value a spinner shows after its label (the range, the CHAR size, or a BRITE group's value), the active pref
    /// set's name after PREF's label, the shown profile's name after PROFILE's, others' level in effect after OTHERS', or
    /// null for any other button.
    /// </summary>
    public string? SpinnerValue(ScopeButton button)
    {
        if (button.Id == ScopeButtonId.Pref)
        {
            return ActivePrefSet;
        }

        if (button.Id == ScopeButtonId.Others)
        {
            return EffectiveBand.OthersLevel.ToUpperInvariant();
        }

        if (button.Id == ScopeButtonId.Profile)
        {
            return ScopeProfilePicker.NameOf(ProfileKind).ToUpperInvariant();
        }

        if (button.Id == ScopeButtonId.Range)
        {
            return Math.Round(RangeNm).ToString(CultureInfo.InvariantCulture);
        }

        if (button.Id == ScopeButtonId.DbChar)
        {
            return DbSettings.CharSize.ToString(CultureInfo.InvariantCulture);
        }

        return ScopeToolbarButtons.BriteGroupOf(button.Id) is { } group ? group.Get(Brightness).ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Whether a toggle's setting is on: a map toggle while the map with its STARS id is on, a DB toggle while its setting
    /// is on, a view toggle while the view it names is open.
    /// </summary>
    private bool IsToggleOn(ScopeButton button) =>
        button.Id switch
        {
            ScopeButtonId.MapToggle => (button.Value is { } starsId) && (FindMapToggle(starsId) is { IsOn: true }),
            ScopeButtonId.Db4thLine => DbSettings.Line4,
            ScopeButtonId.DbDwell => DbSettings.HoverDwell,
            ScopeButtonId.DbAtpa => DbSettings.Atpa,
            _ => (button.Value is { } view) && IsViewOpen(view),
        };

    /// <summary>
    /// Steps the CHAR size by <paramref name="steps"/> points through <c>.scope db char</c>; a step past a limit stops at it,
    /// and a step at the limit runs no command.
    /// </summary>
    private void StepCharSize(int steps)
    {
        int current = DbSettings.CharSize;
        int next = Math.Clamp(current + steps, ScopeDbSettings.MinCharSize, ScopeDbSettings.MaxCharSize);
        if (next != current)
        {
            RunCommand(ScopeToolbarButtons.DbCharCommand(next));
        }
    }

    private void RunButtonCommand(ScopeButton button)
    {
        if (button.Command is not { } command)
        {
            return;
        }

        RunCommand(button.Kind == ScopeButtonKind.Toggle ? $"{command} {(IsActive(button) ? "off" : "on")}" : command);
    }

    /// <summary>Whether <paramref name="button"/> draws active by its kind, for a button no id rule covers.</summary>
    private bool IsActiveByKind(ScopeButton button) =>
        button.Kind switch
        {
            ScopeButtonKind.Menu => OpenMenu == button.Id,
            ScopeButtonKind.Toggle => IsToggleOn(button),
            ScopeButtonKind.Command => (button.Value is { } value) && (value == TemplateId),
            _ => false,
        };

    /// <summary>The PREF tray: one button per set in file order, then SAVE, SAVE AS, RENAME and DELETE.</summary>
    private IReadOnlyList<ScopeButton> PrefTray() =>
        [
            .. PrefSetNames.Select(ScopeToolbarButtons.PrefSetButton),
            ScopeToolbarButtons.PrefSave,
            ScopeToolbarButtons.PrefSaveAs,
            ScopeToolbarButtons.PrefRename,
            ScopeToolbarButtons.PrefDelete,
        ];

    /// <summary>
    /// Runs the PREF tray's own press for <paramref name="button"/>: a set or SAVE closes the tray and runs its command,
    /// SAVE AS, RENAME and DELETE act on the active set. False for any other button.
    /// </summary>
    private bool TryPressPref(ScopeButton button)
    {
        if (button.Id is ScopeButtonId.PrefSet or ScopeButtonId.PrefSave)
        {
            OpenMenu = null;
            RunButtonCommand(button);
            return true;
        }

        if (button.Id is ScopeButtonId.PrefSaveAs or ScopeButtonId.PrefRename or ScopeButtonId.PrefDelete)
        {
            PressPrefAction(button.Id);
            return true;
        }

        return false;
    }

    /// <summary>The PREF tray's SAVE AS, RENAME and DELETE presses.</summary>
    private void PressPrefAction(ScopeButtonId id)
    {
        switch (id)
        {
            case ScopeButtonId.PrefSaveAs:
                OpenMenu = null;
                TypePrefCommand(ScopeToolbarButtons.PrefSaveAsPrefix);
                break;
            case ScopeButtonId.PrefRename:
                PressPrefRename();
                break;
            default:
                PressPrefDelete();
                break;
        }
    }

    /// <summary>RENAME: types the active set's rename into the command box, or says what to do when no set is active.</summary>
    private void PressPrefRename()
    {
        if (ActivePrefSet is not { } active)
        {
            Main.StatusText = NoActivePrefSetMessage;
            return;
        }

        OpenMenu = null;
        TypePrefCommand(ScopeToolbarButtons.PrefRenamePrefix(active));
    }

    /// <summary>DELETE: deletes the active set through its verb, or says what to do when no set is active.</summary>
    private void PressPrefDelete()
    {
        if (ActivePrefSet is not { } active)
        {
            Main.StatusText = NoActivePrefSetMessage;
            return;
        }

        RunCommand(ScopeToolbarButtons.PrefDeleteCommand(active));
    }

    /// <summary>Puts <paramref name="text"/> in the command box, caret after it, and asks for the box's focus.</summary>
    private void TypePrefCommand(string text)
    {
        Main.CommandText = text;
        Main.CommandCaretIndex = text.Length;
        Main.FocusCommandInput();
    }
}
