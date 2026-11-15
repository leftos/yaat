using System.Globalization;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>The LA/CA/MCI list's rows, in CRC's format. The client holds conflict-alert pairs only, so every row is a CA row.</summary>
public static class ConflictListRows
{
    /// <summary>
    /// One row per pair, in the order given: the two callsigns sorted ordinal and joined by <c>*</c>, padded (never cut) to
    /// 15 characters, then three spaces and <c>CA</c>.
    /// </summary>
    public static IReadOnlyList<string> Build(IEnumerable<ConflictAlertDto> pairs) => [.. pairs.Select(Row)];

    private static string Row(ConflictAlertDto pair)
    {
        bool aFirst = string.CompareOrdinal(pair.CallsignA, pair.CallsignB) <= 0;
        string first = aFirst ? pair.CallsignA : pair.CallsignB;
        string second = aFirst ? pair.CallsignB : pair.CallsignA;
        return string.Format(CultureInfo.InvariantCulture, "{0,-15}   {1}", $"{first}*{second}", "CA");
    }
}
