using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Reads one field's text: false when the text is not a value of the field's kind.</summary>
/// <typeparam name="TValue">The field's value type.</typeparam>
/// <param name="text">The typed text.</param>
/// <param name="value">The value read.</param>
/// <returns>Whether the text is a value.</returns>
internal delegate bool ScenarioFieldTryParse<TValue>(string text, out TValue value);

/// <summary>How one kind of General-tab field reads its text, shows its value, and what it says when the text is not one.</summary>
/// <typeparam name="TValue">The field's value type.</typeparam>
/// <param name="TryParse">Reads the text.</param>
/// <param name="Format">Shows a value.</param>
/// <param name="ErrorText">The message under the field when the text does not parse.</param>
internal sealed record ScenarioFieldKind<TValue>(ScenarioFieldTryParse<TValue> TryParse, Func<TValue, string> Format, string ErrorText);

/// <summary>
/// The generator rows' field kinds and pickers: numbers and whole numbers in the invariant culture, trimmed (a blank
/// optional number is none); times through <see cref="ScenarioTime"/> (a blank time is 0, a blank optional time is none;
/// a document's negative time is shown as its second count); the engine and weight options.
/// </summary>
internal static class ScenarioFieldText
{
    /// <summary>The undo step every generator field commit records.</summary>
    public const string ChangeGeneratorText = "Change generator";

    /// <summary>The error a number field shows when its text is not a number.</summary>
    public const string NumberErrorText = "Enter a number.";

    /// <summary>The error a whole-number field shows when its text is not a whole number.</summary>
    public const string WholeNumberErrorText = "Enter a whole number.";

    /// <summary>The error a time field shows when its text is not a time.</summary>
    public const string TimeErrorText = "Enter a time like 1:30, 1:02:30 or 130.";

    /// <summary>A number; blank is not one.</summary>
    public static readonly ScenarioFieldKind<double> Number = new(TryNumber, FormatNumber, NumberErrorText);

    /// <summary>A number, or none when blank.</summary>
    public static readonly ScenarioFieldKind<double?> OptionalNumber = new(TryOptionalNumber, FormatOptionalNumber, NumberErrorText);

    /// <summary>A whole number; blank is not one.</summary>
    public static readonly ScenarioFieldKind<int> WholeNumber = new(TryWholeNumber, FormatWholeNumber, WholeNumberErrorText);

    /// <summary>A time in seconds; blank is 0.</summary>
    public static readonly ScenarioFieldKind<int> Time = new(TryTime, FormatTime, TimeErrorText);

    /// <summary>A time in seconds, or none when blank.</summary>
    public static readonly ScenarioFieldKind<int?> OptionalTime = new(TryOptionalTime, FormatOptionalTime, TimeErrorText);

    /// <summary>The engine types a generator picker offers.</summary>
    public static readonly IReadOnlyList<string> EngineTypes = ["Jet", "Turboprop", "Piston"];

    /// <summary>The weight categories a generator picker offers.</summary>
    public static readonly IReadOnlyList<string> WeightCategories = ["Small", "SmallPlus", "Large", "Heavy"];

    private static readonly ScenarioPickOption NoPosition = new("", "None");

    /// <summary>The options, with the document's value appended as itself when it is not empty and not listed.</summary>
    /// <param name="options">The listed options.</param>
    /// <param name="value">The document's value.</param>
    /// <returns>The options a picker shows.</returns>
    public static IReadOnlyList<string> Picks(IReadOnlyList<string> options, string value) =>
        ((value.Length == 0) || options.Contains(value, StringComparer.Ordinal)) ? options : [.. options, value];

    /// <summary>The auto-track position options: none first, then the catalog, then the document's position when unlisted.</summary>
    /// <param name="catalog">The ARTCC's and its neighbours' positions.</param>
    /// <param name="positionId">The document's auto-track position id; empty for none.</param>
    /// <returns>The options a picker shows.</returns>
    public static IReadOnlyList<ScenarioPickOption> AutoTrackOptions(IReadOnlyList<ScenarioPickOption> catalog, string positionId) =>
        [NoPosition, .. ScenarioGeneralViewModel.WithCurrent(catalog, positionId, $"{positionId} (not in the ARTCC config)")];

    // Set while a picker's options are being updated in place, on the UI thread that updates them.
    [ThreadStatic]
    private static int _updatingOptions;

    /// <summary>
    /// Updates a picker's options in place to the rebuilt ones and, when any entry changed, raises the picker's selection
    /// property again. Each picker keeps one collection as its ItemsSource for its view model's lifetime: replacing the
    /// list would make the picker drop its selection binding. While the update runs, any selection the picker writes (a
    /// ComboBox clearing or moving its selection as items change) is not a user pick (see <see cref="IsUserPick"/>).
    /// </summary>
    /// <typeparam name="T">The option type.</typeparam>
    /// <param name="options">The picker's options, updated in place.</param>
    /// <param name="next">The rebuilt options.</param>
    /// <param name="raiseSelection">Raises the selection property's change.</param>
    public static void SetOptions<T>(ObservableCollection<T> options, IReadOnlyList<T> next, Action raiseSelection)
    {
        bool changed;
        _updatingOptions++;
        try
        {
            changed = UpdateInPlace(options, next);
        }
        finally
        {
            _updatingOptions--;
        }

        if (changed)
        {
            raiseSelection();
        }
    }

