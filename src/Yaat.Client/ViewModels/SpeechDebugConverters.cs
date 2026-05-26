using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Renders an <see cref="System.Collections.IEnumerable"/> of strings as a single space-separated
/// line for the Speech Debug context cards. Empty collections render as "(none)" so the textbox
/// doesn't look broken when a session was captured before any callsigns / fixes were loaded.
/// </summary>
public sealed class JoinSpaceConverter : IValueConverter
{
    public static readonly JoinSpaceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().Where(o => o is not null).Select(o => o!.ToString()!).ToList();
            return items.Count == 0 ? "(none)" : string.Join(' ', items);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Renders an <c>IReadOnlyDictionary&lt;string, IReadOnlyList&lt;string&gt;&gt;</c> as a per-airport
/// runway block (one airport per line): <c>KOAK: 28R 28L 10R 10L 30 12 33 15</c>. Mirrors the
/// format the LLM fallback sees in its user prompt so reviewers can quickly correlate the trace
/// with what the LLM was given.
/// </summary>
public sealed class RunwaysByAirportConverter : IValueConverter
{
    public static readonly RunwaysByAirportConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Collections.IDictionary dict || dict.Count == 0)
        {
            return "(none)";
        }
        var sb = new StringBuilder();
        foreach (System.Collections.DictionaryEntry kvp in dict)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.Append(kvp.Key).Append(": ");
            if (kvp.Value is System.Collections.IEnumerable runways)
            {
                sb.Append(string.Join(' ', runways.Cast<object?>().Where(o => o is not null).Select(o => o!.ToString())));
            }
        }
        return sb.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
