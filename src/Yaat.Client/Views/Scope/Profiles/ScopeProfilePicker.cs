using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope.Profiles;

/// <summary>
/// The kind test that picks a Scope profile from the viewer's position, the text names of the kinds, and the order a
/// profile's name list gives a list of buttons or views.
/// </summary>
public static class ScopeProfilePicker
{
    /// <summary>
    /// The profile kind for <paramref name="position"/>: ERAM for a position with an ERAM sector, STARS for a TRACON or tower
    /// position, else Mixed (no position, a center position without a sector, or any other type).
    /// </summary>
    public static ScopeProfileKind KindFor(ActivePositionDto? position)
    {
        if (position is null)
        {
            return ScopeProfileKind.Mixed;
        }

        if (!string.IsNullOrEmpty(position.EramSectorId))
        {
            return ScopeProfileKind.Eram;
        }

        bool stars =
            (string.Equals(position.PositionType, "Tracon", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(position.PositionType, "Atct", StringComparison.OrdinalIgnoreCase));
        return stars ? ScopeProfileKind.Stars : ScopeProfileKind.Mixed;
    }

    /// <summary>The text name of <paramref name="kind"/>: <c>stars</c>, <c>eram</c> or <c>mixed</c>.</summary>
    public static string NameOf(ScopeProfileKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Parses a kind's text name (<see cref="NameOf"/>), case-insensitive.</summary>
    /// <returns>False when <paramref name="text"/> names no kind.</returns>
    public static bool TryParseKind(string? text, out ScopeProfileKind kind)
    {
        foreach (ScopeProfileKind candidate in Enum.GetValues<ScopeProfileKind>())
        {
            if (string.Equals(NameOf(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = ScopeProfileKind.Mixed;
        return false;
    }

    /// <summary>
    /// <paramref name="items"/> in the order <paramref name="names"/> gives: the items it names, in its order, then every
    /// item it does not name, in their own order. A name matches case-insensitively; a name no item has is added to
    /// <paramref name="unknown"/>, and a name given twice counts once. No item is ever left out.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, IReadOnlyList<string> names, Func<T, string> nameOf, List<string> unknown)
        where T : class
    {
        List<T> ordered = [];
        foreach (string name in names)
        {
            T? match = items.FirstOrDefault(item => string.Equals(nameOf(item), name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                unknown.Add(name);
            }
            else if (!ordered.Contains(match))
            {
                ordered.Add(match);
            }
        }

        ordered.AddRange(items.Where(item => !ordered.Contains(item)));
        return ordered;
    }
}
