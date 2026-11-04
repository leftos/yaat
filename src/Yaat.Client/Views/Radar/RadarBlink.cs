namespace Yaat.Client.Views.Radar;

/// <summary>
/// The one phase every blinking radar token (a flashing datablock field, a blinking target symbol) follows: on for half a
/// second, off for half a second.
/// </summary>
internal static class RadarBlink
{
    /// <summary>
    /// The millisecond clock the phase is read from. A screenshot harness pins it to a value on the on phase, so blinking
    /// tokens are always drawn.
    /// </summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Whether a blinking token is drawn now.</summary>
    internal static bool IsOn => Clock() / 500 % 2 == 0;
}
