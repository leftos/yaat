using System.Collections.Frozen;

namespace Yaat.Client.Services;

/// <summary>
/// Maps aircraft phase names to context menu profiles that control
/// which menu groups are shown, their ordering, and which are hidden.
/// </summary>
public static class ContextMenuProfileService
{
    private static readonly MenuGroup[] AlwaysVisibleGroups =
    [
        MenuGroup.Tracking,
        MenuGroup.DataBlock,
        MenuGroup.Squawk,
        MenuGroup.Coordination,
        MenuGroup.Assignment,
        MenuGroup.DatablockToggle,
        MenuGroup.Delete,
    ];

    private static readonly FrozenSet<MenuGroup> AlwaysVisibleSet = AlwaysVisibleGroups.ToFrozenSet();

    private static readonly MenuGroup[] AllPhaseGroups =
    [
        MenuGroup.Heading,
        MenuGroup.Altitude,
        MenuGroup.Speed,
        MenuGroup.Navigation,
        MenuGroup.DrawRoute,
        MenuGroup.Hold,
        MenuGroup.Approach,
        MenuGroup.Procedures,
        MenuGroup.Tower,
        MenuGroup.Pattern,
    ];

    private static readonly MenuGroup[] FlightCommandGroups =
    [
        MenuGroup.Heading,
        MenuGroup.Altitude,
        MenuGroup.Speed,
        MenuGroup.Navigation,
        MenuGroup.DrawRoute,
        MenuGroup.Hold,
        MenuGroup.Approach,
        MenuGroup.Procedures,
    ];

    private static readonly FrozenSet<MenuGroup> FlightAndPatternHidden = FrozenSet.ToFrozenSet([
        MenuGroup.Heading,
        MenuGroup.Altitude,
        MenuGroup.Speed,
        MenuGroup.Navigation,
        MenuGroup.DrawRoute,
        MenuGroup.Hold,
        MenuGroup.Approach,
        MenuGroup.Procedures,
        MenuGroup.Pattern,
    ]);

    private static readonly FrozenSet<MenuGroup> NoHidden = FrozenSet<MenuGroup>.Empty;

    public static ContextMenuProfile GetProfile(string? currentPhase, bool isOnGround)
    {
        if (string.IsNullOrEmpty(currentPhase))
        {
            return BuildProfile(FlightCommandGroups, [], NoHidden);
        }

        // Ground phases — hide all flight + pattern commands
        if (IsGroundPhase(currentPhase))
        {
            return BuildProfile([MenuGroup.Tower], [], FlightAndPatternHidden);
        }

        // Takeoff: depends on whether still on ground
        if (currentPhase == "Takeoff")
        {
            return isOnGround
                ? BuildProfile([MenuGroup.Tower], [], FlightAndPatternHidden)
                : BuildProfile([MenuGroup.Heading, MenuGroup.Altitude, MenuGroup.Speed, MenuGroup.Navigation], [], NoHidden);
        }

        if (currentPhase == "InitialClimb")
        {
            return BuildProfile([MenuGroup.Heading, MenuGroup.Altitude, MenuGroup.Speed, MenuGroup.Navigation], [], NoHidden);
        }

        // Pattern phases — Tower + Pattern primary, flight commands visible (ClearsPhase)
        if (IsPatternPhase(currentPhase))
        {
            return BuildProfile([MenuGroup.Tower, MenuGroup.Pattern], [], NoHidden);
        }

        if (currentPhase == "FinalApproach")
        {
            return BuildProfile([MenuGroup.Tower], [], NoHidden);
        }

        if (currentPhase is "ApproachNav" or "InterceptCourse")
        {
            return BuildProfile([MenuGroup.Speed, MenuGroup.Altitude, MenuGroup.Tower], [], NoHidden);
        }

        // Holding/navigation phases
        if (IsHoldingPhase(currentPhase))
        {
            return BuildProfile([MenuGroup.Heading, MenuGroup.Altitude, MenuGroup.Speed, MenuGroup.Navigation], [], NoHidden);
        }

        // Landing phases — hide flight commands
        if (currentPhase is "Landing" or "Landing-H")
        {
            return BuildProfile([MenuGroup.Tower], [], FlightAndPatternHidden);
        }

        if (currentPhase == "GoAround")
        {
            return BuildProfile([MenuGroup.Tower, MenuGroup.Heading, MenuGroup.Altitude, MenuGroup.Speed], [], NoHidden);
        }

        // Touch-and-go variants — on the runway, hide flight commands
        if (currentPhase is "TouchAndGo" or "StopAndGo" or "LowApproach")
        {
            return BuildProfile([MenuGroup.Tower], [], FlightAndPatternHidden);
        }

        // Turn phases
        if (IsTurnPhase(currentPhase))
        {
            return BuildProfile([MenuGroup.Heading, MenuGroup.Altitude, MenuGroup.Speed], [], NoHidden);
        }

        if (currentPhase == "Takeoff-H")
        {
            return BuildProfile([MenuGroup.Tower], [], FlightAndPatternHidden);
        }

        // Unknown phase — show everything
        return BuildProfile(FlightCommandGroups, [], NoHidden);
    }

    private static ContextMenuProfile BuildProfile(MenuGroup[] primary, MenuGroup[] explicitSecondary, FrozenSet<MenuGroup> hidden)
    {
        var primarySet = primary.ToHashSet();
        var hiddenSet = hidden;

        // Secondary = all phase groups not in primary and not hidden
        List<MenuGroup> secondary;
        if (explicitSecondary.Length > 0)
        {
            secondary = [.. explicitSecondary];
        }
        else
        {
            secondary = [];
            foreach (var group in AllPhaseGroups)
            {
                if (!primarySet.Contains(group) && !hiddenSet.Contains(group))
                {
                    secondary.Add(group);
                }
            }
        }

        return new ContextMenuProfile(primary, secondary, hidden);
    }

    private static bool IsGroundPhase(string phase)
    {
        return phase
                is "At Parking"
                    or "Pushback"
                    or "Holding After Pushback"
                    or "Taxiing"
                    or "Holding In Position"
                    or "Crossing Runway"
                    or "Runway Exit"
                    or "Holding After Exit"
                    or "AirTaxi"
                    or "LiningUp"
                    or "LinedUpAndWaiting"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
            || phase.StartsWith("Following", StringComparison.Ordinal);
    }

    private static bool IsPatternPhase(string phase)
    {
        return phase is "Pattern Entry" or "Upwind" or "Crosswind" or "Downwind" or "Base" or "MidfieldCrossing";
    }

    private static bool IsHoldingPhase(string phase)
    {
        return phase is "HoldingPattern" or "HPP-L" or "HPP-R" or "HPP" or "HoldingAtFix" or "ProceedToFix";
    }

    private static bool IsTurnPhase(string phase)
    {
        return phase is "S-Turns" || phase.StartsWith("Turn", StringComparison.Ordinal);
    }
}
