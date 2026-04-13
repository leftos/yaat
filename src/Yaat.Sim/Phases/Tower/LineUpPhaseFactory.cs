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
/// The default implementation (<see cref="DefaultImpl"/>) is process-wide
/// state. Tests can temporarily override it via <see cref="Override"/>, which
/// returns an <see cref="IDisposable"/> scope that restores the previous
/// default on dispose.
/// </summary>
public static class LineUpPhaseFactory
{
    /// <summary>
    /// The implementation used by <see cref="Create"/> when no explicit
    /// override is in effect. Defaults to <see cref="LineUpPhaseImpl.V1"/>.
    /// </summary>
    public static LineUpPhaseImpl DefaultImpl { get; set; } = LineUpPhaseImpl.V1;

    /// <summary>
    /// Construct a fresh lineup phase. Uses <paramref name="impl"/> if
    /// supplied, otherwise <see cref="DefaultImpl"/>. The returned instance
    /// is both an <see cref="ILineUpPhase"/> and a <see cref="Phase"/> and
    /// can be added directly to an aircraft's phase list.
    /// </summary>
    public static Phase Create(LineUpPhaseImpl? impl = null)
    {
        var choice = impl ?? DefaultImpl;
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
    /// Temporarily replace <see cref="DefaultImpl"/> for the duration of the
    /// returned scope. Intended for tests that want to exercise multiple
    /// implementations via a parameterized test harness. Not thread-safe;
    /// each test run must serialize its use of this override.
    /// </summary>
    public static IDisposable Override(LineUpPhaseImpl impl)
    {
        var previous = DefaultImpl;
        DefaultImpl = impl;
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope(LineUpPhaseImpl previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            DefaultImpl = previous;
            _disposed = true;
        }
    }
}
