namespace Yaat.Sim;

public static class AtcPositionTypeClassifier
{
    public static string? Classify(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return null;
        }

        var lastUnderscore = callsign.LastIndexOf('_');
        if (lastUnderscore < 0)
        {
            return null;
        }

        var suffix = callsign[(lastUnderscore + 1)..];
        return suffix.ToUpperInvariant() switch
        {
            "TWR" => "TWR",
            "LC" => "TWR",
            "GND" => "GND",
            "GC" => "GND",
            "DEL" => "GND",
            "APP" => "APP",
            "DEP" => "APP",
            "CTR" => "CTR",
            _ => null,
        };
    }
}
