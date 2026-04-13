using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Identifies which concrete <see cref="IGroundNavigator"/> implementation to
/// construct. V1 is the existing Bezier-waypoint arc follower
/// (<see cref="GroundNavigatorV1"/>). V2 is a clean-room redesign under
/// development; it will be added as a new enum value when the implementation
/// lands.
/// </summary>
public enum GroundNavigatorImpl
{
    /// <summary>V1 — the existing Bezier-waypoint arc follower.</summary>
    V1 = 1,
}

/// <summary>
/// Constructs <see cref="IGroundNavigator"/> instances and dispatches snapshot
/// restoration to the correct implementation based on the
/// <see cref="GroundNavigatorDto.ImplVersion"/> discriminator.
///
/// The default implementation (<see cref="DefaultImpl"/>) is process-wide
/// state. Tests can temporarily override it via <see cref="Override"/>, which
/// returns an <see cref="IDisposable"/> scope that restores the previous
/// default on dispose. Use <c>using (GroundNavigatorFactory.Override(...))</c>
/// to parameterize a single test.
/// </summary>
public static class GroundNavigatorFactory
{
    /// <summary>
    /// The implementation used by <see cref="Create"/> when no explicit
    /// override is in effect. Defaults to <see cref="GroundNavigatorImpl.V1"/>.
    /// </summary>
    public static GroundNavigatorImpl DefaultImpl { get; set; } = GroundNavigatorImpl.V1;

    /// <summary>
    /// Construct a fresh navigator. Uses <paramref name="impl"/> if supplied,
    /// otherwise <see cref="DefaultImpl"/>. The caller is responsible for
    /// setting <see cref="IGroundNavigator.MaxSpeedKts"/> and calling
    /// <see cref="IGroundNavigator.SetupSegment"/> before the first
    /// <see cref="IGroundNavigator.Tick"/>.
    /// </summary>
    public static IGroundNavigator Create(GroundNavigatorImpl? impl = null)
    {
        var choice = impl ?? DefaultImpl;
        return choice switch
        {
            GroundNavigatorImpl.V1 => new GroundNavigatorV1(),
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
            _ => throw new InvalidOperationException(
                $"Unknown GroundNavigatorDto.ImplVersion: {dto.ImplVersion}. "
                    + "A newer navigator implementation may have written this snapshot; "
                    + "update GroundNavigatorFactory.FromSnapshot to dispatch it."
            ),
        };

    /// <summary>
    /// Temporarily replace <see cref="DefaultImpl"/> for the duration of the
    /// returned scope. Intended for tests that want to exercise multiple
    /// implementations via a parameterized test harness:
    /// <code>
    /// using (GroundNavigatorFactory.Override(GroundNavigatorImpl.V1))
    /// {
    ///     // test runs against V1
    /// }
    /// </code>
    /// Not thread-safe; each test run must serialize its use of this override.
    /// </summary>
    public static IDisposable Override(GroundNavigatorImpl impl)
    {
        var previous = DefaultImpl;
        DefaultImpl = impl;
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope(GroundNavigatorImpl previous) : IDisposable
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
