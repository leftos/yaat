using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// The Scope's bar under the ERAM profile: CRC's Master Toolbar over the <see cref="ScopeViewModel"/> in its DataContext,
/// its menus opening and closing on their buttons, and each live button acting through the classic bar's own path
/// (<see cref="ScopeViewModel.Press"/>, <see cref="ScopeViewModel.StepSpinner"/>), so it does exactly what its classic
/// counterpart does. Toggles and labels follow the view model on every change.
/// </summary>
public sealed class EramBar : UserControl
{
    private static readonly ILogger Log = AppLog.CreateLogger("EramBar");

    private ScopeViewModel? _viewModel;
    private string _builtGeomap = "";
    private string _builtPrefsets = "";
    private bool _menuCheckPending;

    /// <summary>The bar, with no Scope until its DataContext is one.</summary>
    public EramBar()
    {
        Toolbar = new EramMasterToolbar { MenuSource = MenuFor };
        Toolbar.Menu += OnMenu;
        Toolbar.Toggle += OnPress;
        Toolbar.Command += OnPress;
        Toolbar.IncDec += OnIncDec;
        Toolbar.Wheel += OnWheel;
        Content = Toolbar;
    }

    /// <summary>The Master Toolbar the bar shows.</summary>
    public EramMasterToolbar Toolbar { get; }

