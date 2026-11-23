using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Spawns;

/// <summary>Builds aircraft spawn times: which aircraft a filter takes, in what order, at which slots, and how their
/// shared origins are kept apart.</summary>
/// <remarks>Every member is pure: nothing here reads or writes a file, a clock or a static, and every random draw comes
/// from the <see cref="Random"/> the caller passes. A null <see cref="Random"/> is a preview with no jitter.</remarks>
public static class SpawnSchedule
{
    private static readonly ILogger Log = SimLog.CreateLogger("SpawnSchedule");

    /// <summary>The aircraft <paramref name="filter"/> takes, in the scenario's order.</summary>
    public static IReadOnlyList<ScenarioAircraft> Match(Scenario scenario, SpawnFilter filter)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(filter);
        return [.. scenario.Aircraft.Where(aircraft => Matches(aircraft, filter))];
    }

    /// <summary>The matched aircraft in assignment order: randomly by <paramref name="random"/>, else by spawn delay.</summary>
    public static IReadOnlyList<ScenarioAircraft> Order(IReadOnlyList<ScenarioAircraft> matched, bool randomOrder, Random random)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(random);
        return randomOrder ? [.. matched.OrderBy(_ => random.Next())] : [.. matched.OrderBy(aircraft => aircraft.SpawnDelay)];
    }

    /// <summary>The slots of <paramref name="bands"/>, concatenated in band order, each band's end inclusive.</summary>
    /// <param name="bands">The bands; one whose interval is zero or less is skipped.</param>
    /// <param name="random">The source of each band's jitter, or null for the preview, which jitters nothing.</param>
    public static IReadOnlyList<int> BandSlots(IReadOnlyList<SpawnBand> bands, Random? random)
    {
        ArgumentNullException.ThrowIfNull(bands);
        List<int> slots = [];
        foreach (SpawnBand band in bands)
        {
            if (band.IntervalSec > 0)
            {
                AddBandSlots(band, random, slots);
            }
        }

        return slots;
    }

    /// <summary>The slots of a walk over <paramref name="curve"/>, one interval apart at each sample's workload: an
    /// interval from the minimum up to the maximum as the workload falls, and never closer than five seconds.</summary>
    public static IReadOnlyList<int> WalkSlots(WorkloadCurve curve, WorkloadSettings settings, Random? random)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(settings);
        List<int> slots = [];
        double total = curve.LengthSec;
        if (total <= 0)
        {
            return slots;
        }

        (int min, int max) = Intervals(settings);
        int jitter = Math.Max(0, settings.JitterSec);
        double time = 0;
        while (time < total)
        {
            int index = Math.Clamp((int)(time / total * (WorkloadCurve.SampleCount - 1)), 0, WorkloadCurve.SampleCount - 1);
            double workload = curve.Samples[index] / 100.0;
            slots.Add(Math.Max(0, (int)Math.Round(time) + Draw(jitter, random)));
            time += Math.Max(5.0, max - (workload * (max - min)));
        }

        return slots;
    }

    /// <summary>Exactly <paramref name="count"/> slots spread over the length in proportion to
    /// <paramref name="curve"/>'s workload, no closer than the minimum interval, then jittered.</summary>
    public static IReadOnlyList<int> FillSlots(WorkloadCurve curve, int count, WorkloadSettings settings, Random? random)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(settings);
        List<int> slots = [];
        int total = (int)Math.Round(curve.LengthSec);
        if ((count <= 0) || (total <= 0))
        {
            return slots;
        }

        int jitter = Math.Max(0, settings.JitterSec);
        if (count == 1)
        {
            slots.Add(Math.Clamp(Draw(jitter, random), 0, total));
            return slots;
        }

        foreach (int slot in FillQuantiles(curve, count, total, Math.Min(settings.MinIntervalSec, settings.MaxIntervalSec)))
        {
            slots.Add(Math.Max(0, slot + Draw(jitter, random)));
        }

        return slots;
    }

    /// <summary>The origin two aircraft share when they spawn from the same place, or an empty string when nothing is
    /// deconflicted for it: the departure airport for a parking or on-runway start, the destination and runway for an
    /// on-final start, the fix text for a fix start, and nothing for any other kind.</summary>
    public static string OriginKey(Scenario scenario, ScenarioAircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(aircraft);
        return OriginKeyOf(scenario, aircraft);
    }

    /// <summary>Pairs <paramref name="ordered"/> with <paramref name="slots"/>, pushes same-origin spawns at least
    /// <paramref name="deconfliction"/>'s separation apart, and reports any spawn later than
    /// <paramref name="lengthSec"/>.</summary>
    /// <remarks>No spawn is ever moved earlier, and nothing is clamped to the length: an overrun is reported, with both
    /// remedies on <see cref="SpawnOverrun"/>. A same-origin aircraft the caller does not schedule keeps its delay, and
    /// a scheduled spawn moves later only until it clears every same-origin aircraft, scheduled or not.</remarks>
    /// <param name="scenario">The scenario the aircraft belong to; left unchanged.</param>
    /// <param name="ordered">The aircraft in assignment order.</param>
    /// <param name="slots">The slots to assign, one per aircraft; surplus slots go unused.</param>
    /// <param name="lengthSec">The scenario's length in seconds, which an overrun is measured against; zero or less is
    /// rejected.</param>
    /// <param name="deconfliction">The least time between two spawns of one origin, or null for none.</param>
    public static SpawnScheduleResult Assign(
        Scenario scenario,
        IReadOnlyList<ScenarioAircraft> ordered,
        IReadOnlyList<int> slots,
        int lengthSec,
        SpawnDeconfliction? deconfliction
    )
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lengthSec, 0);
        int count = Math.Min(ordered.Count, slots.Count);
        (string[] origins, int[] delays) = Place(scenario, ordered, slots, count, deconfliction);
        var assignments = new List<SpawnAssignment>(count);
        for (int i = 0; i < count; i++)
        {
            assignments.Add(new SpawnAssignment(ordered[i], delays[i], origins[i]));
        }

        SpawnOverrun? overrun = FindOverrun(assignments, scenario, lengthSec, deconfliction);
        int unused = slots.Count - count;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Assigned {Assigned} of {Matched} aircraft over {LengthSec} s; {UnusedSlots} slots unused; overrun at {LastSpawnSec}",
                count,
                ordered.Count,
                lengthSec,
                unused,
                overrun?.LastSpawnSec
            );
        }

        return new SpawnScheduleResult(assignments, [.. ordered.Skip(count)], unused, lengthSec, overrun, false);
    }

    /// <summary>Writes every assignment's delay to its aircraft; the caller wraps this in its edit.</summary>
    public static void Apply(SpawnScheduleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (SpawnAssignment assignment in result.Assignments)
        {
            assignment.Aircraft.SpawnDelay = assignment.SpawnDelaySec;
        }
    }

    /// <summary>The result with <paramref name="remedy"/> applied to its overrun, and the scenario length the remedy
    /// calls for: the result's own length when nothing changes, the last spawn's when the scenario is extended.</summary>
    /// <param name="result">The result to remedy; left unchanged.</param>
    /// <param name="remedy">What to do: extend the scenario, compress the intervals, or neither.</param>
    /// <param name="newLengthSec">The scenario length the caller should use.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="remedy"/> is not one of
    /// <see cref="SpawnOverrunRemedy"/>'s values.</exception>
    public static SpawnScheduleResult ApplyRemedy(SpawnScheduleResult result, SpawnOverrunRemedy remedy, out int newLengthSec)
    {
        ArgumentNullException.ThrowIfNull(result);
        ThrowIfUnknownRemedy(remedy);
        if (result.Overrun is not SpawnOverrun overrun)
        {
            newLengthSec = result.LengthSec;
            return result;
        }

        switch (remedy)
        {
            case SpawnOverrunRemedy.None:
                newLengthSec = result.LengthSec;
                return result;
            case SpawnOverrunRemedy.ExtendScenario:
                newLengthSec = overrun.ExtendedLengthSec;
                return result with { LengthSec = overrun.ExtendedLengthSec, Overrun = null };
            case SpawnOverrunRemedy.CompressIntervals:
                newLengthSec = result.LengthSec;
                return result with
                {
                    Assignments = Compressed(result.Assignments, overrun.CompressedSpawnSec),
                    Overrun = null,
                    SeparationWarning = (result.SeparationWarning) || (overrun.CompressBreaksSeparation),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(remedy), remedy, "Unknown overrun remedy.");
        }
    }

    private static void ThrowIfUnknownRemedy(SpawnOverrunRemedy remedy)
    {
        if (remedy is not (SpawnOverrunRemedy.None or SpawnOverrunRemedy.ExtendScenario or SpawnOverrunRemedy.CompressIntervals))
        {
            throw new ArgumentOutOfRangeException(nameof(remedy), remedy, "Unknown overrun remedy.");
        }
    }

    private static bool Matches(ScenarioAircraft aircraft, SpawnFilter filter) =>
        (MatchesFields(aircraft, filter)) && (MatchesKind(aircraft, filter.Kind)) && (!IsExcluded(aircraft, filter.ExcludeCallsigns));

    private static bool MatchesFields(ScenarioAircraft aircraft, SpawnFilter filter) =>
        (MatchesPlan(aircraft, filter))
        && (MatchesText(filter.Approach, aircraft.ExpectedApproach))
        && (!Excludes(filter.ExcludeDeparture, aircraft.FlightPlan?.Departure))
        && (!Excludes(filter.ExcludeDestination, aircraft.FlightPlan?.Destination));

    private static bool MatchesPlan(ScenarioAircraft aircraft, SpawnFilter filter) =>
        (MatchesText(filter.FlightRules, aircraft.FlightPlan?.Rules))
        && (MatchesText(filter.Departure, aircraft.FlightPlan?.Departure))
        && (MatchesText(filter.Destination, aircraft.FlightPlan?.Destination));

    private static bool MatchesText(string? filter, string? value) =>
        (string.IsNullOrEmpty(filter)) || (string.Equals(filter, value, StringComparison.OrdinalIgnoreCase));

    private static bool Excludes(string? filter, string? value) =>
        (!string.IsNullOrEmpty(filter)) && (string.Equals(filter, value, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesKind(ScenarioAircraft aircraft, SpawnKind? kind)
    {
        if (kind is not SpawnKind value)
        {
            return true;
        }

        return value switch
        {
            SpawnKind.Departure => IsGroundStart(aircraft),
            SpawnKind.Arrival => HasArrivalStart(aircraft),
            _ => (!IsGroundStart(aircraft)) && (!HasArrivalStart(aircraft)),
        };
    }

    private static bool IsExcluded(ScenarioAircraft aircraft, IReadOnlySet<string>? callsigns) =>
        (callsigns is not null) && (callsigns.Any(callsign => string.Equals(callsign, aircraft.AircraftId, StringComparison.OrdinalIgnoreCase)));

    private static bool IsGroundStart(ScenarioAircraft aircraft) =>
        (aircraft.StartingConditions.Type == StartingConditionKinds.Parking) || (aircraft.StartingConditions.Type == StartingConditionKinds.OnRunway);

    private static bool HasArrivalStart(ScenarioAircraft aircraft) =>
        (!string.IsNullOrWhiteSpace(aircraft.ExpectedApproach)) || (aircraft.StartingConditions.Type == StartingConditionKinds.OnFinal);

    private static void AddBandSlots(SpawnBand band, Random? random, List<int> slots)
    {
        int plus = Math.Max(0, band.PlusSec);
        int minus = Math.Max(0, band.MinusSec);
        int theoretical = band.StartSec;
        int running = band.StartSec;
        while (theoretical <= band.EndSec)
        {
            int offset = ((random is not null) && ((plus > 0) || (minus > 0))) ? random.Next(-minus, plus + 1) : 0;
            int slot = Math.Max(0, running + offset);
            slots.Add(slot);
            theoretical += band.IntervalSec;
            running = (random is not null) ? slot + band.IntervalSec : theoretical;
        }
    }

    private static int[] FillQuantiles(WorkloadCurve curve, int count, int total, int minInterval)
    {
        const double Epsilon = 2.0;
        double[] density = [.. curve.Samples.Select(workload => workload + Epsilon)];
        double[] cumulative = new double[density.Length];
        cumulative[0] = density[0];
        for (int i = 1; i < density.Length; i++)
        {
            cumulative[i] = cumulative[i - 1] + density[i];
        }

        double weight = cumulative[^1];
        int[] raw = new int[count];
        for (int i = 0; i < count; i++)
        {
            int sample = FirstSampleAt(cumulative, weight * i / (count - 1));
            raw[i] = (int)Math.Round(sample / (double)(density.Length - 1) * total);
        }

        for (int i = 1; i < count; i++)
        {
            raw[i] = Math.Max(raw[i], raw[i - 1] + minInterval);
        }

        return raw;
    }

    private static int FirstSampleAt(double[] cumulative, double target)
    {
        int low = 0;
        int high = cumulative.Length - 1;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (cumulative[middle] < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static (int Min, int Max) Intervals(WorkloadSettings settings) =>
        (Math.Min(settings.MinIntervalSec, settings.MaxIntervalSec), Math.Max(settings.MinIntervalSec, settings.MaxIntervalSec));

    private static int Draw(int jitter, Random? random) => ((random is not null) && (jitter > 0)) ? random.Next(-jitter, jitter + 1) : 0;

    private static string OriginKeyOf(Scenario scenario, ScenarioAircraft aircraft)
    {
        string kind = aircraft.StartingConditions.Type;
        if ((kind == StartingConditionKinds.Parking) || (kind == StartingConditionKinds.OnRunway))
        {
            return AirportKey(scenario, aircraft, aircraft.FlightPlan?.Departure).ToUpperInvariant();
        }

        if (kind == StartingConditionKinds.OnFinal)
        {
            return FinalKey(scenario, aircraft);
        }

        if (kind == StartingConditionKinds.FixOrFrd)
        {
            return (aircraft.StartingConditions.Fix ?? "").ToUpperInvariant();
        }

        return "";
    }

    private static string FinalKey(Scenario scenario, ScenarioAircraft aircraft)
    {
        string destination = AirportKey(scenario, aircraft, aircraft.FlightPlan?.Destination);
        string runway = aircraft.StartingConditions.Runway ?? "";
        if ((destination.Length == 0) || (runway.Length == 0))
        {
            return "";
        }

        return $"{destination}/{runway}".ToUpperInvariant();
    }

    private static string AirportKey(Scenario scenario, ScenarioAircraft aircraft, string? planned)
    {
        if (!string.IsNullOrEmpty(planned))
        {
            return planned;
        }

        if (!string.IsNullOrEmpty(aircraft.AirportId))
        {
            return aircraft.AirportId;
        }

        return scenario.PrimaryAirportId ?? "";
    }

    private static (string[] Origins, int[] Delays) Place(
        Scenario scenario,
        IReadOnlyList<ScenarioAircraft> ordered,
        IReadOnlyList<int> slots,
        int count,
        SpawnDeconfliction? deconfliction
    )
    {
        string[] origins = new string[count];
        int[] delays = new int[count];
        for (int i = 0; i < count; i++)
        {
            origins[i] = OriginKeyOf(scenario, ordered[i]);
            delays[i] = slots[i];
        }

        int separation = deconfliction?.MinSeparationSec ?? 0;
        if (separation > 0)
        {
            PushApart(scenario, [.. ordered.Take(count)], origins, separation, delays);
        }

        return (origins, delays);
    }

    private static void PushApart(Scenario scenario, HashSet<ScenarioAircraft> scheduled, string[] origins, int separation, int[] delays)
    {
        Dictionary<string, List<int>> obstacles = UnscheduledDelays(scenario, scheduled);
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < origins.Length; i++)
        {
            if (origins[i].Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(origins[i], out List<int>? indices))
            {
                indices = [];
                groups[origins[i]] = indices;
            }

            indices.Add(i);
        }

        foreach ((string key, List<int> indices) in groups)
        {
            obstacles.TryGetValue(key, out List<int>? clearing);
            PushGroupApart(indices, separation, clearing, delays);
        }
    }

    private static Dictionary<string, List<int>> UnscheduledDelays(Scenario scenario, HashSet<ScenarioAircraft> scheduled)
    {
        var obstacles = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (ScenarioAircraft aircraft in scenario.Aircraft)
        {
            if (scheduled.Contains(aircraft))
            {
                continue;
            }

            string key = OriginKeyOf(scenario, aircraft);
            if (key.Length == 0)
            {
                continue;
            }

            if (!obstacles.TryGetValue(key, out List<int>? delays))
            {
                delays = [];
                obstacles[key] = delays;
            }

            delays.Add(aircraft.SpawnDelay);
        }

        foreach (List<int> delays in obstacles.Values)
        {
            delays.Sort();
        }

        return obstacles;
    }

    private static void PushGroupApart(List<int> indices, int separation, List<int>? obstacles, int[] delays)
    {
        List<int> sorted = [.. indices.OrderBy(index => delays[index])];
        for (int j = 0; j < sorted.Count; j++)
        {
            int current = sorted[j];
            int candidate = (j == 0) ? delays[current] : Math.Max(delays[current], delays[sorted[j - 1]] + separation);
            delays[current] = ClearObstacles(candidate, separation, obstacles);
        }
    }

    private static int ClearObstacles(int candidate, int separation, List<int>? obstacles)
    {
        if (obstacles is null)
        {
            return candidate;
        }

        int cleared = candidate;
        foreach (int obstacle in obstacles)
        {
            if ((obstacle > cleared - separation) && (obstacle < cleared + separation))
            {
                cleared = obstacle + separation;
            }
        }

        return cleared;
    }

    private static SpawnOverrun? FindOverrun(List<SpawnAssignment> assignments, Scenario scenario, int lengthSec, SpawnDeconfliction? deconfliction)
    {
        int last = 0;
        foreach (SpawnAssignment assignment in assignments)
        {
            last = Math.Max(last, assignment.SpawnDelaySec);
        }

        if (last <= lengthSec)
        {
            return null;
        }

        int separation = deconfliction?.MinSeparationSec ?? 0;
        double factor = lengthSec / (double)last;
        int[] compressed = [.. assignments.Select(assignment => (int)Math.Round(assignment.SpawnDelaySec * factor))];
        int gap = (separation > 0) ? SmallestGap(assignments, compressed, scenario, separation) : 0;
        return new SpawnOverrun(lengthSec, last, last - lengthSec, last, factor, compressed, gap, gap < separation);
    }

    private static int SmallestGap(List<SpawnAssignment> assignments, int[] compressed, Scenario scenario, int separation)
    {
        Dictionary<string, List<(int Delay, bool Scheduled)>> groups = DelaysByOrigin(assignments, compressed, scenario);
        int smallest = int.MaxValue;
        foreach (List<(int Delay, bool Scheduled)> delays in groups.Values)
        {
            delays.Sort();
            for (int i = 1; i < delays.Count; i++)
            {
                if ((delays[i].Scheduled) || (delays[i - 1].Scheduled))
                {
                    smallest = Math.Min(smallest, delays[i].Delay - delays[i - 1].Delay);
                }
            }
        }

        return (smallest == int.MaxValue) ? separation : smallest;
    }

    private static Dictionary<string, List<(int Delay, bool Scheduled)>> DelaysByOrigin(
        List<SpawnAssignment> assignments,
        int[] compressed,
        Scenario scenario
    )
    {
        HashSet<ScenarioAircraft> scheduled = [.. assignments.Select(assignment => assignment.Aircraft)];
        Dictionary<string, List<int>> obstacles = UnscheduledDelays(scenario, scheduled);
        var groups = new Dictionary<string, List<(int Delay, bool Scheduled)>>(StringComparer.Ordinal);
        for (int i = 0; i < assignments.Count; i++)
        {
            string key = assignments[i].OriginKey;
            if (key.Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out List<(int Delay, bool Scheduled)>? delays))
            {
                delays = (obstacles.TryGetValue(key, out List<int>? clearing)) ? [.. clearing.Select(delay => (delay, false))] : [];
                groups[key] = delays;
            }

            delays.Add((compressed[i], true));
        }

        return groups;
    }

    private static List<SpawnAssignment> Compressed(IReadOnlyList<SpawnAssignment> assignments, IReadOnlyList<int> slots)
    {
        var compressed = new List<SpawnAssignment>(assignments.Count);
        for (int i = 0; i < assignments.Count; i++)
        {
            compressed.Add(assignments[i] with { SpawnDelaySec = slots[i] });
        }

        return compressed;
    }
}
