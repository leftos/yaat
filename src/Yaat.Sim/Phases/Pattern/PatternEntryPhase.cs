using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Navigates an airborne aircraft to a pattern entry point, descending to pattern
/// altitude and decelerating to pattern speed. Inserted before the first pattern
/// leg phase (downwind, base, etc.) when the aircraft is far from the pattern.
/// Completes when the entry point is reached (NavigationRoute drained by FlightPhysics).
/// </summary>
public sealed class PatternEntryPhase : Phase
{
    public required double EntryLat { get; init; }
    public required double EntryLon { get; init; }
    public required double PatternAltitude { get; init; }

    /// <summary>
    /// Optional lead-in waypoint placed before the entry point so the aircraft
    /// aligns with the leg heading before reaching the entry point.
    /// </summary>
    public double? LeadInLat { get; init; }
    public double? LeadInLon { get; init; }

    public override string Name => "Pattern Entry";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();

        if (LeadInLat is not null && LeadInLon is not null)
        {
            ctx.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Latitude = LeadInLat.Value,
                    Longitude = LeadInLon.Value,
                    Name = "PTN-LEADIN",
                }
            );
        }

        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Latitude = EntryLat,
                Longitude = EntryLon,
                Name = "PTN-ENTRY",
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, (int)PatternAltitude),
            }
        );
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TurnRateOverride = null;
        ctx.Targets.PreferredTurnDirection = null;

        // Set target altitude; UpdateDescentPlanning in FlightPhysics computes
        // the required descent rate from the AltitudeRestriction on PTN-ENTRY.
        ctx.Targets.TargetAltitude = PatternAltitude;
        if (ctx.Aircraft.Altitude < PatternAltitude - 100)
        {
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }

        // Decelerate toward pattern speed
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, EntryLat, EntryLon);
        ctx.Logger.LogDebug(
            "[PatternEntry] {Callsign}: navigating to entry, dist={Dist:F1}nm, alt={Alt:F0}ft, tgtAlt={TgtAlt:F0}ft",
            ctx.Aircraft.Callsign,
            dist,
            ctx.Aircraft.Altitude,
            PatternAltitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // FlightPhysics drains NavigationRoute as waypoints are reached
        return ctx.Targets.NavigationRoute.Count == 0;
    }

    public override PhaseDto ToSnapshot() =>
        new PatternEntryPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            EntryLat = EntryLat,
            EntryLon = EntryLon,
            PatternAltitude = PatternAltitude,
            LeadInLat = LeadInLat,
            LeadInLon = LeadInLon,
        };

    public static PatternEntryPhase FromSnapshot(PatternEntryPhaseDto dto)
    {
        var phase = new PatternEntryPhase
        {
            EntryLat = dto.EntryLat,
            EntryLon = dto.EntryLon,
            PatternAltitude = dto.PatternAltitude,
            LeadInLat = dto.LeadInLat,
            LeadInLon = dto.LeadInLon,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        return phase;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
