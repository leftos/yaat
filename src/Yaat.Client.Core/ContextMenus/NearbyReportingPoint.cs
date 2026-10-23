namespace Yaat.Client.ContextMenus;

/// <summary>
/// One reporting point the Report when flyout offers near an aircraft (<see cref="IMenuHost.GetReportingPointsNearby"/>): the fix
/// <see cref="Name"/>, one <c>REPORT</c> takes, <see cref="DistanceNm"/> from the aircraft and <see cref="BearingTrueDeg"/>, the true
/// bearing from the aircraft to the fix.
/// </summary>
public sealed record NearbyReportingPoint(string Name, double DistanceNm, double BearingTrueDeg)
{
    /// <summary>The farthest a nearby reporting point is from the aircraft, in nautical miles.</summary>
    public const double ReportWhenNearbyRadiusNm = 10.0;

    /// <summary>The most nearby reporting points the flyout lists.</summary>
    public const int ReportWhenMaxNearbyPoints = 6;
}
