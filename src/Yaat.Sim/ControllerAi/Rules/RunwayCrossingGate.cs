using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Whether a combined tower may let an aircraft cross a runway right now (7110.65 §3-1-3, §3-7-2.a.7): nobody is
/// departing, landing or otherwise on the runway surface, no arrival is on short final, and no arrival is inside the
/// final gate (three miles or ninety seconds from the threshold, and low enough to be landing — an overflight at
/// altitude is not an arrival), and nobody is going around or flying a low approach over the pavement (§3-7-2.a.7.1 —
/// only a §3-10-10 altitude-restricted low approach authorizes a crossing under an aircraft, and the AI never asks for
/// one). Another aircraft merely crossing elsewhere on the pavement does not close it. A crosser under its own hold
/// directive is never sent across.
/// </summary>
public static class RunwayCrossingGate
{
    public const double FinalGateNm = 3.0;
    public const double FinalGateSeconds = 90;

    /// <summary>How far out the time gate looks for a fast arrival (a jet at 180 kt covers three miles in a minute).</summary>
    public const double TimeGateMaxNm = 10;

    /// <summary>Above this height over the field an aircraft over the final approach course is an overflight, not an arrival.</summary>
    public const double FinalGateMaxAglFt = 2500;

    /// <summary>An aircraft over the pavement below this height is using the runway (a missed approach, a low approach), whatever its vertical speed.</summary>
    public const double OverRunwayMaxAglFt = 1500;

    public static bool IsClear(
        AircraftState crosser,
        RunwayInfo pavement,
        IReadOnlyList<AircraftState> traffic,
        AirportGroundLayout? layout,
        out string reason
    )
    {
        if (crosser.Ground.Hold is not null)
        {
            reason = $"{crosser.Callsign} is under a hold";
            return false;
        }

        foreach (var other in traffic)
        {
            if (string.Equals(other.Callsign, crosser.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = RunwayOccupancy.AlignedEnd(other.TrueTrack.Degrees, pavement);
            var use = RunwayOccupancy.Classify(other, end, layout);
            if (use is { Kind: not RunwayUseKind.Crossing })
            {
                reason = $"{other.Callsign} {use.Kind} runway {end.Designator}";
                return false;
            }

            if (other.IsOnGround)
            {
                continue;
            }

            if (IsOverTheRunway(other, pavement))
            {
                reason = $"{other.Callsign} over runway {end.Designator}";
                return false;
            }

            if (other.Altitude - pavement.AirportElevationFt > FinalGateMaxAglFt)
            {
                continue;
            }

            // IsOnFinal supplies the direction (approach side, tracking the runway, not climbing); the time gate only adds
            // a fast arrival still outside the distance gate. A departure climbing away past the far end is neither.
            bool insideDistance = RunwayOccupancy.IsOnFinal(other, end, layout, FinalGateNm);
            bool insideTime =
                RunwayOccupancy.IsOnFinal(other, end, layout, TimeGateMaxNm)
                && (RunwayOccupancy.SecondsToLandingThreshold(other, end, layout) is > 0 and <= FinalGateSeconds);
            if (insideDistance || insideTime)
            {
                reason = $"{other.Callsign} inside the final gate for runway {end.Designator}";
                return false;
            }
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Airborne over the runway: a go-around or low approach flown on it (the phase says so, whatever the geometry), or any
    /// aircraft inside the pavement footprint below <see cref="OverRunwayMaxAglFt"/> — climbing traffic that the on-final test
    /// deliberately ignores.
    /// </summary>
    public static bool IsOverTheRunway(AircraftState other, RunwayInfo pavement)
    {
        bool flyingItsMissedApproach =
            (other.Phases?.CurrentPhase is GoAroundPhase or LowApproachPhase) && (other.Phases.AssignedRunway?.Id.Overlaps(pavement.Id) ?? false);
        return flyingItsMissedApproach
            || (RunwayOccupancy.IsWithinPavement(other.Position, pavement) && (other.Altitude - pavement.AirportElevationFt <= OverRunwayMaxAglFt));
    }

    /// <summary>The pavement a crossing bar's combined target ("28R/10L") names, among an airport's runways.</summary>
    public static RunwayInfo? PavementFor(string combinedTarget, IReadOnlyList<RunwayInfo> runways)
    {
        var wanted = RunwayIdentifier.Parse(combinedTarget);
        return runways.FirstOrDefault(r => r.Id.Overlaps(wanted));
    }
}
