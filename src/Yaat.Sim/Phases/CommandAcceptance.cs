namespace Yaat.Sim.Phases;

public enum CommandAcceptanceStatus
{
    Allowed,
    Rejected,
    ClearsPhase,
}

/// <summary>
/// Phase response to a command. <see cref="Allowed"/> means the phase processes the command
/// while staying active; <see cref="ClearsPhase"/> means the phase ends and the command goes
/// to the regular dispatcher; <see cref="Rejected(string)"/> means the command is denied and
/// must carry a human-readable reason that explains *why* this phase doesn't accept it.
/// </summary>
public readonly record struct CommandAcceptance(CommandAcceptanceStatus Status, string? Reason = null)
{
    public static readonly CommandAcceptance Allowed = new(CommandAcceptanceStatus.Allowed);
    public static readonly CommandAcceptance ClearsPhase = new(CommandAcceptanceStatus.ClearsPhase);

    public static CommandAcceptance Rejected(string reason) => new(CommandAcceptanceStatus.Rejected, reason);

    public bool IsAllowed => Status == CommandAcceptanceStatus.Allowed;
    public bool IsRejected => Status == CommandAcceptanceStatus.Rejected;
    public bool ClearsThePhase => Status == CommandAcceptanceStatus.ClearsPhase;
}
