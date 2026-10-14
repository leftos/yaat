using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The push plan (<see cref="PushTargetEntry"/>) of each push-back stand a jet or turboprop is parked on, made off the
/// sim tick: the server plans every such stand of a scenario while it prepares the load (<see cref="PlanParked"/>, in
/// parallel), an aircraft spawned later plans its own stand as it spawns (<see cref="PlanSpawned"/>), and the AI's
/// push rule only reads (<see cref="TryGet"/>), planning on the tick (<see cref="PlanStand"/>) only when a plan is missing.
///
/// <para>A plan is a pure function of the layout, the stand and the design group: the shipped seed's entry where it is
/// current (<see cref="PushTargetSeed.Find"/>), else <see cref="PushTargetPlanner.ComputeStand"/> with the shipped
/// envelopes and the airport sidecars. It is keyed on exactly those, the layout by identity, so another airport or a
/// re-downloaded layout never reads a stale plan, and planning it early or on the tick gives the same plan. It is a derived
/// cache: nothing of it enters a snapshot, a recording or a DTO, and a restored world plans again on demand.</para>
///
/// <para>A server room owns one store and hands it to every engine it builds, so a restart, rewind, recording load or
/// checkpoint restore on the room's pinned layouts reads the plans its load made. One stand whose classification or
/// planning throws at load or at spawn is logged and skipped, left unstored; the push rule then plans it on the tick.</para>
/// </summary>
public sealed class PushPlanStore
{
    /// <summary>Plans one stand for one design group: its plan, or null when the stand has none.</summary>
    /// <param name="layout">The layout the stand is on.</param>
    /// <param name="stand">The stand's name as the layout's node names it.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <param name="sidecars">The airport sidecars the planner reads.</param>
    public delegate PushTargetEntry? StandPlanner(
        AirportGroundLayout layout,
        string stand,
        AirplaneDesignGroup group,
        AirportSidecarCatalog sidecars
    );

    private static readonly ILogger Log = SimLog.CreateLogger("PushPlanStore");

    /// <summary>The shipped design-group envelopes every stand is planned with, read once (<see cref="DesignGroupEnvelopes.LoadShipped"/>).</summary>
    private static readonly Lazy<DesignGroupEnvelopes> ShippedEnvelopes = new(DesignGroupEnvelopes.LoadShipped);

    /// <summary>The shipped push-target seed (<c>Data/PrecomputeCache</c>), read before planning a stand.</summary>
    private static readonly Lazy<PushTargetSeed> ShippedSeed = new(() =>
        new PushTargetSeed(
            new PrecomputeStore(Path.Combine(AppContext.BaseDirectory, "Data", "PrecomputeCache")),
            Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs")
        )
    );

    private readonly ConditionalWeakTable<
        AirportGroundLayout,
        ConcurrentDictionary<(string Stand, AirplaneDesignGroup Group), PushTargetEntry?>
    > _plans = [];

    private readonly StandPlanner _planner;

    private int _hits;
    private int _misses;

