namespace Yaat.Sim.Commands;

/// <summary>
/// The error ids <see cref="EramEntryEngine"/> answers a refused entry with. Each value is an <c>id</c> from
/// <c>docs/eram/error-responses.yaml</c>; a refusal's message is the id alone, or the id, a space and the contents of the
/// field in error (<c>MsgCofieFormat 12.5X</c>). yaat-server reads the id back into its error table and sends CRC the
/// table's text, so the Sim carries no display wording.
/// </summary>
public static class EramEntryErrors
{
    public const string InvalidMessageType = "MsgInvalidMessageType";
    public const string MessageTooShort = "MsgMessageTooShort";
    public const string MessageTooLong = "MsgMessageTooLong";
    public const string CofieFormat = "MsgCofieFormat";
    public const string AltFormat = "MsgALTFormat";
    public const string HeadingFormat = "MsgInvalidHeadingFormat";
    public const string SpeedFormat = "MsgInvalidSpeedFormat";
    public const string TextFormat = "MsgInvalidTextFormat";
    public const string InvalidDirection = "MsgInvalidDirection";
    public const string InvalidLength = "MsgInvalidLength";
    public const string SessionNotActive = "YaatSessionNotActive";
    public const string AlreadyTracked = "YaatAlreadyTracked";
    public const string NotYourControl = "YaatNotYourControl";
    public const string NonAdaptedSector = "MsgNon-AdaptedSector";
    public const string HandoffToOwner = "YaatHandoffToOwner";
    public const string PoExists = "YaatPoExists";
    public const string PoNotFound = "YaatPoNotFound";

    /// <summary>Every id above, for the conformance test that holds them to <c>error-responses.yaml</c>.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        InvalidMessageType,
        MessageTooShort,
        MessageTooLong,
        CofieFormat,
        AltFormat,
        HeadingFormat,
        SpeedFormat,
        TextFormat,
        InvalidDirection,
        InvalidLength,
        SessionNotActive,
        AlreadyTracked,
        NotYourControl,
        NonAdaptedSector,
        HandoffToOwner,
        PoExists,
        PoNotFound,
    ];
}
