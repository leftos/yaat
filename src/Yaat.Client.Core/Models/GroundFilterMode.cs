namespace Yaat.Client.Models;

/// <summary>
/// Tri-state filter for ground view elements: both icon+label, icon only, or fully hidden.
/// Lives in Core so <see cref="Yaat.Client.Services.UserPreferences"/> can reference it
/// without pulling in the ground-view rendering layer.
/// </summary>
public enum GroundFilterMode
{
    LabelsAndIcons,
    IconsOnly,
    Off,
}
