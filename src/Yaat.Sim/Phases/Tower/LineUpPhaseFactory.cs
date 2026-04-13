using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Identifies which concrete <see cref="ILineUpPhase"/> implementation to
/// construct. V1 is the analog 3-stage implementation
/// (<see cref="LineUpPhaseV1"/>); V2 is the closed-form plan playback
/// implementation (<see cref="LineUpPhaseV2"/>) — the clean-room redesign
/// from the Design D spec.
/// </summary>
public enum LineUpPhaseImpl
{
    /// <summary>V1 — the analog perpendicular/cross/align implementation.</summary>
    V1 = 1,

    /// <summary>V2 — the closed-form plan playback implementation (Design D).</summary>
    V2 = 2,
}

/// <summary>
/// Constructs <see cref="ILineUpPhase"/> instances (returned as
/// <see cref="Phase"/> so callers can add them directly to
/// <c>AircraftState.Phases</c>) and dispatches snapshot restoration to the
/// correct implementation based on the
/// <see cref="LineUpPhaseDto.ImplVersion"/> discriminator.
///
/// The default implementation (<see cref="DefaultImpl"/>) is process-wide.
/// Tests can temporarily override it via <see cref="Override"/>, which sets
/// an <see cref="AsyncLocal{T}"/> value that only affects the calling
/// execution context — parallel tests do not race on the override. The
/// override is restored on dispose of the returned scope.
/// </summary>
public static class LineUpPhaseFactory
{
    /// <summary>
    /// The process-wide default used by <see cref="Create"/> when no
    /// <see cref="Override"/> is in effect on the current execution context.
    /// Defaults to <see cref="LineUpPhaseImpl.V2"/> (the closed-form plan
    /// playback) as of the V2 rollout.
    /// </summary>
    public static LineUpPhaseImpl DefaultImpl { get; set; } = LineUpPhaseImpl.V2;

    /// <summary>
    /// Per-execution-context override of <see cref="DefaultImpl"/>. Null when
    /// no <see cref="Override"/> is in effect. Flows with async/await and
    /// isolates across parallel test executions (xUnit uses a new execution
    /// context per test).
    /// </summary>
    private static readonly AsyncLocal<LineUpPhaseImpl?> AmbientImpl = new();

    /// <summary>
    /// The effective implementation for <see cref="Create"/> on the current
    /// execution context: the ambient override if present, otherwise
    /// <see cref="DefaultImpl"/>.
    /// </summary>
    public static LineUpPhaseImpl CurrentImpl => AmbientImpl.Value ?? DefaultImpl;

    /// <summary>
    /// Construct a fresh lineup phase. Uses <paramref name="impl"/> if
    /// supplied, otherwise <see cref="DefaultImpl"/>. The returned instance
    /// is both an <see cref="ILineUpPhase"/> and a <see cref="Phase"/> and
    /// can be added directly to an aircraft's phase list.
    /// </summary>
    public static Phase Create(LineUpPhaseImpl? impl = null)
    {
        var choice = impl ?? CurrentImpl;
        return choice switch
        {
            LineUpPhaseImpl.V1 => new LineUpPhaseV1(),
            LineUpPhaseImpl.V2 => new LineUpPhaseV2(),
            _ => throw new InvalidOperationException($"Unknown LineUpPhaseImpl: {choice}"),
        };
    }

    /// <summary>
    /// Restore a lineup phase from a snapshot. Dispatches on
    /// <see cref="LineUpPhaseDto.ImplVersion"/> so snapshots produced by
    /// different implementations round-trip back into the correct concrete
    /// type.
    /// </summary>
    public static Phase FromSnapshot(LineUpPhaseDto dto) =>
        dto.ImplVersion switch
        {
            1 => LineUpPhaseV1.FromSnapshot(dto),
            2 => LineUpPhaseV2.FromSnapshot(dto),
            _ => throw new InvalidOperationException(
                $"Unknown LineUpPhaseDto.ImplVersion: {dto.ImplVersion}. "
                    + "A newer LineUpPhase implementation may have written this snapshot; "
                    + "update LineUpPhaseFactory.FromSnapshot to dispatch it."
            ),
        };

    /// <summary>
    /// Temporarily set the lineup-phase implementation for the current
    /// execution context. Uses <see cref="AsyncLocal{T}"/> so parallel tests
    /// do not race on the override — each test gets its own ambient value.
    /// Flows through await continuations. The returned scope restores the
    /// previous ambient value on dispose.
    /// </summary>
    public static IDisposable Override(LineUpPhaseImpl impl)
    {
        var previous = AmbientImpl.Value;
        AmbientImpl.Value = impl;
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope(LineUpPhaseImpl? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            AmbientImpl.Value = previous;
            _disposed = true;
        }
    }
}