    /// <summary>
    /// Whether the bar acts on a press of <paramref name="type"/>: RANGE's wheel, a menu with buttons to open, an inc/dec
    /// button with a spinner to step, or a button with a classic button to press. True exactly for the buttons
    /// <see cref="EramButtonTypeExtensions.IsBuilt"/> calls built.
    /// </summary>
    /// <param name="type">The button asked about.</param>
    internal static bool HasAction(EramButtonType type) =>
        (type == EramButtonType.RangeView)
        || (type is EramButtonType.GeomapMenu or EramButtonType.PrefsetMenu)
        || (EramMenus.Fixed(type) is not null)
        || (SpinnerOf(type) is not null)
        || (PressIdOf(type) is not null);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is { } old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.MapToggles.CollectionChanged -= OnMapTogglesChanged;
        }

        _viewModel = DataContext as ScopeViewModel;
        if (_viewModel is { } viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.MapToggles.CollectionChanged += OnMapTogglesChanged;
        }

        Toolbar.RefreshMenu();
        Refresh();
    }

    /// <summary>The spinner an inc/dec button steps, or null for any other button.</summary>
    private static ScopeButtonId? SpinnerOf(EramButtonType type) =>
        type switch
        {
            EramButtonType.VectorLength => ScopeButtonId.Vector,
            EramButtonType.PairedTargetBrightness or EramButtonType.UnpairedTargetBrightness => ScopeButtonId.BriteTargets,
            EramButtonType.PairedTargetHistoryBrightness or EramButtonType.UnpairedTargetHistoryBrightness => ScopeButtonId.BriteHist,
            EramButtonType.FdbBrightness => ScopeButtonId.BriteFdb,
            EramButtonType.FdbLine4Brightness => ScopeButtonId.BriteLine4,
            EramButtonType.DwellBrightness => ScopeButtonId.BriteDwell,
            EramButtonType.MapBcg1 => ScopeButtonId.BriteMapA,
            EramButtonType.MapBcg2 => ScopeButtonId.BriteMapB,
            EramButtonType.FdbFontSize => ScopeButtonId.DbChar,
            EramButtonType.HistoryFilter => ScopeButtonId.Hist,
            EramButtonType.YaatToolSize => ScopeButtonId.ToolSize,
            _ => null,
        };

    /// <summary>The classic button a toggle or command presses, or null for any other button.</summary>
    private static ScopeButtonId? PressIdOf(EramButtonType type) =>
        type switch
        {
            EramButtonType.HideToolbar => ScopeButtonId.BarHide,
            EramButtonType.ShowToolbar => ScopeButtonId.BarShow,
            EramButtonType.CrrView => ScopeButtonId.ViewCrr,
            EramButtonType.CaView => ScopeButtonId.ViewCa,
            EramButtonType.WeatherMenu => ScopeButtonId.DisplayWx,
            EramButtonType.DestinationDisplay or EramButtonType.AircraftDataDisplay => ScopeButtonId.Db4thLine,
            >= EramButtonType.Filter1 and <= EramButtonType.Filter40 => ScopeButtonId.MapToggle,
            EramButtonType.PrefsetSet => ScopeButtonId.PrefSet,
            EramButtonType.PrefsetSave => ScopeButtonId.PrefSave,
            EramButtonType.PrefsetSaveAs => ScopeButtonId.PrefSaveAs,
            EramButtonType.PrefsetRename => ScopeButtonId.PrefRename,
            EramButtonType.PrefsetDelete => ScopeButtonId.PrefDelete,
            _ => YaatPressIdOf(type),
        };

    /// <summary>The classic button a YAAT menu toggle or command presses, or null for any other button.</summary>
    private static ScopeButtonId? YaatPressIdOf(EramButtonType type) =>
        type switch
        {
            EramButtonType.YaatOthers => ScopeButtonId.Others,
            EramButtonType.YaatAuto => ScopeButtonId.Auto,
            EramButtonType.YaatTdm => ScopeButtonId.Tdm,
            EramButtonType.YaatMva => ScopeButtonId.Mva,
            EramButtonType.YaatProfileStars => ScopeButtonId.ProfileStars,
            EramButtonType.YaatProfileEram => ScopeButtonId.ProfileEram,
            EramButtonType.YaatProfileMixed => ScopeButtonId.ProfileMixed,
            EramButtonType.YaatTemplateStars => ScopeButtonId.TemplateStars,
            EramButtonType.YaatTemplateEram => ScopeButtonId.TemplateEram,
            EramButtonType.YaatTemplateMixed => ScopeButtonId.TemplateMixed,
            EramButtonType.YaatTemplateEuroScope => ScopeButtonId.TemplateEuroScope,
            EramButtonType.YaatToolJRing => ScopeButtonId.ToolJRing,
            EramButtonType.YaatToolCone => ScopeButtonId.ToolCone,
            EramButtonType.YaatToolDri5 => ScopeButtonId.ToolDri5,
            EramButtonType.YaatToolDri3 => ScopeButtonId.ToolDri3,
            EramButtonType.YaatDisplayFix => ScopeButtonId.DisplayFix,
            EramButtonType.YaatDisplayLock => ScopeButtonId.DisplayLock,
            EramButtonType.YaatDisplayDcnf => ScopeButtonId.DisplayDcnf,
            EramButtonType.YaatDisplayOffCntr => ScopeButtonId.DisplayOffCntr,
            EramButtonType.YaatDbDwell => ScopeButtonId.DbDwell,
            EramButtonType.YaatDbAtpa => ScopeButtonId.DbAtpa,
            EramButtonType.YaatDbAtpaAlertCones => ScopeButtonId.DbAtpaAlertCones,
            EramButtonType.YaatDbAtpaMonitorCones => ScopeButtonId.DbAtpaMonitorCones,
            EramButtonType.YaatDbCaRing => ScopeButtonId.DbCaRing,
            EramButtonType.YaatViewSsa => ScopeButtonId.ViewSsa,
            EramButtonType.YaatViewRpoQueue => ScopeButtonId.ViewRpoQueue,
            EramButtonType.YaatLensOn => ScopeButtonId.LensOn,
            EramButtonType.YaatLensPath => ScopeButtonId.LensPath,
            EramButtonType.YaatLensQueue => ScopeButtonId.LensQueue,
            EramButtonType.YaatLensCheckIn => ScopeButtonId.LensCheckIn,
            _ => null,
        };

    /// <summary>
    /// The classic tray a YAAT submenu stands for in the Scope's open menu: TOOLS, whose opening arms the ring tool, and
    /// PILOT LENS, the shared tray state the classic bar and IsActive of PILOT LENS read; null for any other submenu. An ERAM
    /// menu button has no lit state, so on this bar the lens shows on the submenu's ON toggle, not on PILOT LENS itself.
    /// </summary>
    private static ScopeButtonId? TrayOf(EramButtonType? submenu) =>
        submenu switch
        {
            EramButtonType.YaatToolsMenu => ScopeButtonId.Tools,
            EramButtonType.YaatLensMenu => ScopeButtonId.Lens,
            _ => null,
        };

    /// <summary>The classic button whose value a button shows on its second line: a spinner's, PROFILE's or OTHERS'.</summary>
    private static ScopeButtonId? ValueIdOf(EramButtonType type) =>
        type switch
        {
            EramButtonType.YaatProfileMenu => ScopeButtonId.Profile,
            EramButtonType.YaatOthers => ScopeButtonId.Others,
            _ => SpinnerOf(type),
        };

    /// <summary>The PREF tray's pref-set buttons, in file order.</summary>
    private static IEnumerable<ScopeButton> PrefSets(ScopeViewModel viewModel) =>
        viewModel.TrayChildren(Classic(ScopeButtonId.Pref)).Where(button => button.Id == ScopeButtonId.PrefSet);

    /// <summary>The classic bar's button with <paramref name="id"/>; every id the ERAM bar names has one.</summary>
    /// <exception cref="InvalidOperationException">The classic bar has no button with <paramref name="id"/>.</exception>
    private static ScopeButton Classic(ScopeButtonId id) =>
        ScopeToolbarButtons.Find(id) ?? throw new InvalidOperationException($"The classic bar has no {id} button for the ERAM bar to press");

    /// <summary>
    /// The classic button a press of <paramref name="type"/> keyed <paramref name="key"/> stands for: a GeoMap filter's map
    /// toggle by its STARS id, a pref set's tray button by its name, or the classic button of <see cref="PressIdOf"/>.
    /// </summary>
    private static ScopeButton? PressTarget(EramButtonType type, string? key, ScopeViewModel viewModel) =>
        PressIdOf(type) switch
        {
            null => null,
            ScopeButtonId.MapToggle => (key is { } starsId) && (viewModel.FindMapToggle(starsId) is { } toggle)
                ? ScopeToolbarButtons.MapToggle(toggle.StarsId, toggle.ShortName)
                : null,
            ScopeButtonId.PrefSet => PrefSets(viewModel).FirstOrDefault(button => button.Value == key),
            { } id => Classic(id),
        };

    /// <summary>The value a button shows on its second line, or null for a button that keeps its own.</summary>
    private static string? ValueOf(EramButtonType type, ScopeViewModel viewModel)
    {
        if (type == EramButtonType.RangeView)
        {
            return ScopeToolbarButtons.RangeLabel(viewModel.RangeNm);
        }

        // MAP A and MAP B keep their labels: the letter is their second line.
        if ((type is EramButtonType.MapBcg1 or EramButtonType.MapBcg2) || (ValueIdOf(type) is not { } id))
        {
            return null;
        }

        return viewModel.SpinnerValue(Classic(id));
    }

    /// <summary>The buttons <paramref name="menu"/> holds: GeoMap's and PREFSET's from the Scope, the rest from their tables.</summary>
    /// <remarks>Notes the map list or the set names it builds from, so a rebuild happens only when they change.</remarks>
    private IReadOnlyList<EramMenuButton>? MenuFor(EramButtonType menu)
    {
        switch (menu)
        {
            case EramButtonType.GeomapMenu:
                _builtGeomap = GeomapList();
                return EramMenus.Geomap([.. (_viewModel?.MapToggles ?? []).Select(t => (t.StarsId, t.ShortName))]);
            case EramButtonType.PrefsetMenu:
                _builtPrefsets = PrefsetList();
                return EramMenus.Prefset([.. PrefSetNames()]);
            default:
                return EramMenus.Fixed(menu);
        }
    }

    /// <summary>The pref sets' names, in file order.</summary>
    /// <remarks>A pref-set button with no name is a bug in the PREF tray: it is left out, with a warning, never drawn blank.</remarks>
    private IEnumerable<string> PrefSetNames()
    {
        foreach (ScopeButton button in (_viewModel is { } vm) ? PrefSets(vm) : [])
        {
            if (button.Value is { } name)
            {
                yield return name;
            }
            else
            {
                Log.LogWarning("ERAM bar: a PREF tray pref-set button has no set name; PREFSET leaves it out");
            }
        }
    }

    /// <summary>The Scope's map list as the GeoMap menu is built from it: its STARS ids in order.</summary>
    private string GeomapList() => string.Join(',', (_viewModel?.MapToggles ?? []).Select(t => t.StarsId.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The Scope's pref-set names as the PREFSET menu is built from them, in file order.</summary>
    private string PrefsetList() => string.Join('\n', PrefSetNames());

    /// <summary>
    /// Checks the open GeoMap or PREFSET menu against its list once the current changes are over: a map list can change
    /// one toggle at a time, so the check waits for the dispatcher and runs once.
    /// </summary>
    private void ScheduleMenuCheck()
    {
        if (_menuCheckPending)
        {
            return;
        }

        _menuCheckPending = true;
        Dispatcher.UIThread.Post(CheckMenu);
    }

    /// <summary>Rebuilds the open GeoMap or PREFSET menu when its list has changed; its buttons' lit state is Refresh's.</summary>
    private void CheckMenu()
    {
        _menuCheckPending = false;
        bool stale = Toolbar.OpenMenu switch
        {
            EramButtonType.GeomapMenu => GeomapList() != _builtGeomap,
            EramButtonType.PrefsetMenu => PrefsetList() != _builtPrefsets,
            _ => false,
        };
        if (stale)
        {
            Toolbar.RefreshMenu();
            Refresh();
        }
    }

    /// <summary>A Master Toolbar menu button opens its menu or closes it when it is the open one; any other opens its submenu.</summary>
    private void OnMenu(object? sender, EramButtonEventArgs e)
    {
        if (EramMasterLayout.Placements.Any(placement => placement.Type == e.Type))
        {
            Toolbar.OpenMenu = (Toolbar.OpenMenu == e.Type) ? null : e.Type;
        }
        else
        {
            Toolbar.OpenSubmenu = (Toolbar.OpenSubmenu == e.Type) ? null : e.Type;
        }

        MirrorTray();
        Refresh();
    }

    /// <summary>
    /// Sets the Scope's open menu as the classic bar's tray would be: TOOLS or PILOT LENS while the YAAT submenu standing
    /// for it is open, and none once it closes, so the ring tool arms as on the classic bar and the shared tray state
    /// stays the classic bar's; the lens itself shows on the PILOT LENS submenu's ON toggle, as an ERAM menu button has no
    /// lit state.
    /// </summary>
    private void MirrorTray()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        if (TrayOf(Toolbar.OpenSubmenu) is { } tray)
        {
            viewModel.OpenMenu = tray;
        }
        else if (viewModel.OpenMenu is ScopeButtonId.Tools or ScopeButtonId.Lens)
        {
            viewModel.OpenMenu = null;
        }
    }

    /// <summary>Closes the YAAT TOOLS or PILOT LENS submenu when the Scope closes the tray it stands for (a command press does).</summary>
    private void FollowTray(ScopeViewModel viewModel)
    {
        if ((TrayOf(Toolbar.OpenSubmenu) is { } tray) && (viewModel.OpenMenu != tray))
        {
            Toolbar.OpenSubmenu = null;
        }
    }

    private void OnPress(object? sender, EramButtonEventArgs e)
    {
        if ((_viewModel is { } viewModel) && (PressTarget(e.Type, e.Key, viewModel) is { } target))
        {
            viewModel.Press(target);
        }
    }

    private void OnIncDec(object? sender, EramButtonEventArgs e)
    {
        if ((_viewModel is { } viewModel) && (SpinnerOf(e.Type) is { } id))
        {
            viewModel.StepSpinner(id, e.Delta);
        }
    }

    /// <summary>The wheel over RANGE steps the range as the classic RANGE spinner's wheel does.</summary>
    private void OnWheel(object? sender, EramButtonEventArgs e)
    {
        if ((_viewModel is { } viewModel) && (e.Type == EramButtonType.RangeView))
        {
            viewModel.StepSpinner(ScopeButtonId.Range, e.Delta);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScopeViewModel.MapToggles) or nameof(ScopeViewModel.PrefSetNames))
        {
            ScheduleMenuCheck();
        }

        if ((e.PropertyName == nameof(ScopeViewModel.OpenMenu)) && (_viewModel is { } viewModel))
        {
            FollowTray(viewModel);
        }

        // A bar hiding to its SHOW tile closes its open menu, and with it any submenu.
        if ((e.PropertyName == nameof(ScopeViewModel.IsBarHidden)) && (_viewModel is { IsBarHidden: true }))
        {
            Toolbar.OpenMenu = null;
        }

        Refresh();
    }

    private void OnMapTogglesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleMenuCheck();
        Refresh();
    }

    /// <summary>Sets every shown button's state from the Scope: a toggle's or command's IsOn, a value's second line.</summary>
    private void Refresh()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        foreach (EramToolbarButton button in Toolbar.AllButtons)
        {
            if (PressTarget(button.Type, button.Key, viewModel) is { } target)
            {
                button.IsOn = viewModel.IsActive(target);
            }

            if (ValueOf(button.Type, viewModel) is { } value)
            {
                button.SetLines(button.Line1, value);
            }
        }
    }
}
