using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Per-tick taxi observer used by the E2E coverage suite. Fed one
/// <see cref="AircraftState"/> snapshot per simulated second, it accumulates
/// the metrics the suite asserts on:
///
/// <list type="bullet">
/// <item><c>CumulativePathFt</c> — sum of great-circle distance between
///   consecutive observed positions; a proxy for "ground actually covered"
///   that does not depend on internal route segmentation.</item>
/// <item><c>CumulativeAbsTurnDeg</c> — sum of |Δheading| between consecutive
///   observations. A real taxi from A to B has a finite cumulative turn
///   bounded by the route geometry; spinning blows past it by 5-10×.</item>
/// <item><c>MaxConsecutiveZeroProgressSec</c> — longest stretch (in observed
///   ticks) where ground speed stayed below <see cref="ZeroProgressIasThreshold"/>
///   while the aircraft was NOT in a phase where stopping is legitimate
///   (hold-short, runway crossing, parked).</item>
/// <item><c>LastSegmentIndex</c> / <c>LastPhaseName</c> — diagnostic snapshot
///   of the aircraft's last observed taxi state, for failure messages.</item>
/// </list>
///
/// The hold-short / crossing / parked filter mirrors the existing
/// <c>OakNorthFieldTaxiSpinTests</c> approach: a legitimate stop at a runway
/// hold-short is not a spin, it's the aircraft waiting for clearance.
/// </summary>
internal sealed class TaxiBudgetEvaluator
{
    /// <summary>
    /// Ground speed below this (knots) counts as "not moving" for the purpose
    /// of zero-progress detection. Set above zero so tiny floating-point drift
    /// during a held position doesn't accumulate ticks.
    /// </summary>
    public const double ZeroProgressIasThreshold = 1.0;

    private double? _prevHdgDeg;
    private LatLon? _prevPos;
    private int _zeroProgressRun;

    public double CumulativePathFt { get; private set; }
    public double CumulativeAbsTurnDeg { get; private set; }
    public int MaxConsecutiveZeroProgressSec { get; private set; }
    public int? LastSegmentIndex { get; private set; }
    public int? LastSegmentCount { get; private set; }
    public string? LastPhaseName { get; private set; }
    public double LastGroundSpeedKts { get; private set; }
    public LatLon? LastPosition { get; private set; }
    public double LastHeadingDeg { get; private set; }

    public void Observe(AircraftState aircraft)
    {
        var phase = aircraft.Phases?.CurrentPhase;
        bool legitimateStop = phase is HoldingShortPhase or CrossingRunwayPhase or AtParkingPhase;

        if (_prevPos is { } pp)
        {
            double dNm = GeoMath.DistanceNm(pp, aircraft.Position);
            CumulativePathFt += dNm * GeoMath.FeetPerNm;
        }

        if (_prevHdgDeg is { } ph)
        {
            double cur = aircraft.TrueHeading.Degrees;
            double delta = (((cur - ph) + 540.0) % 360.0) - 180.0;
            CumulativeAbsTurnDeg += Math.Abs(delta);
        }

        if (aircraft.GroundSpeed < ZeroProgressIasThreshold && !legitimateStop)
        {
            _zeroProgressRun++;
            if (_zeroProgressRun > MaxConsecutiveZeroProgressSec)
            {
                MaxConsecutiveZeroProgressSec = _zeroProgressRun;
            }
        }
        else
        {
            _zeroProgressRun = 0;
        }

        _prevPos = aircraft.Position;
        _prevHdgDeg = aircraft.TrueHeading.Degrees;
        LastPosition = aircraft.Position;
        LastHeadingDeg = aircraft.TrueHeading.Degrees;
        LastGroundSpeedKts = aircraft.GroundSpeed;
        LastSegmentIndex = aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex;
        LastSegmentCount = aircraft.Ground.AssignedTaxiRoute?.Segments.Count;
        LastPhaseName = phase?.Name;
    }

    public string DiagnosticSummary()
    {
        string segInfo = LastSegmentIndex is { } idx && LastSegmentCount is { } total ? $"seg {idx}/{total}" : "no-route";
        string posInfo = LastPosition is { } p ? $"({p.Lat:F6},{p.Lon:F6})" : "(none)";
        return $"phase={LastPhaseName ?? "(none)"}, {segInfo}, pos={posInfo}, hdg={LastHeadingDeg:F0}, gs={LastGroundSpeedKts:F1}, "
            + $"path={CumulativePathFt:F0}ft, turn={CumulativeAbsTurnDeg:F0}deg, maxZeroProgress={MaxConsecutiveZeroProgressSec}s";
    }
}
