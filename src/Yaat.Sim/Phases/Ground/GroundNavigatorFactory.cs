using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Identifies which concrete <see cref="IGroundNavigator"/> implementation
/// to construct. V1 is the Bezier-waypoint arc follower
/// (<see cref="GroundNavigatorV1"/>); V2 is the closed-form
/// <see cref="PathPrimitive"/> playback from Design B
/// (<see cref="GroundNavigatorV2"/>).
/// </summary>
public enum GroundNavigatorImpl
{
    /// <summary>V1 — the Bezier-waypoint arc follower.</summary>
    V1 = 1,

    /// <summary>V2 — the closed-form PathPrimitive playback (Design B).</summary>
    V2 = 2,
}

/// <summary>
/// Constructs <see cref="IGroundNavigator"/> instances and dispatches snapshot
/// restoration to the correct implementation based on the
/// <see cref="GroundNavigatorDto.ImplVersion"/> discriminator.
///
/// The default implementation (<see cref="DefaultImpl"/>) is process-wide.
/// Tests can temporarily override it via <see cref="Override"/>, which sets
/// an <see cref="AsyncLocal{T}"/> value that only affects the calling
/// execution context — parallel tests do not race on the override.
/// </summary>
public static class GroundNavigatorFactory
{
    /// <summary>
    /// The process-wide default used by <see cref="Create"/> when no
    /// <see cref="Override"/> is in effect on the current execution context.
    /// Defaults to <see cref="GroundNavigatorImpl.V2"/> (the closed-form
    /// <see cref="PathPrimitive"/> playback) as of the V2 rollout.
    /// </summary>
    public static GroundNavigatorImpl DefaultImpl { get; set; } = GroundNavigatorImpl.V2;

    /// <summary>
    /// Per-execution-context override of <see cref="DefaultImpl"/>. Null when
    /// no <see cref="Override"/> is in effect. Flows with async/await and
    /// isolates across parallel test executions (xUnit uses a fresh
    /// execution context per test).
    /// </summary>
    private static readonly AsyncLocal<GroundNavigatorImpl?> AmbientImpl = new();

    /// <summary>
    /// The effective implementation for <see cref="Create"/> on the current
    /// execution context: the ambient override if present, otherwise
    /// <see cref="DefaultImpl"/>.
    /// </summary>
    public static GroundNavigatorImpl CurrentImpl => AmbientImpl.Value ?? DefaultImpl;

    /// <summary>
    /// Construct a fresh navigator. Uses <paramref name="impl"/> if supplied,
    /// otherwise <see cref="DefaultImpl"/>. The caller is responsible for
    /// setting <see cref="IGroundNavigator.MaxSpeedKts"/> and calling
    /// <see cref="IGroundNavigator.SetupSegment"/> before the first
    /// <see cref="IGroundNavigator.Tick"/>.
    /// </summary>
    public static IGroundNavigator Create(GroundNavigatorImpl? impl = null)
    {
        var choice = impl ?? CurrentImpl;
        return choice switch
        {
            GroundNavigatorImpl.V1 => new GroundNavigatorV1(),
            GroundNavigatorImpl.V2 => new GroundNavigatorV2(),
            _ => throw new InvalidOperationException($"Unknown GroundNavigatorImpl: {choice}"),
        };
    }

    /// <summary>
    /// Restore a navigator from a snapshot. Dispatches on
    /// <see cref="GroundNavigatorDto.ImplVersion"/> so snapshots produced by
    /// different implementations round-trip back into the correct concrete
    /// type.
    /// </summary>
    public static IGroundNavigator FromSnapshot(GroundNavigatorDto dto) =>
        dto.ImplVersion switch
        {
            1 => GroundNavigatorV1.FromSnapshot(dto),
            2 => GroundNavigatorV2.FromSnapshot(dto),
            _ => throw new InvalidOperationException(
                $"Unknown GroundNavigatorDto.ImplVersion: {dto.ImplVersion}. "
                    + "A newer navigator implementation may have written this snapshot; "
                    + "update GroundNavigatorFactory.FromSnapshot to dispatch it."
            ),
        };

    /// <summary>
    /// Temporarily set the ground-navigator implementation for the current
    /// execution context. Uses <see cref="AsyncLocal{T}"/> so parallel tests
    /// do not race on the override — each test gets its own ambient value.
    /// Flows through await continuations. The returned scope restores the
    /// previous ambient value on dispose.
    /// </summary>
    public static IDisposable Override(GroundNavigatorImpl impl)
    {
        var previous = AmbientImpl.Value;
        AmbientImpl.Value = impl;
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope(GroundNavigatorImpl? previous) : IDisposable
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
