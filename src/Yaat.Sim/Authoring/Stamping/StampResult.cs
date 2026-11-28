using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>What <see cref="RouteStamp.Apply(ScenarioAircraft, StampRoute, StampProfile, StampContext)"/> made of one aircraft.</summary>
/// <param name="Aircraft">A copy of the aircraft with the stamp written; an unchanged copy when the stamp was skipped.</param>
/// <param name="Changes">Every field the stamp changed, in document order: the JSON diff of the old and new aircraft.</param>
/// <param name="Warnings">One line per warning; a line that starts with <c>Error:</c> names something the stamp did not write.</param>
/// <param name="SkipReason">Why the profile does not apply to the aircraft; null when it was stamped.</param>
public sealed record StampResult(ScenarioAircraft Aircraft, IReadOnlyList<StampChange> Changes, IReadOnlyList<string> Warnings, string? SkipReason);
