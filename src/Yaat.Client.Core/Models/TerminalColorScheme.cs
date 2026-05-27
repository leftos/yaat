namespace Yaat.Client.Models;

/// <summary>
/// Operator-tunable foreground colors for each <see cref="TerminalEntryKind"/>, stored
/// as 6-digit hex strings. Defaults match the original hard-coded scheme: white for
/// commands, gray scale for system/response, lime green for SAY-class transmissions
/// and pilot speech, orange/red for warnings/errors, cyan for chat.
/// </summary>
public sealed record TerminalColorScheme(
    string Command,
    string Response,
    string System,
    string Say,
    string PilotSpeech,
    string Warning,
    string Error,
    string Chat,
    string Tdls
)
{
    public const string DefaultCommand = "#FFFFFF";
    public const string DefaultResponse = "#D3D3D3";
    public const string DefaultSystem = "#808080";
    public const string DefaultSay = "#32CD32";
    public const string DefaultPilotSpeech = "#32CD32";
    public const string DefaultWarning = "#FFA500";
    public const string DefaultError = "#FF0000";
    public const string DefaultChat = "#00FFFF";

    /// <summary>
    /// Default vTDLS color — bright amber, matching real-world ACARS terminals which display
    /// text on a dark amber/yellow phosphor. Distinct from Say/PilotSpeech (green) and Chat
    /// (cyan) so PDC traffic is visually identifiable in the room's terminal log at a glance.
    /// </summary>
    public const string DefaultTdls = "#FFB000";

    public static TerminalColorScheme Default { get; } =
        new(DefaultCommand, DefaultResponse, DefaultSystem, DefaultSay, DefaultPilotSpeech, DefaultWarning, DefaultError, DefaultChat, DefaultTdls);

    public string For(TerminalEntryKind kind) =>
        kind switch
        {
            TerminalEntryKind.Command => Command,
            TerminalEntryKind.Response => Response,
            TerminalEntryKind.System => System,
            TerminalEntryKind.Say => Say,
            TerminalEntryKind.PilotSpeech => PilotSpeech,
            TerminalEntryKind.Warning => Warning,
            TerminalEntryKind.Error => Error,
            TerminalEntryKind.Chat => Chat,
            TerminalEntryKind.Tdls => Tdls,
            _ => DefaultCommand,
        };
}
