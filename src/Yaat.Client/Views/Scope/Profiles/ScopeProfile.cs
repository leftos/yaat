using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.Views.Scope.Profiles;

/// <summary>The three Scope profiles: how the scope behaves for a STARS, an ERAM or a mixed position.</summary>
public enum ScopeProfileKind
{
    /// <summary>The STARS profile, for a TRACON or tower position.</summary>
    Stars,

    /// <summary>The ERAM profile, for an ERAM sector.</summary>
    Eram,

    /// <summary>The Mixed profile, for any other position and for no position.</summary>
    Mixed,
}

/// <summary>One zoom band of a profile: the values the scope takes while its range is at most <see cref="MaxNm"/>.</summary>
/// <param name="MaxNm">The band's outer edge in NM, inclusive; null for the last band, which has no edge.</param>
/// <param name="OthersLevel">The block level of tracks the viewer does not own: <c>full</c> or <c>partial</c>.</param>
/// <param name="VectorMin">The vector length in minutes; 0 draws no vector.</param>
/// <param name="History">How many history dots each track draws.</param>
/// <param name="Rings">The separation ring a new ring takes: <c>jring</c> or <c>dri</c>.</param>
public sealed record ScopeZoomBand(double? MaxNm, string OthersLevel, double VectorMin, int History, string Rings);

/// <summary>
/// One Scope profile as its file stores it: its name and kind, its zoom bands, the datablock template it opens with, and the
/// order of the toolbar's buttons and of the views. Every field holds text names, so a rename in code leaves the file
/// readable.
/// </summary>
/// <param name="Name">The profile's name, such as <c>mixed</c>.</param>
/// <param name="Kind">The profile's kind: <c>stars</c>, <c>eram</c> or <c>mixed</c>.</param>
/// <param name="Bands">The zoom bands, inner first.</param>
/// <param name="Template">The template id the profile opens with (<see cref="ScopeTemplates.IsSelectable"/>).</param>
/// <param name="Toolbar">The bar's button names, in the order they come first on the bar.</param>
/// <param name="Views">The view names, in the order they come first in the dock.</param>
public sealed record ScopeProfile(
    string Name,
    string Kind,
    IReadOnlyList<ScopeZoomBand> Bands,
    string Template,
    IReadOnlyList<string> Toolbar,
    IReadOnlyList<string> Views
)
{
    /// <summary>The <c>othersLevel</c> word for the full block.</summary>
    public const string OthersFull = "full";

    /// <summary>The <c>othersLevel</c> word for the template's lower block.</summary>
    public const string OthersPartial = "partial";

    /// <summary>The <c>rings</c> word for a J-ring.</summary>
    public const string RingsJRing = "jring";

    /// <summary>The <c>rings</c> word for a DRI halo.</summary>
    public const string RingsDri = "dri";

    /// <summary>The serializer options profile files are written and read with: the pref-set files' options.</summary>
    public static JsonSerializerOptions JsonOptions => ScopePrefSet.JsonOptions;

    /// <summary>Fields this build does not know, kept so a resave does not lose them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
