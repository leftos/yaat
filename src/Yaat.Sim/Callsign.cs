using System.Text.RegularExpressions;

namespace Yaat.Sim;

public static class Callsign
{
    private static readonly Regex Pattern = new(@"^[A-Z0-9\-]{1,7}$", RegexOptions.Compiled);

    public static bool IsValid(string? callsign) => !string.IsNullOrEmpty(callsign) && Pattern.IsMatch(callsign);
}
