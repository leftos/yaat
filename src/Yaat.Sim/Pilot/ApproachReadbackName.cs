using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The approach a clearance names, as a pilot reads it back (7110.65 4-8-1): the type, its variant letter, then the runway,
/// "ILS runway two eight right" / "ILS runway 28R", "RNAV Zulu runway two eight right" / "RNAV Z runway 28R"; a circling
/// approach has no runway, "VOR Alpha" / "VOR A". The spoken form spells the runway and says the variant's phonetic word;
/// the terminal form uses digits and the raw letter.
/// </summary>
public static class ApproachReadbackName
{
    /// <summary>The terminal and spoken words for an approach type code; null for a code no readback names.</summary>
    /// <param name="typeCode">The CIFP approach type code (I, R, H, L, B, V, S, D, N, Q, P, X, T, U).</param>
    /// <returns>The terminal and spoken type words, or null.</returns>
    public static (string Terminal, string Spoken)? TypeWords(char typeCode) =>
        char.ToUpperInvariant(typeCode) switch
        {
            'I' => ("ILS", "ILS"),
            'R' or 'H' => ("RNAV", "RNAV"),
            'L' => ("LOC", "localizer"),
            'B' => ("LOC BC", "localizer back course"),
            'V' or 'S' => ("VOR", "VOR"),
            'D' => ("VOR DME", "VOR DME"),
            'N' => ("NDB", "NDB"),
            'Q' => ("NDB DME", "NDB DME"),
            'P' => ("GPS", "GPS"),
            'X' => ("LDA", "LDA"),
            'T' => ("TACAN", "TACAN"),
            'U' => ("SDF", "SDF"),
            _ => null,
        };

    /// <summary>
    /// The type and variant words of an approach id, typed shorthand or CIFP ("ILS", "RNAV Z" / "RNAV Zulu"), and its runway;
    /// null when the id names no runway or a type no readback names.
    /// </summary>
    /// <param name="approachId">The approach id.</param>
    /// <returns>The terminal and spoken type-and-variant words and the runway, or null.</returns>
    public static (string Terminal, string Spoken, string Runway)? TypeAndRunway(string? approachId)
    {
        if (string.IsNullOrWhiteSpace(approachId) || (NavigationDatabase.ParseShorthand(approachId.Trim()) is not { } parsed))
        {
            return null;
        }

        if ((parsed.TypeCode is not { } typeCode) || (parsed.Runway is not { } runway))
        {
            return null;
        }

        return WithVariant(typeCode, VariantLetter(parsed.Variant)) is { } words ? (words.Terminal, words.Tts, runway) : null;
    }

    /// <summary>The readback name of a typed or CIFP approach id; null when the id names no runway or no known type.</summary>
    /// <param name="approachId">The approach id, as typed ("ILS28R", "RNAV28RZ") or from the CIFP ("H12-Z").</param>
    /// <returns>The terminal and spoken name, or null.</returns>
    public static PilotSpeechText? ForShorthand(string? approachId) =>
        TypeAndRunway(approachId) is { } named ? WithRunway(new PilotSpeechText(named.Terminal, named.Spoken), named.Runway) : null;

    /// <summary>The readback name of a resolved approach procedure; null when its type is one no readback names.</summary>
    /// <param name="procedure">The resolved CIFP approach procedure.</param>
    /// <returns>The terminal and spoken name, or null.</returns>
    public static PilotSpeechText? ForProcedure(CifpApproachProcedure procedure)
    {
        if (string.IsNullOrWhiteSpace(procedure.Runway))
        {
            // A circling approach names its variant after a dash ("VOR-A") and no runway.
            int dash = procedure.ApproachId.LastIndexOf('-');
            char? circlingLetter = (dash >= 0) && (dash < procedure.ApproachId.Length - 1) ? procedure.ApproachId[dash + 1] : null;
            return WithVariant(procedure.TypeCode, circlingLetter);
        }

        string? variant = NavigationDatabase.ParseShorthand(procedure.ApproachId)?.Variant;
        return WithVariant(procedure.TypeCode, VariantLetter(variant)) is { } words
            ? WithRunway(words, RunwayIdentifier.ToDisplayDesignator(procedure.Runway))
            : null;
    }

    private static PilotSpeechText WithRunway(PilotSpeechText type, string runway) =>
        new(
            $"{type.Terminal} runway {PhraseologyVerbalizer.CompactRunway(runway)}",
            $"{type.Tts} runway {PhraseologyVerbalizer.SpellRunway(runway)}"
        );

    /// <summary>The type words with the variant letter after them ("RNAV Z" / "RNAV Zulu"); null for an unknown type.</summary>
    private static PilotSpeechText? WithVariant(char typeCode, char? variant)
    {
        if (TypeWords(typeCode) is not { } type)
        {
            return null;
        }

        if ((variant is not { } letter) || !NatoPhoneticAlphabet.TryGetWord(letter, out string? word))
        {
            return new PilotSpeechText(type.Terminal, type.Spoken);
        }

        string spokenWord = char.ToUpperInvariant(word[0]) + word[1..];
        return new PilotSpeechText($"{type.Terminal} {char.ToUpperInvariant(letter)}", $"{type.Spoken} {spokenWord}");
    }

    /// <summary>The variant letter in the text after a runway designator ("Z", "-Z"); null when there is none.</summary>
    private static char? VariantLetter(string? variant)
    {
        string trimmed = (variant ?? "").Trim().TrimStart('-');
        return (trimmed.Length == 1) && char.IsLetter(trimmed[0]) ? trimmed[0] : null;
    }
}
