namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Every threshold and weight the context decoder scores with. Every field is required, so a
/// sweep or a test states the whole set it runs under; <see cref="Default"/> holds the start values.
/// </summary>
public sealed record DecoderSettings
{
    /// <summary>Weight of the raw Levenshtein similarity in <see cref="SpokenFormScore.WordScore"/>; the rest is phonetic.</summary>
    public required double WRaw { get; init; }

    /// <summary>Shortest word, by the longer of the two compared, eligible for fuzzy matching.</summary>
    public required int MinFuzzyChars { get; init; }

    /// <summary>Lowest word score that counts when a heard word is compared with a spoken-form NATO token.</summary>
    public required double NatoCodeAccept { get; init; }

    /// <summary>Score at or above which the callsign is resolved.</summary>
    public required double CallsignAccept { get; init; }

    /// <summary>Score at or above which a wrong callsign still produces a best-guess say-again.</summary>
    public required double CallsignSayAgainFloor { get; init; }

    /// <summary>How far below the best callsign score a hypothesis stays eligible on the joint score.</summary>
    public required double CallsignJointMargin { get; init; }

    /// <summary>How many callsign candidates survive the first stage.</summary>
    public required int CallsignTopK { get; init; }

    /// <summary>How many tokens before the first token a callsign span may start.</summary>
    public required int MaxStartOffset { get; init; }

    /// <summary>Longest token span a callsign match may cover.</summary>
    public required int MaxCallsignSpan { get; init; }

    /// <summary>Longest token span an entity match may cover.</summary>
    public required int MaxEntitySpan { get; init; }

    /// <summary>Score at or above which a named entity is accepted.</summary>
    public required double EntityAcceptName { get; init; }

    /// <summary>Score at or above which an entity whose form is code only is accepted.</summary>
    public required double EntityAcceptCodeOnly { get; init; }

    /// <summary>Score at or above which an entity is accepted when no callsign resolved.</summary>
    public required double EntityAcceptNoCallsign { get; init; }

    /// <summary>Prior of an airport entity, below an aircraft's own entity so the own one wins a tie.</summary>
    public required double AirportEntityPrior { get; init; }

    /// <summary>Prior of a bare runway name.</summary>
    public required double BareRunwayPrior { get; init; }

    /// <summary>Prior of an airline flight number heard without its telephony.</summary>
    public required double NumberOnlyPrior { get; init; }

    /// <summary>Prior of a GA callsign's last three characters heard alone.</summary>
    public required double GaLastThreePrior { get; init; }

    /// <summary>Prior of a GA callsign's shortened N-number form.</summary>
    public required double ShortGaPrior { get; init; }

    /// <summary>Prior of a telephony form with a trailing "air" or "airlines" dropped.</summary>
    public required double TelephonyMinusAirPrior { get; init; }

    /// <summary>Exponent applied to the joint score's coverage term.</summary>
    public required double CoverageExponent { get; init; }

    /// <summary>Word confidence at or above which a non-exact match is refused.</summary>
    public required double ConfidentWordProb { get; init; }

    /// <summary>Weight of the acoustic score in the n-best combination.</summary>
    public required double AcousticWeight { get; init; }

    /// <summary>Penalty in nats per n-best rank when no acoustic score is available.</summary>
    public required double MissingAcousticRankPenalty { get; init; }

    /// <summary>The start values every caller begins from.</summary>
    public static DecoderSettings Default { get; } =
        new()
        {
            WRaw = 0.5,
            MinFuzzyChars = 4,
            NatoCodeAccept = 0.75,
            CallsignAccept = 0.85,
            CallsignSayAgainFloor = 0.60,
            CallsignJointMargin = 0.10,
            CallsignTopK = 5,
            MaxStartOffset = 2,
            MaxCallsignSpan = 8,
            MaxEntitySpan = 6,
            EntityAcceptName = 0.80,
            EntityAcceptCodeOnly = 0.95,
            EntityAcceptNoCallsign = 0.90,
            AirportEntityPrior = 0.92,
            BareRunwayPrior = 0.90,
            NumberOnlyPrior = 0.90,
            GaLastThreePrior = 0.90,
            ShortGaPrior = 0.95,
            TelephonyMinusAirPrior = 0.95,
            CoverageExponent = 0.5,
            ConfidentWordProb = 0.97,
            AcousticWeight = 0.3,
            MissingAcousticRankPenalty = 0.1,
        };
}
