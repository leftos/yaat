using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// Author-facing JSON shape for a single fix pronunciation hint. Each entry names a fix (by its
/// canonical name or any alias the navdb knows) and lists one or more phonetic spellings that
/// should be injected into Whisper's <c>initial_prompt</c> when that fix is programmed for the
/// selected aircraft. Whisper's decoder biases toward any tokens present in the prompt, so seeding
/// both the canonical spelling and its phonetic form catches both correct and approximate
/// pronunciations — the downstream <c>PhoneticFixMatcher</c> normalizes either back to canonical.
///
/// Example: <c>{"fix": "SYRAH", "pronunciations": ["see rah"]}</c> — if SYRAH is on the selected
/// aircraft's route, Whisper sees "SYRAH see rah" in its prompt and will happily decode either
/// spelling depending on how the controller pronounces it.
///
/// The optional <c>displayName</c> marks an entry as a genuine human-readable name (e.g. a visual
/// reporting point: <c>{"fix": "VPCBT", "pronunciations": ["Lake Chabot"], "displayName": "Lake Chabot"}</c>)
/// rather than a pure phonetic spelling hint. Only entries with a <c>displayName</c> surface in
/// operator-facing terminal text — phonetic-only entries like SYRAH stay hidden from the display,
/// so "see rah" never leaks into a command readback. Display names may also differ from
/// pronunciations: <c>VPCOL</c> displays the corrected "Oakland Coliseum" while its pronunciations
/// retain the common misspelling for STT robustness.
/// </summary>
public sealed class FixPronunciationDefinition
{
    [JsonPropertyName("fix")]
    public string Fix { get; set; } = "";

    [JsonPropertyName("pronunciations")]
    public List<string> Pronunciations { get; set; } = [];

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}
