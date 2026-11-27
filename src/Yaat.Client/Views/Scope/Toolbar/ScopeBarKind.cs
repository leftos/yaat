using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>Which display control bar a Scope shows.</summary>
public enum ScopeBarKind
{
    /// <summary>Today's bar, shown for a Mixed profile.</summary>
    Classic,

    /// <summary>The STARS display control bar.</summary>
    Stars,

    /// <summary>The ERAM toolbar.</summary>
    Eram,
}

/// <summary>Picks the bar a Scope shows from the room's Scope profile kind.</summary>
public static class ScopeBarPicker
{
    /// <summary>STARS profile → Stars, ERAM profile → Eram, Mixed and EuroScope → Classic (today's bar).</summary>
    public static ScopeBarKind KindFor(ScopeProfileKind kind) =>
        kind switch
        {
            ScopeProfileKind.Stars => ScopeBarKind.Stars,
            ScopeProfileKind.Eram => ScopeBarKind.Eram,
            ScopeProfileKind.Mixed => ScopeBarKind.Classic,
            ScopeProfileKind.EuroScope => ScopeBarKind.Classic,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope profile kind"),
        };
}
