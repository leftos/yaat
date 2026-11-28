using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// Writes a <see cref="StripStep"/> as the one preset command the editor owns: <c>STRIP FAC/Bay/rack[/slot]</c>,
/// <c>AN box [text]</c>, <c>STRIPO</c>, <c>STRIPD</c>, <c>HSC FAC/Bay/rack lines</c>, <c>HSA lines</c>,
/// <c>HSM FAC/Bay/rack[/slot]</c>, <c>HSO</c>, <c>HSS</c>, <c>HSD</c>. Text the parser would split is removed: the
/// <c>,</c> and <c>;</c> separators and their word aliases (<see cref="CommandSchemeParser.SeparatorAliases"/>), and the
/// <c>\</c> line separator inside a half-strip line. An arrival's full-strip commands carry its <c>ARRIVAL_</c> id,
/// since the bare form addresses the departure strip only.
/// </summary>
public static class StripCommandText
{
    /// <summary>The most lines a half-strip step carries: the handler adds the callsign line to the parser's six.</summary>
    public const int MaxHalfStripUserLines = 5;

    private static readonly ILogger Log = SimLog.CreateLogger("StripCommandText");

    /// <summary>
    /// True when the engine would give the aircraft an arrival strip, by the engine's own rule
    /// (<see cref="StripRequests.IsArrivalCandidate(AircraftState, string?)"/>): it spawns airborne, is no live-traffic
    /// shadow, and its filed destination is the primary airport. Every other aircraft uses the bare departure forms.
    /// </summary>
    public static bool IsArrival(ScenarioAircraft aircraft, string? primaryAirportId)
    {
        AircraftState state = ScenarioLoader.CreateBaseState(aircraft, primaryAirportId, primaryApproach: null);
        state.IsOnGround = !ScenarioLoader.IsAirborneSpawn(aircraft, primaryAirportId);
        return StripRequests.IsArrivalCandidate(state, primaryAirportId);
    }

    /// <summary>The <c>ARRIVAL_{callsign}</c> id an arrival's full-strip commands carry, or null for any other aircraft.</summary>
    public static string? ArrivalStripId(ScenarioAircraft aircraft, string? primaryAirportId) =>
        IsArrival(aircraft, primaryAirportId) ? $"{StripMutations.ArrivalStripIdPrefix}{aircraft.AircraftId}" : null;

    /// <summary>
    /// Writes the step's command text, or says why it cannot be written: an <see cref="StripStepKind.Other"/> or external
    /// step, a missing box or bay, a rack or slot below 1, more than <see cref="MaxHalfStripUserLines"/> lines, or an
    /// <c>HSA</c> whose first word would read back as a bay.
    /// </summary>
    /// <param name="step">The step to write.</param>
    /// <param name="arrivalStripId">The aircraft's <see cref="ArrivalStripId"/>; null for a departure.</param>
    /// <param name="command">The command text when the step can be written.</param>
    /// <param name="error">Why it cannot, otherwise.</param>
    public static bool TryFormat(
        StripStep step,
        string? arrivalStripId,
        [NotNullWhen(true)] out string? command,
        [NotNullWhen(false)] out string? error
    )
    {
        command = null;
        error = step.SourceText is not null ? "an external strip step is not written by the editor" : null;
        if (error is null)
        {
            (command, error) = Format(step, arrivalStripId is null ? "" : arrivalStripId + " ");
        }

        if (error is not null)
        {
            command = null;
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Strip step {Kind} not written: {Error}", step.Kind, error);
            }
            return false;
        }

