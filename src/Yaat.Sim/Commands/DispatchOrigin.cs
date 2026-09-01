namespace Yaat.Sim.Commands;

/// <summary>
/// Who issued a controller command. An AI-controller dispatch runs the same dispatcher a human's does, but it is
/// never the student establishing two-way communications (<c>HasMadeInitialContact</c> stays untouched, like a
/// scenario-scripted preset) and is never scored by the solo-training evaluator as a student action.
/// </summary>
public enum DispatchOrigin
{
    Human,
    ControllerAi,
}

/// <summary>
/// The synthetic connection id an AI position dispatches under (<c>"AI:{positionId}"</c>). The origin is derived from
/// the connection id at every dispatch site — live and replay alike — so a recorded AI command (which already carries
/// its connection id) replays with the same origin without a recording-schema change.
/// </summary>
public static class AiConnectionId
{
    public const string Prefix = "AI:";

    public static string Format(string positionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        return Prefix + positionId;
    }

    public static bool IsAi(string? connectionId) => connectionId is not null && connectionId.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? connectionId, out string positionId)
    {
        if (IsAi(connectionId) && connectionId!.Length > Prefix.Length)
        {
            positionId = connectionId[Prefix.Length..];
            return true;
        }

        positionId = "";
        return false;
    }

    public static DispatchOrigin OriginOf(string? connectionId) => IsAi(connectionId) ? DispatchOrigin.ControllerAi : DispatchOrigin.Human;
}
