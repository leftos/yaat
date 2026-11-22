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
