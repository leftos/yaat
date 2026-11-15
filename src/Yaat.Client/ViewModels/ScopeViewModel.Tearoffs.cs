using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's button tear-offs: copies of toolbar buttons placed on the scope, one per button key, each anchored to a
/// corner. Every change goes through <c>.scope tearoff</c>; DEL TEAROFF marks tear-offs and then deletes the marked ones.
/// </summary>
public sealed partial class ScopeViewModel
{
    private readonly HashSet<ScopeButtonKey> _markedTearoffs = [];

    /// <summary>Whether DEL TEAROFF's mode is on: a click on a tear-off marks or unmarks it instead of pressing it.</summary>
    [ObservableProperty]
    private bool _isDeletingTearoffs;

    /// <summary>The committed tear-offs, in the order they were first torn off.</summary>
    public ObservableCollection<ScopeTearoff> Tearoffs { get; } = [];

    /// <summary>Whether the button with <paramref name="key"/> is torn off.</summary>
    public bool IsTornOff(ScopeButtonKey key) => Tearoffs.Any(t => t.Key == key);

    /// <summary>Whether the tear-off of <paramref name="key"/> is marked for DEL TEAROFF.</summary>
    public bool IsTearoffMarked(ScopeButtonKey key) => _markedTearoffs.Contains(key);

    /// <summary>Marks the tear-off of <paramref name="key"/> for DEL TEAROFF, or unmarks it when it is marked.</summary>
    public void ToggleTearoffMark(ScopeButtonKey key)
    {
        if (!_markedTearoffs.Remove(key))
        {
            _markedTearoffs.Add(key);
        }
    }

    /// <summary>
    /// Places the tear-off of <paramref name="key"/> at <paramref name="place"/>: a torn-off key moves in its own slot, any
    /// other is appended.
    /// </summary>
    public void PlaceTearoff(ScopeButtonKey key, ScopePlacement place)
    {
        var tearoff = new ScopeTearoff(key, place);
        for (int i = 0; i < Tearoffs.Count; i++)
        {
            if (Tearoffs[i].Key == key)
            {
                Tearoffs[i] = tearoff;
                return;
            }
        }

        Tearoffs.Add(tearoff);
    }

    /// <summary>Deletes the tear-offs of <paramref name="keys"/>; a key with no tear-off is skipped.</summary>
    public void DeleteTearoffs(IEnumerable<ScopeButtonKey> keys)
    {
        foreach (ScopeButtonKey key in keys)
        {
            _markedTearoffs.Remove(key);
            if (Tearoffs.FirstOrDefault(t => t.Key == key) is { } tearoff)
            {
                Tearoffs.Remove(tearoff);
            }
        }
    }

    /// <summary>
    /// The button <paramref name="key"/> stands for: a map toggle from <see cref="MapToggles"/> (one whose map the Scope
    /// lacks is labelled with its STARS id alone), else the toolbar's button with the key's id.
    /// </summary>
    public ScopeButton ButtonFor(ScopeButtonKey key)
    {
        if (key.Id == ScopeButtonId.MapToggle)
        {
            string id = key.Value ?? "";
            ScopeMapToggle? toggle = FindMapToggle(id);
            return toggle is not null
                ? ScopeToolbarButtons.MapToggle(toggle.StarsId, toggle.ShortName)
                : new ScopeButton(ScopeButtonId.MapToggle, id, ScopeButtonKind.Toggle, [], id, $".scope map {id}");
        }

        return key.Id == ScopeButtonId.MapClear
            ? ScopeToolbarButtons.MapClear
            : ScopeToolbarButtons.Find(key.Id) ?? throw new ArgumentException($"No scope button '{key.Id}'.", nameof(key));
    }

    /// <summary>
    /// Acts on a DEL TEAROFF press: the first press turns its mode on and closes the tray; the next deletes the marked
    /// tear-offs, if any, through <c>.scope tearoff delete</c>, and turns the mode off.
    /// </summary>
    private void PressDeleteTearoff()
    {
        if (!IsDeletingTearoffs)
        {
            IsDeletingTearoffs = true;
            OpenMenu = null;
            return;
        }

        ScopeButtonKey[] marked = [.. Tearoffs.Select(t => t.Key).Where(_markedTearoffs.Contains)];
        if (marked.Length > 0)
        {
            RunCommand(ScopeToolbarButtons.TearoffDeleteCommand(marked));
        }

        IsDeletingTearoffs = false;
    }

    // Leaving the mode, whichever way, clears the marks.
    partial void OnIsDeletingTearoffsChanged(bool value)
    {
        if (!value)
        {
            _markedTearoffs.Clear();
        }
    }
}
