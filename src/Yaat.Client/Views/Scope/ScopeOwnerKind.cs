using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The display kind of a track's owner, read from its sector code as the server formats it: <c>C</c> and a sector is an ERAM
/// sector, a leading digit (the STARS subset) a STARS position. An unowned track follows the zoom band's
/// <see cref="Profiles.ScopeZoomBand.Unowned"/>, and an owner with no sector code draws the ERAM block. The Mixed template
/// routes each track through it.
/// </summary>
public static class ScopeOwnerKind
{
    /// <summary>
    /// The template of <paramref name="ac"/>'s owner's kind: <see cref="ScopeTemplates.StarsId"/> for a STARS position,
    /// <paramref name="unownedTemplateId"/> when nobody owns the track, else <see cref="ScopeTemplates.EramId"/>.
    /// </summary>
    public static string TemplateIdFor(AircraftModel ac, string unownedTemplateId)
    {
        if ((ac.OwnerSectorCode is { Length: > 0 } code) && char.IsAsciiDigit(code[0]))
        {
            return ScopeTemplates.StarsId;
        }

        return string.IsNullOrEmpty(ac.Owner) ? unownedTemplateId : ScopeTemplates.EramId;
    }

    /// <summary>
    /// The template <paramref name="ac"/> draws when the scope picked <paramref name="templateId"/>: its owner's kind under
    /// <see cref="ScopeTemplates.MixedId"/> (an unowned track's <paramref name="unownedTemplateId"/>), else the pick itself.
    /// </summary>
    public static string Resolve(string templateId, AircraftModel ac, string unownedTemplateId) =>
        (templateId == ScopeTemplates.MixedId) ? TemplateIdFor(ac, unownedTemplateId) : templateId;
}
