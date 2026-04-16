namespace Yaat.Client.Models;

/// <summary>
/// All user-customizable ground view colors (hex strings, no alpha) plus brightness.
/// </summary>
public sealed record GroundColorScheme(
    string Background,
    string Taxiway,
    string TaxiLabel,
    string RampEdge,
    string HoldShort,
    string RunwayFill,
    string RunwayOutline,
    string Aircraft,
    string DatablockText,
    int Brightness
)
{
    public const string DefaultBackground = "#0E0E1A";
    public const string DefaultTaxiway = "#787882";
    public const string DefaultTaxiLabel = "#A0A0AA";
    public const string DefaultRampEdge = "#50505A";
    public const string DefaultHoldShort = "#DCC83C";
    public const string DefaultRunwayFill = "#3C3C3C";
    public const string DefaultRunwayOutline = "#646464";
    public const string DefaultAircraft = "#FFFFFF";
    public const string DefaultDatablockText = "#00E600";
    public const int DefaultBrightness = 100;

    public static GroundColorScheme Default { get; } =
        new(
            DefaultBackground,
            DefaultTaxiway,
            DefaultTaxiLabel,
            DefaultRampEdge,
            DefaultHoldShort,
            DefaultRunwayFill,
            DefaultRunwayOutline,
            DefaultAircraft,
            DefaultDatablockText,
            DefaultBrightness
        );
}
