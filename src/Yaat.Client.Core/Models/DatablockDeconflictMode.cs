namespace Yaat.Client.Models;

/// <summary>
/// Opt-in datablock deconfliction mode for a radar or ground view. Off leaves datablocks at
/// their default/leader-direction placement; the other two automatically reposition overlapping
/// datablocks so they stay readable. Lives in Core so <see cref="Yaat.Client.Services.UserPreferences"/>
/// can reference it without pulling in the rendering layer.
/// </summary>
public enum DatablockDeconflictMode
{
    /// <summary>No automatic repositioning — datablocks keep their default/leader-direction placement.</summary>
    Off,

    /// <summary>Each overlapping datablock snaps to one of the eight STARS compass leader directions.</summary>
    CompassSnap,

    /// <summary>Overlapping datablocks slide freely (force-directed push-apart) until separated.</summary>
    FreeForm,
}
