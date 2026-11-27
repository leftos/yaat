namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What a left click on the Airport Editor's map does.</summary>
public enum AirportEditorMode
{
    /// <summary>Selects the feature under the click, and drags the selected line's vertex or a point.</summary>
    Select,

    /// <summary>Adds a taxiway vertex; a double-click on the last one, or Enter, finishes the taxiway.</summary>
    DrawTaxiway,

    /// <summary>Adds a runway end; the second one finishes the runway.</summary>
    DrawRunway,

    /// <summary>Places a parking position.</summary>
    PlaceParking,

    /// <summary>Places a spot.</summary>
    PlaceSpot,

    /// <summary>Places a helipad.</summary>
    PlaceHelipad,
}

/// <summary>The Airport Editor modes' names and the features they make.</summary>
public static class AirportEditorModes
{
    /// <summary>The mode's name, as the HUD shows it.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>
    /// The name: <c>Select</c>, <c>Draw taxiway</c>, <c>Draw runway</c>, <c>Place parking</c>, <c>Place spot</c> or
    /// <c>Place helipad</c>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a mode.</exception>
    public static string Text(AirportEditorMode mode) =>
        mode switch
        {
            AirportEditorMode.Select => "Select",
            AirportEditorMode.DrawTaxiway => "Draw taxiway",
            AirportEditorMode.DrawRunway => "Draw runway",
            AirportEditorMode.PlaceParking => "Place parking",
            AirportEditorMode.PlaceSpot => "Place spot",
            AirportEditorMode.PlaceHelipad => "Place helipad",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not an Airport Editor mode"),
        };

    /// <summary>The type of the feature a mode makes.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns><c>taxiway</c>, <c>runway</c>, <c>parking</c>, <c>spot</c> or <c>helipad</c>, or null for Select.</returns>
    public static string? FeatureType(AirportEditorMode mode) =>
        mode switch
        {
            AirportEditorMode.DrawTaxiway => "taxiway",
            AirportEditorMode.DrawRunway => "runway",
            AirportEditorMode.PlaceParking => "parking",
            AirportEditorMode.PlaceSpot => "spot",
            AirportEditorMode.PlaceHelipad => "helipad",
            _ => null,
        };

    /// <summary>A new feature's name prefix and the name of the undo step that adds it.</summary>
    /// <param name="featureType">The feature's type.</param>
    /// <returns>The prefix (<c>TWY</c>) and the step's name (<c>Draw taxiway</c>).</returns>
    /// <exception cref="InvalidOperationException">The editor does not create features of that type.</exception>
    internal static (string Prefix, string Description) NewFeatureStyle(string featureType) =>
        featureType switch
        {
            "taxiway" => ("TWY", "Draw taxiway"),
            "runway" => ("RWY", "Draw runway"),
            "parking" => ("PARK", "Place parking"),
            "spot" => ("SPOT", "Place spot"),
            "helipad" => ("HELI", "Place helipad"),
            _ => throw new InvalidOperationException($"The editor does not create {featureType} features"),
        };
}
