using System.Globalization;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>
/// The SSA's two lines, in CRC's format: the UTC time, the primary airport's altimeter and the airport, then the tracking
/// status, <c>FUSED</c> and the range.
/// </summary>
/// <param name="Line1">The time, then the altimeter and the airport when there is a primary airport.</param>
/// <param name="Status">The tracking status: <c>OK/OK/NA</c>, or <c>NA/NA/NA</c> when not signed in.</param>
/// <param name="IsStatusAlert">Whether the status draws in the alert colour (<c>NA/NA/NA</c>).</param>
/// <param name="Line2Tail">Line 2 after the status: <c>FUSED</c> and the range.</param>
public sealed record SsaLines(string Line1, string Status, bool IsStatusAlert, string Line2Tail)
{
    /// <summary>The status shown while in a room with an active position.</summary>
    public const string SignedInStatus = "OK/OK/NA";

    /// <summary>The status shown otherwise.</summary>
    public const string SignedOutStatus = "NA/NA/NA";

    /// <summary>The whole of line 2.</summary>
    public string Line2 => $"{Status} {Line2Tail}";

    /// <summary>Builds the lines from their inputs.</summary>
    /// <param name="utcNow">The UTC time, shown as <c>HHmm/ss</c>.</param>
    /// <param name="altimeterInHg">The primary airport's altimeter, or null without weather (<c>--.--</c>).</param>
    /// <param name="airportId">The primary airport id, or null: line 1 is then the time alone.</param>
    /// <param name="isSignedIn">Whether the client is in a room with an active position.</param>
    /// <param name="rangeNm">The scope's range, shown as the toolbar's RANGE readout shows it (<see cref="ScopeToolbarButtons.RangeLabel"/>).</param>
    public static SsaLines Build(DateTime utcNow, double? altimeterInHg, string? airportId, bool isSignedIn, double rangeNm)
    {
        string time = utcNow.ToString("HHmm'/'ss", CultureInfo.InvariantCulture);
        string line1 = string.IsNullOrEmpty(airportId)
            ? time
            : $"{time} {(altimeterInHg is { } alt ? alt.ToString("F2", CultureInfo.InvariantCulture) : "--.--")} {airportId}";
        string range = ScopeToolbarButtons.RangeLabel(rangeNm);
        return new SsaLines(line1, isSignedIn ? SignedInStatus : SignedOutStatus, !isSignedIn, $"FUSED {range}NM");
    }
}
