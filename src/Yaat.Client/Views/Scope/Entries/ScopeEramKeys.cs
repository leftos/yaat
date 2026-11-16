namespace Yaat.Client.Views.Scope.Entries;

/// <summary>
/// CRC's ERAM keyboard on the Scope: the four keys it maps to characters of ERAM's own (its <c>EramChar</c> values 0x80, 0x81,
/// 0x87 and 0x88), and the marks the Scope shows in their place. The server finds where a message's text starts by the
/// clear-weather character, so the entry an ERAM entry sends carries them; the preview and the response show marks instead.
/// </summary>
internal static class ScopeEramKeys
{
    /// <summary>ERAM's up arrow, which <c>[</c> types.</summary>
    internal const char UpArrow = '\u0080';

    /// <summary>ERAM's down arrow, which <c>]</c> types.</summary>
    internal const char DownArrow = '\u0081';

    /// <summary>ERAM's clear-weather symbol, which a backtick types.</summary>
    internal const char ClearWeather = '\u0087';

    /// <summary>ERAM's overcast-weather symbol, which <c>~</c> types.</summary>
    internal const char OvercastWeather = '\u0088';

    /// <summary>Replaces each of the four keys <paramref name="text"/> holds with its ERAM character; another character stays.</summary>
    internal static string MapTyped(string text) => new([.. text.Select(MapChar)]);

    /// <summary>Replaces each ERAM character <paramref name="text"/> holds with the mark the Scope shows for it.</summary>
    internal static string ToDisplay(string text) => new([.. text.Select(DisplayChar)]);

    private static char MapChar(char c) =>
        c switch
        {
            '[' => UpArrow,
            ']' => DownArrow,
            '`' => ClearWeather,
            '~' => OvercastWeather,
            _ => c,
        };

    private static char DisplayChar(char c) =>
        c switch
        {
            UpArrow => '↑',
            DownArrow => '↓',
            ClearWeather => '○',
            OvercastWeather => '●',
            _ => c,
        };
}