    /// <summary>
    /// Makes the options hold the next ones, in order, by removing, inserting or replacing only the entries that differ,
    /// so an entry that stays keeps its place in the collection and a picker keeps it selected.
    /// </summary>
    /// <typeparam name="T">The option type.</typeparam>
    /// <param name="options">The options, updated in place.</param>
    /// <param name="next">The entries they should hold.</param>
    /// <returns>Whether any entry changed.</returns>
    public static bool UpdateInPlace<T>(ObservableCollection<T> options, IReadOnlyList<T> next)
    {
        bool changed = false;
        for (int i = 0; i < next.Count; i++)
        {
            changed |= AlignAt(options, next, i);
        }

        while (options.Count > next.Count)
        {
            changed = true;
            options.RemoveAt(options.Count - 1);
        }

        return changed;
    }

    // Makes the options' entry at the index the next one there, the entries before it already aligned: false when it
    // already was.
    private static bool AlignAt<T>(ObservableCollection<T> options, IReadOnlyList<T> next, int i)
    {
        if ((i < options.Count) && EqualityComparer<T>.Default.Equals(options[i], next[i]))
        {
            return false;
        }

        int later = IndexOf(options, next[i], i + 1);
        if (later > i)
        {
            // The entry is further on: the ones before it are gone.
            for (int k = later - 1; k >= i; k--)
            {
                options.RemoveAt(k);
            }
        }
        else if ((i < options.Count) && (IndexOf(next, options[i], i + 1) < 0))
        {
            options[i] = next[i];
        }
        else
        {
            options.Insert(i, next[i]);
        }

        return true;
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item, int from)
    {
        EqualityComparer<T> eq = EqualityComparer<T>.Default;
        for (int i = from; i < list.Count; i++)
        {
            if (eq.Equals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The option whose value is the field's value; null when none is (the picker shows nothing).</summary>
    /// <param name="options">The picker's options.</param>
    /// <param name="value">The field's value.</param>
    /// <returns>The option to show selected.</returns>
    public static ScenarioPickOption? Selected(IReadOnlyList<ScenarioPickOption> options, string value) =>
        options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.Ordinal));

    /// <summary>
    /// Whether a picker's bound selection is the user's pick: not written while options are updated in place (see
    /// <see cref="SetOptions{T}"/>), not null (a picker clearing its selection) and not the value already held (a picker
    /// restoring its selection).
    /// </summary>
    /// <param name="picked">The selection the picker wrote.</param>
    /// <param name="current">The field's value.</param>
    /// <returns>True when the selection should be committed.</returns>
    public static bool IsUserPick([NotNullWhen(true)] string? picked, string current) =>
        (_updatingOptions == 0) && (picked is not null) && !string.Equals(picked, current, StringComparison.Ordinal);

    /// <summary>
    /// Takes a picker's selection: when it is the user's pick (see <see cref="IsUserPick"/>) it is set and committed;
    /// anything else (a picker clearing or restoring its selection, an options update) writes nothing.
    /// </summary>
    /// <param name="picked">The selection the picker wrote.</param>
    /// <param name="current">The field's value.</param>
    /// <param name="set">Sets the field.</param>
    /// <param name="commit">Commits the field.</param>
    public static void Pick(string? picked, string current, Action<string> set, Action commit)
    {
        if (IsUserPick(picked, current))
        {
            set(picked);
            commit();
        }
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryOptionalNumber(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        bool parsed = TryNumber(text, out double number);
        value = parsed ? number : null;
        return parsed;
    }

    private static bool TryWholeNumber(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryTime(string text, out int value)
    {
        int? seconds = ScenarioTime.Parse(text);
        value = seconds ?? 0;
        return seconds is not null;
    }

    private static bool TryOptionalTime(string text, out int? value)
    {
        value = string.IsNullOrWhiteSpace(text) ? null : ScenarioTime.Parse(text);
        return string.IsNullOrWhiteSpace(text) || (value is not null);
    }

    private static string FormatNumber(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatOptionalNumber(double? value) => (value is { } number) ? FormatNumber(number) : "";

    private static string FormatWholeNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A time as its field shows it: <see cref="ScenarioTime.Format"/>, or the second count when negative.</summary>
    /// <param name="seconds">The time in seconds.</param>
    /// <returns>The text shown.</returns>
    public static string FormatTime(int seconds) => (seconds >= 0) ? ScenarioTime.Format(seconds) : FormatWholeNumber(seconds);

    private static string FormatOptionalTime(int? seconds) => (seconds is { } value) ? FormatTime(value) : "";
}
