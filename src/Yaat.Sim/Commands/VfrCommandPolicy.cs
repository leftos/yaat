namespace Yaat.Sim.Commands;

/// <summary>
/// How far the VFR-only command set opens up for an IFR aircraft. The controller picks this;
/// the aircraft's flight rules are never changed by the choice — an IFR aircraft that accepts
/// <c>EF</c> stays IFR.
/// </summary>
/// <remarks>
/// Member order is load-bearing: the client's settings ComboBox maps its selected index onto
/// this enum, so new members must be appended rather than inserted.
/// </remarks>
public enum VfrCommandsForIfr
{
    /// <summary>VFR-only commands are refused for IFR aircraft — <c>CIFR</c> first.</summary>
    None,

    /// <summary>Only <c>EF</c> (enter final) is accepted for an IFR aircraft.</summary>
    EnterFinalOnly,

    /// <summary>Every VFR-only command is accepted for an IFR aircraft.</summary>
    All,
}

/// <summary>
/// Classifies commands that model VFR-only operations — traffic-pattern work, the pattern-relative
/// takeoff modifiers, <c>FOLLOW</c>, and the <c>CM A/B</c> altitude restrictions — and decides which
/// of them an IFR aircraft may still be given under a chosen <see cref="VfrCommandsForIfr"/>.
///
/// <para>The predicates live here rather than in <see cref="CommandDispatcher"/> because the desktop
/// client is what enforces them: it evaluates the policy against the target aircraft's flight rules
/// before the command reaches the wire. Keeping one list means the client and the command grammar
/// cannot drift apart.</para>
/// </summary>
public static class VfrCommandPolicy
{
    /// <summary>
    /// Traffic-pattern and VFR-tower commands. These drive pattern geometry (legs, spacing, orbits,
    /// touch-and-go) that only makes sense for an aircraft flying the visual pattern.
    /// </summary>
    public static bool RequiresVfr(ParsedCommand command) =>
        command
            is EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftCrosswindCommand
                or EnterRightCrosswindCommand
                or EnterLeftBaseCommand
                or EnterRightBaseCommand
                or EnterFinalCommand
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand
                or TurnCrosswindCommand
                or TurnDownwindCommand
                or TurnBaseCommand
                or ExtendPatternCommand
                or MakeShortApproachCommand
                or MakeNormalApproachCommand
                or MakeLeft360Command
                or MakeRight360Command
                or MakeLeft270Command
                or MakeRight270Command
                or CircleAirportCommand
                or PatternSizeCommand
                or MakeLeftSTurnsCommand
                or MakeRightSTurnsCommand
                or OffsetLeftPatternCommand
                or OffsetRightPatternCommand
                or Plan270Command
                or Cancel270Command
                or TouchAndGoCommand
                or StopAndGoCommand
                or LowApproachCommand
                or ClearedForOptionCommand
                or HoldPresentPosition360Command
                or HoldPresentPositionHoverCommand
                or HoldAtFixOrbitCommand
                or HoldAtFixHoverCommand;

    /// <summary>
    /// Whether a departure instruction is one of the pattern-relative takeoff modifiers, which peel
    /// the aircraft toward the traffic pattern at liftoff: a relative turn off runway heading
    /// (MR{N}/ML{N}), a crosswind/downwind pattern-exit departure (MRC/MLC, MRD/MLD), or closed
    /// traffic (MLT/MRT). Given to an IFR departure they abandon its SID or filed route with no
    /// amended clearance (7110.65 §4-3-2, §5-6-2).
    ///
    /// <para>Everything else is a clearance an IFR departure receives routinely and is never gated:
    /// a bare CTO (follow the SID), CTO with an assigned heading, CTO RH (runway heading, awaiting
    /// vectors), a helicopter present-position hover, on course, and direct-to-fix — the last two
    /// being ordinary IFR clearances (§5-6-2 <c>CLEARED DIRECT</c>) that merely happen to be modelled
    /// as departure modifiers here.</para>
    /// </summary>
    public static bool IsVfrOnlyDeparture(DepartureInstruction? departure) =>
        departure is RelativeTurnDeparture or PatternExitDeparture or ClosedTrafficDeparture;

    /// <summary>
    /// Whether the command models a VFR-only operation. Beyond <see cref="RequiresVfr"/> this covers
    /// the VFR-only takeoff modifiers, <c>FOLLOW</c> (IFR traffic uses <c>CVA FOLLOW</c> for visual
    /// separation), and the <c>CM A/B</c> at-or-above / at-or-below altitude restrictions.
    /// </summary>
    public static bool IsVfrOnly(ParsedCommand command) =>
        RequiresVfr(command)
        || (command is FollowCommand)
        || (command is ClimbMaintainCommand { Modifier: not AltitudeAssignmentModifier.None })
        || (command is ClearedForTakeoffCommand cto && IsVfrOnlyDeparture(cto.Departure))
        || (command is ClearedTakeoffPresentCommand ctopp && IsVfrOnlyDeparture(ctopp.Departure));

    /// <summary>
    /// Whether an IFR aircraft may be given this command under <paramref name="mode"/>. Commands that
    /// are not VFR-only are always allowed. Callers gate only IFR aircraft — a VFR aircraft accepts
    /// the whole set regardless of the mode.
    /// </summary>
    public static bool AllowsForIfr(ParsedCommand command, VfrCommandsForIfr mode)
    {
        if (!IsVfrOnly(command))
        {
            return true;
        }

        return mode switch
        {
            VfrCommandsForIfr.All => true,
            VfrCommandsForIfr.EnterFinalOnly => command is EnterFinalCommand,
            _ => false,
        };
    }
}
