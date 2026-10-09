namespace Yaat.Client.ContextMenus;

/// <summary>What a Push back to… target is, which picks its section and its badge.</summary>
public enum MenuPushTargetKind
{
    /// <summary>A taxilane (a taxiway outside the movement area) behind the aircraft, sent as a bare <c>PUSH &lt;twy&gt;</c>.</summary>
    Taxilane,

    /// <summary>A movement-area taxiway behind the aircraft, sent as a bare <c>PUSH &lt;twy&gt;</c>.</summary>
    Taxiway,

    /// <summary>A ramp spot, sent as <c>PUSH $spot</c>.</summary>
    Spot,
}

/// <summary>One facing a Push back to… target can end on.</summary>
/// <param name="Cardinal">The magnetic 8-point cardinal it ends facing: <c>N</c>, <c>NW</c>.</param>
/// <param name="Command">The command that pushes to the target ending on it: <c>PUSH TE FACE N</c>.</param>
public sealed record MenuPushFacing(string Cardinal, string Command);

/// <summary>
/// One target the Push back to… submenu offers, planned as a tug move from the aircraft's stand or from the pose it is
/// held at after a pushback, and checked against the aircraft about it: what it is, its name, how the move meets it,
/// how long the planned tow is, the command it sends, the facings it can end on, and the parked neighbour that blocks
/// it now.
/// </summary>
/// <param name="Kind">What the target is.</param>
/// <param name="Name">The taxiway's or spot's own name, as the layout names it.</param>
/// <param name="Note">How a taxiway push meets the taxiway (<c>alongside</c> or <c>across</c>); null for a spot.</param>
/// <param name="PathLengthFt">The planned tug path's length, feet.</param>
/// <param name="Command">The finished command the row sends for the menu's aircraft.</param>
/// <param name="Facings">
/// For a taxilane or taxiway, one facing per magnetic cardinal the push can end on, each sending
/// <c>PUSH &lt;twy&gt; FACE &lt;dir&gt;</c>; <c>[]</c> for a spot or a target with no stored facing.
/// </param>
/// <param name="BlockedBy">The callsign of the parked or held neighbour that blocks the target now; null when it is clear.</param>
public sealed record MenuPushTarget(
    MenuPushTargetKind Kind,
    string Name,
    string? Note,
    double PathLengthFt,
    string Command,
    IReadOnlyList<MenuPushFacing> Facings,
    string? BlockedBy
)
{
    /// <summary>The verb, with its trailing space, every target's <see cref="Command"/> starts with.</summary>
    public const string PushVerb = "PUSH ";

    /// <summary>
    /// The forced form of <see cref="Command"/>, which pushes past a blocking neighbour: <c>PUSHF TF</c> for <c>PUSH TF</c>,
    /// <c>PUSHF $C</c> for <c>PUSH $C</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Command"/> does not start with the <c>PUSH</c> verb.</exception>
    public string ForcedCommand =>
        Command.StartsWith(PushVerb, StringComparison.Ordinal)
            ? $"PUSHF {Command[PushVerb.Length..]}"
            : throw new InvalidOperationException($"Push target command '{Command}' does not start with '{PushVerb}', so it has no forced form.");
}