        return command is not null;
    }

    private static readonly Dictionary<StripStepKind, string> BareHalfStripVerbs = new()
    {
        [StripStepKind.HalfOffset] = "HSO",
        [StripStepKind.HalfSlide] = "HSS",
        [StripStepKind.HalfDelete] = "HSD",
    };

    private static (string? Command, string? Error) Format(StripStep step, string idPrefix) =>
        step.Kind switch
        {
            StripStepKind.Move => FormatBayCommand("STRIP " + idPrefix, step, allowSlot: true),
            StripStepKind.Annotate => FormatAnnotate(step, idPrefix),
            StripStepKind.Offset => (("STRIPO " + idPrefix).TrimEnd(), null),
            StripStepKind.Delete => (("STRIPD " + idPrefix).TrimEnd(), null),
            _ => FormatHalfStrip(step),
        };

    private static (string? Command, string? Error) FormatHalfStrip(StripStep step)
    {
        if (BareHalfStripVerbs.TryGetValue(step.Kind, out string? verb))
        {
            return (verb, null);
        }

        return step.Kind switch
        {
            StripStepKind.HalfCreate => FormatHalfCreate(step),
            StripStepKind.HalfAmend => FormatHalfAmend(step),
            StripStepKind.HalfMove => FormatBayCommand("HSM ", step, allowSlot: true),
            _ => (null, $"{step.Kind} strip steps are read-only"),
        };
    }

    private static (string? Command, string? Error) FormatAnnotate(StripStep step, string idPrefix)
    {
        if (step.Box is null)
        {
            return (null, "an annotation step needs a box");
        }

        string text = CleanText(step.Text);
        string head = $"AN {idPrefix}{step.Box.Written}";
        return (text.Length == 0 ? head : $"{head} {text}", null);
    }

    private static (string? Command, string? Error) FormatBayCommand(string verbAndId, StripStep step, bool allowSlot)
    {
        if (BaySpec(step, allowSlot) is not { } spec)
        {
            return (null, BayError(step, allowSlot));
        }

        return (verbAndId + spec, null);
    }

    private static (string? Command, string? Error) FormatHalfCreate(StripStep step)
    {
        if (BaySpec(step, allowSlot: false) is not { } spec)
        {
            return (null, BayError(step, allowSlot: false));
        }

        if (FormatLines(step.Lines) is not { } lines)
        {
            return (null, $"a half-strip carries at most {MaxHalfStripUserLines} lines");
        }

        return (lines.Length == 0 ? $"HSC {spec}" : $"HSC {spec} {lines}", null);
    }

    private static (string? Command, string? Error) FormatHalfAmend(StripStep step)
    {
        if (FormatLines(step.Lines) is not { } lines)
        {
            return (null, $"a half-strip carries at most {MaxHalfStripUserLines} lines");
        }

        string head = lines.Split(' ', 2)[0];
        bool readsAsBay = head.Contains('/') && !head.Contains('\\');
        bool readsAsId = head.StartsWith(StripMutations.HalfStripIdPrefix, StringComparison.OrdinalIgnoreCase);
        if (readsAsBay || readsAsId)
        {
            return (null, $"the first line's first word '{head}' would read as a bay or a half-strip id");
        }

        return (lines.Length == 0 ? "HSA" : $"HSA {lines}", null);
    }

    /// <summary>The bay as <c>FAC/Bay/rack[/slot]</c>, or null when a part is missing or out of range.</summary>
    private static string? BaySpec(StripStep step, bool allowSlot)
    {
        string facility = CleanBayPart(step.FacilityId);
        string bay = CleanBayPart(step.BayName);
        if ((!PartsValid(facility, bay, step.Rack)) || (!SlotValid(step.Slot, allowSlot)))
        {
            return null;
        }

        string spec = string.Create(CultureInfo.InvariantCulture, $"{facility}/{bay}/{step.Rack}");
        return step.Slot is { } slot ? string.Create(CultureInfo.InvariantCulture, $"{spec}/{slot}") : spec;
    }

    private static bool PartsValid(string facility, string bay, int? rack) =>
        (facility.Length > 0) && (!facility.Contains(' ')) && (bay.Length > 0) && (rack >= 1);

    private static bool SlotValid(int? slot, bool allowSlot) => (slot is null) || (allowSlot && (slot >= 1));

    private static string BayError(StripStep step, bool allowSlot) =>
        $"{step.Kind} needs a facility, a bay, a rack of 1 or more{(allowSlot ? " and an optional slot of 1 or more" : " and no slot")}";

    /// <summary>The lines joined by <c>\</c>, each cleaned; null when there are too many; empty when all are empty.</summary>
    private static string? FormatLines(IReadOnlyList<string>? lines)
    {
        List<string> cleaned = [.. (lines ?? []).Select(l => CleanText(l.Replace("\\", "", StringComparison.Ordinal)))];
        if (cleaned.Count > MaxHalfStripUserLines)
        {
            return null;
        }

        return cleaned.All(l => l.Length == 0) ? "" : string.Join('\\', cleaned);
    }

    private static string CleanBayPart(string? part) => CollapseWhitespace((part ?? "").Replace("/", "", StringComparison.Ordinal));

    /// <summary>Removes the compound separators and their word aliases, and collapses whitespace runs.</summary>
    private static string CleanText(string? text)
    {
        string withoutSeparators = (text ?? "").Replace(",", "", StringComparison.Ordinal).Replace(";", "", StringComparison.Ordinal);
        IEnumerable<string> words = withoutSeparators
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !CommandSchemeParser.SeparatorAliases.Any(a => a.Word.Equals(w, StringComparison.OrdinalIgnoreCase)));
        return string.Join(' ', words);
    }

    private static string CollapseWhitespace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
