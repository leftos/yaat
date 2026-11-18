using System.Globalization;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>One LA/CA/MCI list row: its text, and whether it draws dimmed (a suppressed STARS pair, steady).</summary>
/// <param name="Text">The row's text, in CRC's format.</param>
/// <param name="Dimmed">The pair is a suppressed STARS pair: the row draws at <see cref="DimmedOpacity"/>.</param>
public sealed record ConflictListRow(string Text, bool Dimmed)
{
    /// <summary>The opacity a dimmed row draws at: half brightness, as the suppressed pair's CA line.</summary>
    public const double DimmedOpacity = 0.5;

    /// <summary>The opacity the row draws at.</summary>
    public double Opacity => Dimmed ? DimmedOpacity : 1.0;
}

/// <summary>The LA/CA/MCI list's rows, in CRC's format.</summary>
public static class ConflictListRows
{
    /// <summary>
    /// One row per pair, in the order given: the two names sorted ordinal and joined by <c>*</c>, padded (never cut) to 15
    /// characters, then three spaces and the label. A STARS pair is <c>CA</c>, or <c>MCI</c> when a member
    /// <paramref name="find"/> resolves is an untracked target with no flight plan; an ERAM pair is <c>ERAM</c>, or
    /// <c>ERAM MCI</c> when it has an intruder. An MCI row names the intruder by its beacon code (docs/crc/stars.md, LA/CA/MCI
    /// List: "its reported beacon code will be shown in place of the callsign"); a suppressed STARS pair's row is dimmed.
    /// </summary>
    public static IReadOnlyList<ConflictListRow> Rows(IEnumerable<ConflictAlertDto> pairs, Func<string, AircraftModel?> find) =>
        [.. pairs.Select(p => Row(p, find))];

    private static ConflictListRow Row(ConflictAlertDto pair, Func<string, AircraftModel?> find)
    {
        AircraftModel? a = find(pair.CallsignA);
        AircraftModel? b = find(pair.CallsignB);
        string? intruder = IntruderOf(pair, a, b);
        string nameA = NameOf(pair.CallsignA, a, intruder);
        string nameB = NameOf(pair.CallsignB, b, intruder);
        bool aFirst = string.CompareOrdinal(nameA, nameB) <= 0;
        string first = aFirst ? nameA : nameB;
        string second = aFirst ? nameB : nameA;
        string text = string.Format(CultureInfo.InvariantCulture, "{0,-15}   {1}", $"{first}*{second}", LabelOf(pair.Kind, intruder is not null));
        return new ConflictListRow(text, (pair.Kind == ConflictAlertKind.Stars) && pair.Suppressed);
    }

    /// <summary>The pair's Mode C intruder: an ERAM pair's own, else a STARS member that is untracked with no flight plan.</summary>
    private static string? IntruderOf(ConflictAlertDto pair, AircraftModel? a, AircraftModel? b)
    {
        if (pair.Kind == ConflictAlertKind.Eram)
        {
            return pair.IntruderCallsign;
        }

        if ((a is not null) && DatablockText.IsModeCIntruder(a))
        {
            return pair.CallsignA;
        }

        return ((b is not null) && DatablockText.IsModeCIntruder(b)) ? pair.CallsignB : null;
    }

    private static string NameOf(string callsign, AircraftModel? ac, string? intruder) =>
        (string.Equals(callsign, intruder, StringComparison.Ordinal) && (ac is { BeaconCode: > 0 }))
            ? ac.BeaconCode.ToString("0000", CultureInfo.InvariantCulture)
            : callsign;

    private static string LabelOf(ConflictAlertKind kind, bool hasIntruder) =>
        (kind, hasIntruder) switch
        {
            (ConflictAlertKind.Eram, true) => "ERAM MCI",
            (ConflictAlertKind.Eram, false) => "ERAM",
            (_, true) => "MCI",
            _ => "CA",
        };
}
