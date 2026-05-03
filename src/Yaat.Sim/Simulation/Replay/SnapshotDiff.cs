using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Pure-function comparator between an engine's live aircraft state and a
/// captured snapshot's aircraft DTOs. Used by the replay verification path
/// to surface drift between the replayed state and the recorded one.
///
/// Tolerances are loose enough that float wobble (RNG, integration order)
/// doesn't cause false positives, but tight enough to catch real drift like
/// a missed nav-route update or a 7° heading offset.
/// </summary>
public static class SnapshotDiff
{
    public const double PositionThresholdNm = 0.5;
    public const double HeadingThresholdDeg = 5;
    public const double AltitudeThresholdFt = 100;
    public const double IasThresholdKt = 10;

    public static SnapshotDriftReport Compare(double elapsedSeconds, StateSnapshotDto expected, IReadOnlyList<AircraftState> actual)
    {
        var actualByCallsign = actual.ToDictionary(a => a.Callsign, StringComparer.OrdinalIgnoreCase);
        var drifts = new List<AircraftDrift>();

        foreach (var snap in expected.Aircraft)
        {
            if (!actualByCallsign.TryGetValue(snap.Callsign, out var live))
            {
                drifts.Add(new AircraftDrift(snap.Callsign, [new FieldDrift("Existence", "present", "missing", null)]));
                continue;
            }

            var fieldDrifts = CompareAircraft(snap, live);
            if (fieldDrifts.Count > 0)
            {
                drifts.Add(new AircraftDrift(snap.Callsign, fieldDrifts));
            }
        }

        var snapshotCallsigns = expected.Aircraft.Select(a => a.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var live in actual)
        {
            if (!snapshotCallsigns.Contains(live.Callsign))
            {
                drifts.Add(new AircraftDrift(live.Callsign, [new FieldDrift("Existence", "missing", "present", null)]));
            }
        }

        return new SnapshotDriftReport(elapsedSeconds, drifts);
    }

