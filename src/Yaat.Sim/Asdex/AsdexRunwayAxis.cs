using Yaat.Sim.Phases;

namespace Yaat.Sim.Asdex;

/// <summary>
/// A flat airport-scale frame laid along one runway: <c>along</c> is feet from end 1 toward end 2, <c>lateral</c> is feet right
/// of that line (positive to the right facing end 2). Hold-bar geometry and the bar status rule both measure in it, so a bar's
/// along-runway position and an aircraft's compare exactly, and the runway-user rule (<see cref="AsdexRunwayUsers"/>) takes the
/// runway's true bearing from it. Built once per facility runway and kept, as its inputs are the navdata ends alone.
/// </summary>
public sealed class AsdexRunwayAxis
{
    private readonly double _lat0;
    private readonly double _lon0;
    private readonly double _feetPerDegLon;
    private readonly double _ue;
    private readonly double _un;

    public AsdexRunwayAxis(RunwayInfo runway)
    {
        Runway = runway;
        _lat0 = runway.Lat1;
        _lon0 = runway.Lon1;
        _feetPerDegLon = Math.Cos(_lat0 * Math.PI / 180.0) * FeetPerDegLat;
        double e = (runway.Lon2 - _lon0) * _feetPerDegLon;
        double n = (runway.Lat2 - _lat0) * FeetPerDegLat;
        LengthFt = Math.Sqrt((e * e) + (n * n));
        _ue = e / LengthFt;
        _un = n / LengthFt;
        BearingDeg = ((Math.Atan2(_ue, _un) * 180.0 / Math.PI) + 360.0) % 360.0;
    }

    private const double FeetPerDegLat = 60.0 * GeoMath.FeetPerNm;

    /// <summary>The navdata runway the axis is laid along.</summary>
    public RunwayInfo Runway { get; }

    /// <summary>End 1 to end 2, feet.</summary>
    public double LengthFt { get; }

    /// <summary>The true bearing from end 1 to end 2, degrees.</summary>
    public double BearingDeg { get; }

    public double Along(LatLon point)
    {
        (double e, double n) = Enu(point);
        return (e * _ue) + (n * _un);
    }

    public double Lateral(LatLon point)
    {
        (double e, double n) = Enu(point);
        return (e * _un) - (n * _ue);
    }

    public LatLon ToLatLon(double along, double lateral)
    {
        double e = (along * _ue) + (lateral * _un);
        double n = (along * _un) - (lateral * _ue);
        return new LatLon(_lat0 + (n / FeetPerDegLat), _lon0 + (e / _feetPerDegLon));
    }

    private (double E, double N) Enu(LatLon point) => ((point.Lon - _lon0) * _feetPerDegLon, (point.Lat - _lat0) * FeetPerDegLat);
}
