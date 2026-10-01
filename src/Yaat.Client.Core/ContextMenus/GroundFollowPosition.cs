namespace Yaat.Client.ContextMenus;

/// <summary>
/// Where in the ground view's menu the Follow… and Give way to… submenus are placed, each place offering them for its
/// own phases: see <see cref="SharedMenuGroups.AddGroundFollowAndGiveWay"/>.
/// </summary>
public enum GroundFollowPosition
{
    /// <summary>After the pushback items, for an aircraft at parking: Follow… only.</summary>
    Parking,

    /// <summary>After Hold short of…, for a taxiing aircraft.</summary>
    Taxi,

    /// <summary>After the runway clearances, for an aircraft in one of the stationary holds.</summary>
    Hold,
}
