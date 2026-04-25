namespace Yaat.Sim.Pilot;

/// <summary>
/// Controls how the pilot AI varies its readback phraseology in solo-training mode.
/// <see cref="Verbatim"/> emits the textbook readback (the first-declared <c>PhraseologyRule</c>
/// pattern, inverted) for every command.
/// </summary>
public enum PilotPersonality
{
    Verbatim,
}
