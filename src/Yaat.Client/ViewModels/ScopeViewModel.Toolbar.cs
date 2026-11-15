using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope toolbar's state: which menu's tray is open, and the button actions. Every action runs a <c>.scope</c>
/// command through <see cref="MainViewModel.RunScopeCommand"/>, the handler the command line uses.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>The menu whose tray shows under the bar, or null when no tray is open. One tray at a time.</summary>
    [ObservableProperty]
    private ScopeButtonId? _openMenu;

    /// <summary>Runs the <c>.scope</c> command <paramref name="text"/>, as the toolbar's buttons do.</summary>
    public void RunCommand(string text) => Main.RunScopeCommand(text);

    /// <summary>
    /// Acts on a click of <paramref name="button"/>: a menu opens its tray, or closes it when it is the open one; a
    /// command button closes the tray and runs its command; a spinner does nothing on a click.
    /// </summary>
    public void Press(ScopeButton button)
    {
        switch (button.Kind)
        {
            case ScopeButtonKind.Menu:
                OpenMenu = OpenMenu == button.Id ? null : button.Id;
                break;
            case ScopeButtonKind.Command:
                OpenMenu = null;
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

    /// <summary>Whether <paramref name="button"/> draws in the active state: the open menu, or the template pick in use.</summary>
    public bool IsActive(ScopeButton button) => (OpenMenu == button.Id) || ((button.Value is { } value) && (value == TemplateId));

    /// <summary>The value a spinner shows after its label, or null for any other button.</summary>
    public string? SpinnerValue(ScopeButton button) =>
        button.Id == ScopeButtonId.Range ? Math.Round(RangeNm).ToString(CultureInfo.InvariantCulture) : null;

    private void RunButtonCommand(ScopeButton button)
    {
        if (button.Value is { } templateId)
        {
            RunCommand(ScopeToolbarButtons.TemplateCommand(templateId));
        }
    }
}
