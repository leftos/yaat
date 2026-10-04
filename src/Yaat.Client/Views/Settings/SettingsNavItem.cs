using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Settings;

/// <summary>
/// One row of the Settings sidebar: a section the user can open, or (with no <paramref name="Id"/>) the
/// header of a group of sections, which is never selectable.
/// </summary>
/// <param name="Id">The section this row opens, or null for a group header.</param>
/// <param name="Group">The group the row belongs to.</param>
/// <param name="Title">The text shown in the sidebar: the section title, or the group name for a header.</param>
public sealed record SettingsNavItem(SettingsSectionId? Id, string Group, string Title)
{
    public bool IsHeader => Id is null;
}
