using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The most ground speed (kt) <see cref="Aircraft"/> lost between two <see cref="Sample"/> calls a physics sub-tick apart. The
/// sub-tick that ends at rest having started at or below <see cref="TaxiingPhase.StartNodeHoldArmSpeedKts"/> is the normal take
/// of a stop from a crawl, not a dead stop, and is left out.
/// </summary>
internal sealed class SpeedLossWatch(AircraftState aircraft)
{
    private double _previousKts = aircraft.GroundSpeed;
    private int _samples;

    public AircraftState Aircraft { get; } = aircraft;

    /// <summary>The largest loss (kt) in one sub-tick so far.</summary>
    public double WorstKts { get; private set; }

    /// <summary>Where the largest loss happened: the sample number, the phase and the speeds either side of it.</summary>
    public string WorstAt { get; private set; } = "none";

    /// <summary>Reads the aircraft's speed after a physics sub-tick and keeps the loss since the last sample when it is the largest.</summary>
    public void Sample()
    {
        _samples++;
        double nowKts = Aircraft.GroundSpeed;
        double lossKts = _previousKts - nowKts;
        bool takesTheStop = (nowKts <= 0.0) && (_previousKts <= TaxiingPhase.StartNodeHoldArmSpeedKts);
        if (!takesTheStop && (lossKts > WorstKts))
        {
            WorstKts = lossKts;
            WorstAt = $"sample {_samples} ({Aircraft.Phases?.CurrentPhase?.Name}, {_previousKts:F2} -> {nowKts:F2} kt)";
        }

        _previousKts = nowKts;
    }
}
