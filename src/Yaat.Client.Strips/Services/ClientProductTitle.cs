using System.Globalization;

namespace Yaat.Client.Services;

/// <summary>
/// Shared tab/window/page title format for the strips-family products:
/// <c>"(N) {FACILITY} - {product}"</c>, with an <c>" (YAAT)"</c> suffix on the
/// browser-hosted pages only — the desktop app's own tabs and windows skip it.
/// The <c>"(N) "</c> pending-count prefix appears only while something is queued.
/// </summary>
public static class ClientProductTitle
{
    public static string Build(int pendingCount, string? facilityId, string product, bool includeYaatSuffix)
    {
        var title = string.IsNullOrEmpty(facilityId) ? product : $"{facilityId} - {product}";
        if (includeYaatSuffix)
        {
            title += " (YAAT)";
        }
        return pendingCount > 0 ? $"({pendingCount.ToString(CultureInfo.InvariantCulture)}) {title}" : title;
    }
}