    private static List<FieldDrift> CompareAircraft(AircraftSnapshotDto expected, AircraftState actual)
    {
        var drifts = new List<FieldDrift>();

        var posDistNm = GeoMath.DistanceNm(expected.Position, actual.Position);
        if (posDistNm > PositionThresholdNm)
        {
            drifts.Add(
                new FieldDrift(
                    "Position",
                    $"({expected.Position.Lat:F4},{expected.Position.Lon:F4})",
                    $"({actual.Position.Lat:F4},{actual.Position.Lon:F4})",
                    $"{posDistNm:F2} nm"
                )
            );
        }

        var headingDelta = Math.Abs(NormalizeHeadingDelta(actual.TrueHeading.Degrees - expected.TrueHeadingDeg));
        if (headingDelta > HeadingThresholdDeg)
        {
            drifts.Add(new FieldDrift("TrueHeading", $"{expected.TrueHeadingDeg:F1}°", $"{actual.TrueHeading.Degrees:F1}°", $"Δ {headingDelta:F1}°"));
        }

        var altDelta = Math.Abs(actual.Altitude - expected.Altitude);
        if (altDelta > AltitudeThresholdFt)
        {
            drifts.Add(new FieldDrift("Altitude", $"{expected.Altitude:F0} ft", $"{actual.Altitude:F0} ft", $"Δ {altDelta:F0} ft"));
        }

        var iasDelta = Math.Abs(actual.IndicatedAirspeed - expected.IndicatedAirspeed);
        if (iasDelta > IasThresholdKt)
        {
            drifts.Add(
                new FieldDrift("IndicatedAirspeed", $"{expected.IndicatedAirspeed:F0} kt", $"{actual.IndicatedAirspeed:F0} kt", $"Δ {iasDelta:F0} kt")
            );
        }

        var expectedNav = expected.Targets.NavigationRoute?.Select(n => n.Name).ToList() ?? [];
        var actualNav = actual.Targets.NavigationRoute.Select(n => n.Name).ToList();
        if (!expectedNav.SequenceEqual(actualNav, StringComparer.OrdinalIgnoreCase))
        {
            drifts.Add(new FieldDrift("NavigationRoute", $"[{string.Join(",", expectedNav)}]", $"[{string.Join(",", actualNav)}]", null));
        }

        if (!NullableDoubleEqual(expected.Targets.AssignedAltitude, actual.Targets.AssignedAltitude))
        {
            drifts.Add(
                new FieldDrift("AssignedAltitude", FmtNullable(expected.Targets.AssignedAltitude), FmtNullable(actual.Targets.AssignedAltitude), null)
            );
        }

        if (!NullableDoubleEqual(expected.Targets.AssignedMagneticHeadingDeg, actual.Targets.AssignedMagneticHeading?.Degrees))
        {
            drifts.Add(
                new FieldDrift(
                    "AssignedHeading",
                    FmtNullable(expected.Targets.AssignedMagneticHeadingDeg),
                    FmtNullable(actual.Targets.AssignedMagneticHeading?.Degrees),
                    null
                )
            );
        }

        if (!NullableDoubleEqual(expected.Targets.AssignedSpeed, actual.Targets.AssignedSpeed))
        {
            drifts.Add(new FieldDrift("AssignedSpeed", FmtNullable(expected.Targets.AssignedSpeed), FmtNullable(actual.Targets.AssignedSpeed), null));
        }

        var expectedPhase = ExtractPhaseName(expected.Phases);
        var actualPhase = actual.Phases?.CurrentPhase?.GetType().Name;
        if (!string.Equals(expectedPhase, actualPhase, StringComparison.Ordinal))
        {
            drifts.Add(new FieldDrift("CurrentPhase", expectedPhase ?? "(none)", actualPhase ?? "(none)", null));
        }

        var expectedOwner = expected.Track.Owner is { } eo ? $"{eo.Subset}{eo.SectorId}" : null;
        var actualOwner = actual.Track.Owner is { } ao ? $"{ao.Subset}{ao.SectorId}" : null;
        if (!string.Equals(expectedOwner, actualOwner, StringComparison.Ordinal))
        {
            drifts.Add(new FieldDrift("Track.Owner", expectedOwner ?? "(none)", actualOwner ?? "(none)", null));
        }

        var expectedPeer = expected.Track.HandoffPeer is { } ep ? $"{ep.Subset}{ep.SectorId}" : null;
        var actualPeer = actual.Track.HandoffPeer is { } ap ? $"{ap.Subset}{ap.SectorId}" : null;
        if (!string.Equals(expectedPeer, actualPeer, StringComparison.Ordinal))
        {
            drifts.Add(new FieldDrift("Track.HandoffPeer", expectedPeer ?? "(none)", actualPeer ?? "(none)", null));
        }

        return drifts;
    }

    private static string? ExtractPhaseName(PhaseListDto? phases)
    {
        if (phases is null || phases.Phases.Count == 0)
        {
            return null;
        }

        if (phases.CurrentIndex < 0 || phases.CurrentIndex >= phases.Phases.Count)
        {
            return null;
        }

        // Snapshot phase types have the "Dto" suffix; strip it so comparison against
        // the live phase type name lines up (e.g. "FollowingTaxiRoutePhaseDto" → "FollowingTaxiRoutePhase").
        var dtoName = phases.Phases[phases.CurrentIndex].GetType().Name;
        return dtoName.EndsWith("Dto", StringComparison.Ordinal) ? dtoName[..^3] : dtoName;
    }

    private static bool NullableDoubleEqual(double? a, double? b, double tolerance = 0.5)
    {
        if (a is null && b is null)
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return Math.Abs(a.Value - b.Value) <= tolerance;
    }

    private static string FmtNullable(double? value) => value is null ? "(none)" : value.Value.ToString("F1");

    private static double NormalizeHeadingDelta(double degrees)
    {
        double d = degrees % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        else if (d < -180.0)
        {
            d += 360.0;
        }
        return d;
    }
}
