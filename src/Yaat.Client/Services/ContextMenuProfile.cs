namespace Yaat.Client.Services;

public record ContextMenuProfile(
    IReadOnlyList<MenuGroup> PrimaryGroups,
    IReadOnlyList<MenuGroup> SecondaryGroups,
    IReadOnlySet<MenuGroup> HiddenGroups
);
