namespace Yaat.Client.Services;

/// <summary>
/// Maps ARTCC IDs to FAA Winds and Temperatures Aloft (FD) forecast region codes.
/// Used for aviationweather.gov /api/data/windtemp queries.
/// </summary>
public static class FdRegionMapping
{
    private static readonly Dictionary<string, string> ArtccToRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        // Boston
        ["ZBW"] = "bos",
        ["ZNY"] = "bos",
        ["ZOB"] = "bos",
        ["ZDC"] = "bos",

        // Miami
        ["ZTL"] = "mia",
        ["ZJX"] = "mia",
        ["ZMA"] = "mia",
        ["ZHU"] = "mia",

        // Chicago
        ["ZAU"] = "chi",
        ["ZMP"] = "chi",
        ["ZKC"] = "chi",
        ["ZID"] = "chi",
        ["ZCL"] = "chi",

        // Dallas-Fort Worth
        ["ZFW"] = "dfw",
        ["ZME"] = "dfw",
        ["ZAB"] = "dfw",

        // Salt Lake City
        ["ZDV"] = "slc",
        ["ZLC"] = "slc",
        ["ZSE"] = "slc",

        // San Francisco
        ["ZOA"] = "sfo",
        ["ZLA"] = "sfo",

        // Alaska
        ["ZAN"] = "alaska",
    };

    /// <summary>
    /// Returns the FD region code for the given ARTCC, or null if unknown.
    /// </summary>
    public static string? GetRegion(string artccId)
    {
        return ArtccToRegion.GetValueOrDefault(artccId);
    }
}
