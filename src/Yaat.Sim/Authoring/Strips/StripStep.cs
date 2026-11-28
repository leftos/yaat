namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// One strip step of a scenario aircraft, read from or written to one of its preset commands. An owned step is a
/// preset holding exactly one strip command in the editor's spelling; an external step is any other preset holding a
/// strip command, carried with its source text and never rewritten.
/// </summary>
/// <param name="Kind">What the step does.</param>
/// <param name="TimeOffsetSeconds">Seconds after spawn the preset fires (the preset's time offset).</param>
/// <param name="PresetId">The preset the step was read from; null for a step not yet written.</param>
/// <param name="FacilityId">The bay's facility, for <see cref="StripStepKind.Move"/>, <see cref="StripStepKind.HalfCreate"/>
/// and <see cref="StripStepKind.HalfMove"/>.</param>
/// <param name="BayName">The bay's name as the command spells it (upper case after a read).</param>
/// <param name="Rack">The 1-based rack.</param>
/// <param name="Slot">The optional 1-based slot within the rack (<see cref="StripStepKind.Move"/> and <see cref="StripStepKind.HalfMove"/>).</param>
/// <param name="Box">The annotation box, for <see cref="StripStepKind.Annotate"/>.</param>
/// <param name="Text">The annotation text; null clears the box.</param>
/// <param name="Lines">The half-strip's user lines, for <see cref="StripStepKind.HalfCreate"/> and <see cref="StripStepKind.HalfAmend"/>.</param>
/// <param name="SourceText">An external step's whole preset command text; null for an owned step.</param>
/// <param name="ConditionText">An external step's condition, as text; null when its strip command has none.</param>
/// <param name="DerivedTimeSeconds">An external step's derived firing time; not derived yet, so always null.</param>
public sealed record StripStep(
    StripStepKind Kind,
    int TimeOffsetSeconds,
    string? PresetId = null,
    string? FacilityId = null,
    string? BayName = null,
    int? Rack = null,
    int? Slot = null,
    StripBox? Box = null,
    string? Text = null,
    IReadOnlyList<string>? Lines = null,
    string? SourceText = null,
    string? ConditionText = null,
    int? DerivedTimeSeconds = null
);
