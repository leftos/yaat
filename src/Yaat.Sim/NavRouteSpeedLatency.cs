using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>
/// Which speed restrictions on a route the nav-route overlay shows latent, fix by fix. A published SID or STAR speed is
/// latent while an ATC speed is in force or DSR is (7110.65 5-7-2.e Ex 2, 5-7-4.d); a controller's crossing speed never
/// is. Each published speed is judged by <see cref="FlightPhysics.PublishedSpeedApplies"/>, the rule the physics flies
/// at the fix; the overlay never draws a controller-set speed latent, while the physics does not fly a controller crossing
/// speed under DSR (YAAT-1920). The walk starts from the aircraft's speed state now; past a fix carrying a speed revert
/// it carries on in the state <see cref="FlightPhysics.RestoreSpeedPastFix"/> will leave there. A revert a hold or the final gate will drop is
/// judged as if it applies.
/// </summary>
internal static class NavRouteSpeedLatency
{
    private static readonly ILogger Log = SimLog.CreateLogger("NavRouteSpeedLatency");

    /// <summary>One entry per route fix: true where the fix's speed restriction is latent, false where it binds or there is none.</summary>
    /// <param name="aircraft">The aircraft whose speed state the walk starts from.</param>
    /// <param name="route">The route to judge, in flying order.</param>
    /// <returns>The latency of each fix's speed restriction, in route order.</returns>
    internal static bool[] LatentAlongRoute(AircraftState aircraft, IReadOnlyList<NavigationTarget> route)
    {
        bool[] latent = new bool[route.Count];
        bool explicitSpeed = aircraft.Targets.HasExplicitSpeedCommand;
        bool deleted = aircraft.Procedure.SpeedRestrictionsDeleted;
        for (int i = 0; i < route.Count; i++)
        {
            NavigationTarget fix = route[i];
            // A controller's crossing speed is always shown active, DSR or not.
            latent[i] =
                (fix.SpeedRestriction is not null)
                && !FlightPhysics.IsControllerSetSpeed(fix)
                && !FlightPhysics.PublishedSpeedApplies(fix, explicitSpeed, deleted);
            (explicitSpeed, deleted) = StateAfter(fix, explicitSpeed, deleted);
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("{Callsign}: {Count} of {Total} route speeds latent", aircraft.Callsign, latent.Count(l => l), route.Count);
        }
        return latent;
    }

    /// <summary>
    /// The speed state past <paramref name="fix"/>, in <see cref="FlightPhysics.RestoreSpeedPastFix"/>'s order: SMAX's
    /// re-arm, else the Mach hold with its own explicit flag, else the RNS, RPS or DSR given with the crossing; a plain
    /// revert keeps the assignment as it was.
    /// </summary>
    private static (bool Explicit, bool Deleted) StateAfter(NavigationTarget fix, bool explicitSpeed, bool deleted)
    {
        // Arming SMAX again clears DSR, as FlightCommandHandler.ArmSpeedMax does.
        if (fix.RevertSpeedMax)
        {
            return (true, false);
        }

        if (fix.RevertMach is not null)
        {
            return (fix.RevertMachExplicit ?? true, deleted);
        }

        return fix.RevertResume switch
        {
            SpeedResumeKind.Deleted => (false, true),
            SpeedResumeKind.Published => (false, false),
            SpeedResumeKind.Normal => (false, deleted),
            _ => (explicitSpeed, deleted),
        };
    }
}
