namespace Yaat.Sim.Authoring.Stamping;

/// <summary>One field a stamp changed.</summary>
/// <param name="Path">
/// The JSON Pointer of the field in the aircraft. A list whose items all carry an id matches items by id, an empty id only
/// at its own position: a removed item is numbered by its old position, an added or changed one by its new position.
/// </param>
/// <param name="Kind">Whether the field changed, was added or was removed.</param>
/// <param name="OldValue">The old value as JSON text; null when the field was added.</param>
/// <param name="NewValue">The new value as JSON text; null when the field was removed.</param>
public sealed record StampChange(string Path, DiffKind Kind, string? OldValue, string? NewValue);
