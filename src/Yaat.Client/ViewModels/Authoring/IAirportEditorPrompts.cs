namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The questions the Airport Editor asks the user, answered by the window's dialogs and pickers, so the view model
/// needs no UI to run.
/// </summary>
public interface IAirportEditorPrompts
{
    /// <summary>Asks for the airport whose vNAS map to open from the cache.</summary>
    /// <returns>The airport's FAA or ICAO id, or null when the user cancelled.</returns>
    Task<string?> AskAirportIdAsync();

    /// <summary>Asks for the airport-map file to open (<c>.geojson</c> or <c>.json</c>).</summary>
    /// <returns>The file's path, or null when the user cancelled.</returns>
    Task<string?> PickMapFileAsync();

    /// <summary>Asks for the folder whose combined <c>.geojson</c> to open.</summary>
    /// <returns>The folder's path, or null when the user cancelled.</returns>
    Task<string?> PickMapFolderAsync();
}