    /// <summary>A store that plans each stand with <paramref name="planner"/>.</summary>
    /// <param name="planner">How a stand is planned: <see cref="PlanFromShippedData"/> outside tests.</param>
    public PushPlanStore(StandPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    /// <summary>A store that plans with the shipped seed and envelopes (<see cref="PlanFromShippedData"/>).</summary>
    public static PushPlanStore CreateShipped() => new(PlanFromShippedData);

    /// <summary>The reads (<see cref="TryGet"/>) that found the stand's plan stored.</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>The reads (<see cref="TryGet"/>) that found no plan stored, so the reader planned on the tick.</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>
    /// Plans, in parallel, the stand of every aircraft in <paramref name="aircraft"/> that is a jet or turboprop parked on a
    /// push-back stand, each stand and design group once, skipping those already stored.
    /// </summary>
    /// <param name="aircraft">The aircraft, in or not yet in the world (a scenario's loaded aircraft).</param>
    /// <param name="layoutFor">The layout each aircraft operates on, as the sim tick resolves it.</param>
    /// <param name="maxDegreeOfParallelism">How many stands are planned at once.</param>
    /// <returns>How many stands were planned and stored; a stand whose planning threw is not counted.</returns>
    public int PlanParked(IEnumerable<AircraftState> aircraft, Func<AircraftState, AirportGroundLayout?> layoutFor, int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(layoutFor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        AirportSidecarCatalog sidecars = CurrentSidecars();
        var seen = new HashSet<StandKey>();
        var stands = new List<StandKey>();
        foreach (AircraftState ac in aircraft)
        {
            if ((TryStandOf(ac, layoutFor, sidecars) is { } stand) && !Holds(stand) && seen.Add(stand))
            {
                stands.Add(stand);
            }
        }

        var clock = Stopwatch.StartNew();
        int planned = 0;
        Parallel.ForEach(
            stands,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            stand =>
            {
                if (TryPlanAndStore(stand, sidecars))
                {
                    Interlocked.Increment(ref planned);
                }
            }
        );
        Log.LogDebug("Planned the push targets of {Count} of {Total} stands in {Ms} ms", planned, stands.Count, clock.ElapsedMilliseconds);
        return planned;
    }

    /// <summary>
    /// Plans, on the calling thread, the stand of an aircraft that has just spawned when it is a jet or turboprop parked on
    /// a push-back stand and the stand's plan is not stored yet.
    /// </summary>
    /// <param name="aircraft">The aircraft that spawned.</param>
    /// <param name="layout">The layout it operates on, as the sim tick resolves it; null when its airport has none.</param>
    public void PlanSpawned(AircraftState aircraft, AirportGroundLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        AirportSidecarCatalog sidecars = CurrentSidecars();
        if ((TryStandOf(aircraft, _ => layout, sidecars) is { } stand) && !Holds(stand))
        {
            TryPlanAndStore(stand, sidecars);
        }
    }

    /// <summary>
    /// <see cref="StandOf"/> for one aircraft, isolated as planning a stand is: resolving its layout or classifying its
    /// stand that throws fails this aircraft alone, logged and left unplanned, so the push rule plans it on the tick.
    /// </summary>
    private static StandKey? TryStandOf(AircraftState aircraft, Func<AircraftState, AirportGroundLayout?> layoutFor, AirportSidecarCatalog sidecars)
    {
        AirportGroundLayout? layout = null;
        try
        {
            layout = layoutFor(aircraft);
            return StandOf(aircraft, layout, sidecars);
        }
        catch (Exception ex)
        {
            Log.LogError(
                ex,
                "Classifying stand {Stand} of {Callsign} at {Airport} ({Type}) for a push plan failed; left for the push rule to plan on the tick",
                aircraft.Ground.ParkingSpot,
                aircraft.Callsign,
                layout?.AirportId ?? aircraft.FlightPlan.Departure,
                aircraft.AircraftType
            );
            return null;
        }
    }

    /// <summary>The stored plan of <paramref name="stand"/> for <paramref name="group"/>, counted as a hit or a miss.</summary>
    /// <param name="layout">The layout the stand is on.</param>
    /// <param name="stand">The stand's name as the layout's node names it.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <param name="entry">The stored plan; null when the stand has no plan (stored as such) or none is stored.</param>
    /// <returns>Whether a plan was stored.</returns>
    public bool TryGet(AirportGroundLayout layout, string stand, AirplaneDesignGroup group, out PushTargetEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (
            _plans.TryGetValue(layout, out ConcurrentDictionary<(string, AirplaneDesignGroup), PushTargetEntry?>? plans)
            && plans.TryGetValue((stand, group), out entry)
        )
        {
            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        entry = null;
        return false;
    }

    /// <summary>Plans <paramref name="stand"/> for <paramref name="group"/> on the calling thread and stores the plan.</summary>
    /// <param name="layout">The layout the stand is on.</param>
    /// <param name="stand">The stand's name as the layout's node names it.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <returns>The plan, or null when the stand has none.</returns>
    public PushTargetEntry? PlanStand(AirportGroundLayout layout, string stand, AirplaneDesignGroup group)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var key = new StandKey(layout, stand, group);
        PushTargetEntry? entry = Plan(key, CurrentSidecars());
        Store(key, entry);
        return entry;
    }

    /// <summary>
    /// The stand an aircraft needs a plan for: a jet or turboprop (the categories that ask for a push) parked on a
    /// push-back stand of <paramref name="layout"/>; null otherwise.
    /// </summary>
    private static StandKey? StandOf(AircraftState aircraft, AirportGroundLayout? layout, AirportSidecarCatalog sidecars)
    {
        if (
            (layout is null) || (AircraftCategorization.Categorize(aircraft.AircraftType) is not (AircraftCategory.Jet or AircraftCategory.Turboprop))
        )
        {
            return null;
        }

        if ((ParkedStandDeparture.Of(aircraft, layout, sidecars) != StandDeparture.PushBack) || (aircraft.Ground.ParkingSpot is not { } spot))
        {
            return null;
        }

        return layout.FindParkingByName(spot) is { Name: { } stand }
            ? new StandKey(layout, stand, AirplaneDesignGroups.GroupForType(aircraft.AircraftType))
            : null;
    }

    private static AirportSidecarCatalog CurrentSidecars() => NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty;

    /// <summary>
    /// The shipped planner: the stand's entry in the shipped seed where it is current, else
    /// <see cref="PushTargetPlanner.ComputeStand"/> with the shipped envelopes.
    /// </summary>
    /// <param name="layout">The layout the stand is on.</param>
    /// <param name="stand">The stand's name as the layout's node names it.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <param name="sidecars">The airport sidecars the planner reads.</param>
    /// <returns>The plan, or null when the stand has none.</returns>
    public static PushTargetEntry? PlanFromShippedData(
        AirportGroundLayout layout,
        string stand,
        AirplaneDesignGroup group,
        AirportSidecarCatalog sidecars
    ) => SeededPlan(layout.AirportId, stand, group) ?? PushTargetPlanner.ComputeStand(layout, ShippedEnvelopes.Value, sidecars, stand, group);

    /// <summary>The stand's plan, made with the store's planner.</summary>
    private PushTargetEntry? Plan(StandKey key, AirportSidecarCatalog sidecars)
    {
        PushTargetEntry? entry = _planner(key.Layout, key.Stand, key.Group, sidecars);
        if (entry is null)
        {
            Log.LogDebug("Stand {Stand} at {Airport} has no push plan for design group {Group}", key.Stand, key.Layout.AirportId, key.Group);
        }

        return entry;
    }

    /// <summary>
    /// Plans and stores one stand ahead of the tick. Planning that throws fails this stand alone: it is logged and left
    /// unstored, so the push rule plans it on the tick, and never fails the load or the spawn that asked.
    /// </summary>
    /// <returns>Whether the stand's plan was stored.</returns>
    private bool TryPlanAndStore(StandKey key, AirportSidecarCatalog sidecars)
    {
        PushTargetEntry? entry;
        try
        {
            entry = Plan(key, sidecars);
        }
        catch (Exception ex)
        {
            Log.LogError(
                ex,
                "Push plan of stand {Stand} at {Airport} for design group {Group} failed; left unstored for the push rule to plan on the tick",
                key.Stand,
                key.Layout.AirportId,
                key.Group
            );
            return false;
        }

        Store(key, entry);
        return true;
    }

    /// <summary>The shipped seed's entry for the stand, or null when there is none or the shipped data folder is absent.</summary>
    private static PushTargetEntry? SeededPlan(string airportId, string stand, AirplaneDesignGroup group)
    {
        try
        {
            return ShippedSeed.Value.Find(airportId, stand, group);
        }
        catch (DirectoryNotFoundException ex)
        {
            Log.LogWarning(ex, "No shipped push-target seed for stand {Stand} at {Airport}; planning it live", stand, airportId);
            return null;
        }
    }

    private bool Holds(StandKey key) =>
        _plans.TryGetValue(key.Layout, out ConcurrentDictionary<(string, AirplaneDesignGroup), PushTargetEntry?>? plans)
        && plans.ContainsKey((key.Stand, key.Group));

    private void Store(StandKey key, PushTargetEntry? entry) => _plans.GetOrCreateValue(key.Layout)[(key.Stand, key.Group)] = entry;

    /// <summary>
    /// A stand to plan: the layout it is on (compared by identity, as <see cref="AirportGroundLayout"/> keeps reference
    /// equality), its node's name and the design group.
    /// </summary>
    private sealed record StandKey(AirportGroundLayout Layout, string Stand, AirplaneDesignGroup Group);
}
