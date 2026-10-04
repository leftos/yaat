namespace Yaat.Client.ContextMenus;

public record ContextMenuProfile(
    IReadOnlyList<MenuGroup> PrimaryGroups,
    IReadOnlyList<MenuGroup> SecondaryGroups,
    IReadOnlySet<MenuGroup> HiddenGroups
);
