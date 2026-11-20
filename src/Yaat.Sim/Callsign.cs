using System.Text.RegularExpressions;

namespace Yaat.Sim;

public static partial class Callsign
{
    [GeneratedRegex(@"^[A-Z0-9\-]{1,7}$")]
    private static partial Regex CallsignRegex();

    public static bool IsValid(string? callsign) => !string.IsNullOrEmpty(callsign) && CallsignRegex().IsMatch(callsign);
}
