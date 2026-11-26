namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>Whether a spinner tile holds the pointer.</summary>
public enum DcbLatchState
{
    /// <summary>The spinner takes no wheel input; a press latches it.</summary>
    Idle,

    /// <summary>The spinner holds the pointer: its wheel steps the value, a press on it accepts, Esc cancels.</summary>
    Latched,
}

/// <summary>What one transition of a <see cref="DcbSpinnerLatch"/> did.</summary>
public enum DcbLatchStep
{
    /// <summary>A press latched the spinner, remembering the value it showed.</summary>
    Latched,

    /// <summary>A wheel step changed the value, a preview until a press accepts it.</summary>
    Preview,

    /// <summary>A press accepted the value the wheel left.</summary>
    Accept,

    /// <summary>Esc cancelled the latch, restoring the value it remembered.</summary>
    Cancel,
}

/// <summary>One transition of a <see cref="DcbSpinnerLatch"/>: what happened and the value it left behind.</summary>
/// <param name="Step">The transition.</param>
/// <param name="Value">The spinner's value once the transition has run.</param>
public readonly record struct DcbLatchResult(DcbLatchStep Step, double Value);

/// <summary>
/// CRC's STARS spinner latch (DcbButton.cs): a press latches the spinner and remembers the value it showed, the wheel steps
/// the value by the spinner's step within its bounds without wrapping, a second press accepts the value the wheel left, and
/// Esc cancels, restoring the value the latch took.
/// </summary>
/// <param name="value">The value the spinner starts at, clamped within <paramref name="min"/> and <paramref name="max"/>.</param>
/// <param name="min">The lowest value the wheel reaches.</param>
/// <param name="max">The highest value the wheel reaches.</param>
/// <param name="step">What one wheel notch adds to or takes from the value.</param>
public sealed class DcbSpinnerLatch(double value, double min, double max, double step)
{
    private double _latchValue;

    /// <summary>The value the spinner shows.</summary>
    public double Value { get; private set; } = Math.Clamp(value, min, max);

    /// <summary>Whether the spinner holds the pointer.</summary>
    public DcbLatchState State { get; private set; }

    /// <summary>Presses the spinner: it latches while idle, and accepts its value while latched.</summary>
    public DcbLatchResult Press()
    {
        if (State == DcbLatchState.Latched)
        {
            State = DcbLatchState.Idle;
            return new DcbLatchResult(DcbLatchStep.Accept, Value);
        }

        _latchValue = Value;
        State = DcbLatchState.Latched;
        return new DcbLatchResult(DcbLatchStep.Latched, Value);
    }

    /// <summary>
    /// Turns the wheel once by the sign of <paramref name="delta"/> — one step up or down within the bounds, as CRC reads
    /// the wheel's direction — or does nothing and returns null while the spinner is idle.
    /// </summary>
    public DcbLatchResult? Wheel(int delta)
    {
        if ((State != DcbLatchState.Latched) || (delta == 0))
        {
            return null;
        }

        Value = Math.Clamp(Value + (Math.Sign(delta) * step), min, max);
        return new DcbLatchResult(DcbLatchStep.Preview, Value);
    }

    /// <summary>Cancels the latch and restores the value it took, or does nothing and returns null while the spinner is idle.</summary>
    public DcbLatchResult? Escape()
    {
        if (State != DcbLatchState.Latched)
        {
            return null;
        }

        Value = _latchValue;
        State = DcbLatchState.Idle;
        return new DcbLatchResult(DcbLatchStep.Cancel, Value);
    }
}
