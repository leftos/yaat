namespace Yaat.Client.Services;

/// <summary>
/// Converts a stream of discrete mouse-wheel events into whole steps, scaled by a
/// sensitivity factor. At sensitivity 1.0 every event yields exactly one step (the
/// historical behavior); at 0.25 a step is emitted only every fourth event. This tames
/// discrete spinners (range, PTL, history, brightness) on a Mac trackpad, which fires a
/// burst of small-delta events per swipe. Reversing direction resets the accumulator so a
/// change of direction takes effect immediately.
/// </summary>
public sealed class ScrollStepAccumulator
{
    private double _accumulated;
    private int _lastDirection;

    /// <summary>
    /// Feeds one wheel event and returns the net whole steps to apply (0, ±1, ±2, …).
    /// </summary>
    /// <param name="direction">Sign of the wheel delta: +1, -1, or 0 for no movement.</param>
    /// <param name="sensitivity">Scroll sensitivity in (0, 1]; values outside are clamped.</param>
    public int Accumulate(int direction, double sensitivity)
    {
        if (direction == 0)
        {
            return 0;
        }

        if (direction != _lastDirection)
        {
            _accumulated = 0.0;
            _lastDirection = direction;
        }

        _accumulated += Math.Clamp(sensitivity, 0.0, 1.0);
        int magnitude = (int)Math.Floor(_accumulated);
        _accumulated -= magnitude;
        return magnitude * direction;
    }
}
