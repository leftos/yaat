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
/// </summary>
public sealed class FixPronunciationDefinition
{
    [JsonPropertyName("fix")]
    public string Fix { get; set; } = "";

    [JsonPropertyName("pronunciations")]
    public List<string> Pronunciations { get; set; } = [];
}
