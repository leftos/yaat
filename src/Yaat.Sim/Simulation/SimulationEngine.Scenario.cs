using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Scenario load and the ground-layout resolution it depends on.
public sealed partial class SimulationEngine
{
    // Reads the layouts that must already be loaded, so a read repeated every tick reports a missing one once.
    private ReadyLayoutReader? _readyLayouts;

    /// <summary>
    /// Loads a scenario into a fresh world. <paramref name="sessionStartUtc"/> is the instant t=0 of the session is
    /// anchored to (<see cref="SimScenarioState.SessionStartUtc"/>): the room clock for a new session, the recorded
    /// instant when replaying.
    /// </summary>
    public List<string> LoadScenario(string json, int rngSeed, DateTime sessionStartUtc) =>
        LoadScenario(json, rngSeed, sessionStartUtc, static _ => { });

    /// <summary>
    /// <see cref="LoadScenario(string, int, DateTime)"/>, with <paramref name="configureBeforePresets"/> run on the new
    /// scenario state before the immediate aircraft's presets dispatch: a replay restores there what the live room had in
    /// place when its presets dispatched (the ARTCC config and the towered airports).
    /// </summary>
    public List<string> LoadScenario(string json, int rngSeed, DateTime sessionStartUtc, Action<SimScenarioState> configureBeforePresets)
    {
        Scenario?.ReplaceWindGridPins(null);
        World.Clear();
        // A reload starts the frequency quiet and free of gates: a reload-and-replay from zero must not inherit airtime
        // recorded later in the run the load replaces, nor lines it still had queued.
        World.ActiveFrequency.Clear();
        _loggedExitCapacityRefusals.Clear();
        World.Rng = new SerializableRandom(rngSeed);
        World.ReactionDelayRng = new SerializableRandom(rngSeed);
        World.ReleaseJitterRng = new SerializableRandom(rngSeed);
        ApproachEvaluator.Reset();
        SoloTrainingEvaluator.Reset();
        BeaconCodePool.Clear();

        ScenarioLoadResult result = ScenarioLoader.Load(json, _groundData, World.Rng, sessionStartUtc.Date);

        // No ARTCC config reaches Yaat.Sim, so the pool has no banks here and falls back to sequential
        // codes. The server configures banks from the facility before running its own assignment pass.
        ScenarioLoader.AssignSpawnBeacons(BeaconCodePool, result.AllAircraftStates);

        Scenario = new SimScenarioState
        {
            ScenarioId = ScenarioIdentity.ResolveScenarioId(result.Id, json),
            ScenarioName = result.Name,
            RngSeed = rngSeed,
            SessionStartUtc = sessionStartUtc,
            OriginalScenarioJson = json,
            PrimaryAirportId = result.PrimaryAirportId,
            ArtccId = result.ArtccId,
            IsLiveSession = result.IsLiveSession,
            InitialContactTransfers = NavigationDatabase.Instance.InitialContactTransfers,
            WakeDirectives = NavigationDatabase.Instance.WakeDirectives,
            HasSoloParkingInitialCallupSource = result.HasParkingSpawns,
            HasSoloArrivalGeneratorSource = result.HasArrivalGenerators,
            InitialStripBayByCallsign = result.InitialStripBayByCallsign,
        };
        configureBeforePresets(Scenario);

        // Derived, not recorded: every ASDE-X facility starts on the configuration its ARTCC config, navdata and the
        // active runways set above give it, which the first snapshot then carries. No ARTCC config (a bare load) seeds nothing.
        Scenario.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Build(
            Scenario.ArtccConfig,
            NavigationDatabase.InstanceOrNull,
            Scenario.ActiveRunways
        );

        // Add immediate aircraft and dispatch their presets
        foreach (LoadedAircraft loaded in result.ImmediateAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            loaded.State.SpawnedAtSeconds = Scenario.ElapsedSeconds;
            World.AddAircraft(loaded.State);
            DispatchPresetCommands(loaded);
        }

        // Queue delayed aircraft
        foreach (LoadedAircraft loaded in result.DelayedAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            Scenario.DelayedQueue.Add(
                new DelayedSpawn
                {
                    Aircraft = loaded,
                    SpawnAtSeconds = loaded.SpawnDelaySeconds,
                    HeldForRelease = DepartureSpawnClassifier.IsHeldSpawnCandidate(loaded),
                }
            );
        }

        // Queue triggers
        foreach (InitializationTrigger trigger in result.Triggers)
        {
            Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = trigger.Command, FireAtSeconds = trigger.TimeOffset });
        }

        // Initialize generators
        _generatorSpawnLog.Clear();
        foreach (ScenarioGeneratorConfig genConfig in result.Generators)
        {
            string runwayId = genConfig.Runway ?? "";
            RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(result.PrimaryAirportId ?? "", runwayId);
            if (runway is null)
            {
                result.Warnings.Add($"Generator '{genConfig.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found");
                continue;
            }

            // The first generator fires on its authored schedule. Each subsequent generator with
            // randomized intervals gets a random initial phase within its first interval, so multiple
            // generators that share a startTimeOffset don't all spawn on the same first tick. Keyed off
            // the count of already-added generators, so a generator skipped for a missing runway above
            // doesn't consume the "first" slot.
            double firstSpawnSeconds = (double)genConfig.StartTimeOffset;
            if (Scenario.Generators.Count > 0 && genConfig.RandomizeInterval)
            {
                firstSpawnSeconds += World.Rng.NextDouble() * genConfig.IntervalTime;
            }

            Scenario.Generators.Add(
                new GeneratorState
                {
                    Config = genConfig,
                    Runway = runway,
                    NextSpawnSeconds = firstSpawnSeconds,
                }
            );
        }

        foreach (VfrArrivalGeneratorConfig cfg in result.VfrArrivalGenerators)
        {
            Scenario.VfrArrivalGenerators.Add(new VfrArrivalGeneratorState { Config = cfg, NextSpawnSeconds = StaggeredFirstSpawn(cfg) });
        }

        foreach (OverflightGeneratorConfig cfg in result.OverflightGenerators)
        {
            Scenario.OverflightGenerators.Add(new OverflightGeneratorState { Config = cfg, NextSpawnSeconds = StaggeredFirstSpawn(cfg) });
        }

        // Set ground layout
        if (Scenario.PrimaryAirportId is not null)
        {
            World.GroundLayout = ReadReadyLayout(Scenario.PrimaryAirportId, "scenario load");
        }

        Scenario.ScenarioAutoDeleteMode = result.AutoDeleteMode;
        Scenario.HasOngoingTrafficSource = result.HasOngoingTrafficSource;

        return result.Warnings;
    }

    // --- Three-phase tick API ---

    /// <summary>
    /// The ground layout for <paramref name="airportId"/>'s own field, or null when the engine's ground data has no
    /// map for it. <see cref="SimulationWorld.GroundLayout"/> — the scenario's primary airport, resolved through the
    /// same ground data — answers for that airport without a second lookup.
    ///
    /// <para>This is the datum a measurement <em>about a runway</em> resolves through: a landing threshold
    /// (<see cref="LandingThreshold"/>) and a distance to it (<see cref="RunwayOccupancy"/>) belong to the runway's
    /// field, not to whichever layout the aircraft happens to be carrying. An airborne arrival's
    /// <see cref="AircraftGroundOps.Layout"/> is null for a scripted arrival with no destination and for every
    /// live-traffic shadow, so reading a displacement off it measures one aircraft in a stream against the pavement
    /// end while the aircraft beside it measures against the real landing threshold — hundreds of feet apart on a
    /// displaced runway, and enough to put one of them inside the §5-7-1.b.4 window and the other outside it.</para>
    /// </summary>
    internal AirportGroundLayout? ResolveAirportLayout(string airportId) => ReadAirportLayout(airportId).Layout;

    /// <summary>
    /// <see cref="ResolveAirportLayout"/> with its loading state: the primary airport's layout, loaded, else the gate's
    /// answer (<see cref="ReadLayoutGate"/>). The read a destination change picks its layout through
    /// (<see cref="DispatchContext.LayoutReadForAirport"/>), so a loading destination is waited on, not taken for no map.
    /// </summary>
    internal GroundDataRead ReadAirportLayout(string airportId)
    {
        if ((World.GroundLayout is { } primary) && NavigationDatabase.AirportIdsMatch(airportId, primary.AirportId))
        {
            return new GroundDataRead(true, primary, null);
        }

        return ReadLayoutGate(airportId);
    }

    /// <summary>
    /// A flight-plan amend's layout pick (<see cref="ResolveGroundLayout"/>): attached, or, while an airport the pick reads
    /// is deferred, the layout is left as it is and the aircraft waits on those airports to re-run the pick at their
    /// admission (<see cref="AttachWaitingAircraft"/>). Either way it replaces any earlier wait.
    /// </summary>
    private void PickAmendedLayout(AircraftState aircraft)
    {
        List<string> waits = [];
        AirportGroundLayout? layout = ResolveGroundLayout(aircraft, waits);
        bool waiting = (layout is null) && (waits.Count > 0);
        aircraft.Ground.PendingLayoutAirportIds = waiting ? waits : null;
        aircraft.Ground.PendingLayoutForDestination = false;
        if (!waiting)
        {
            aircraft.Ground.Layout = layout;
        }
    }

    /// <summary>
    /// At an admission: every aircraft that waits on airports none of which is still deferred re-runs the pick that waited,
    /// in ordinal callsign order, so all of them attach the one admitted instance in this second.
    /// </summary>
    private void AttachWaitingAircraft()
    {
        List<AircraftState>? ready = null;
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if ((aircraft.Ground.PendingLayoutAirportIds is { } waits) && !AnyDeferred(waits))
            {
                (ready ??= []).Add(aircraft);
            }
        }

        if (ready is null)
        {
            return;
        }

        ready.Sort(static (a, b) => string.CompareOrdinal(a.Callsign, b.Callsign));
        foreach (AircraftState aircraft in ready)
        {
            if (aircraft.Ground.PendingLayoutForDestination)
            {
                aircraft.Ground.PendingLayoutAirportIds = null;
                aircraft.Ground.PendingLayoutForDestination = false;
                CommandDispatcher.PickDestinationLayout(aircraft, ReadAirportLayout);
            }
            else
            {
                PickAmendedLayout(aircraft);
            }
        }
    }

    private bool AnyDeferred(IReadOnlyList<string> airportIds)
    {
        foreach (string airportId in airportIds)
        {
            if (IsLayoutDeferred(airportId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ground layout <paramref name="aircraft"/> taxis on, or null when there is none or when any airport the choice
    /// reads is still loading: a pick that skips a loading airport for another one is never made.
    /// </summary>
    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft) => ResolveGroundLayout(aircraft, null);

    /// <summary>
    /// <see cref="ResolveGroundLayout(AircraftState)"/> with whether its null is a loading airport's: <c>Loading</c> is true
    /// when the pick read an airport whose layout is deferred, false for a layout and for an airport admitted with no map.
    /// </summary>
    internal (AirportGroundLayout? Layout, bool Loading) PickGroundLayout(AircraftState aircraft)
    {
        List<string> waits = [];
        AirportGroundLayout? layout = ResolveGroundLayout(aircraft, waits);
        return (layout, waits.Count > 0);
    }

    // The layout a dispatch context carries: the aircraft's attached one, which is never loading, else the engine's pick.
    private (AirportGroundLayout? Layout, bool Loading) DispatchGroundLayout(AircraftState aircraft) =>
        aircraft.Ground.Layout is { } attached ? (attached, false) : PickGroundLayout(aircraft);

    // ResolveGroundLayout, adding each deferred airport the pick read to waits (ordinal, once) when it is given.
    private AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft, List<string>? waits)
    {
        // An aircraft physically on the ground taxis on the airport its wheels are on —
        // never on a filed destination. A departure that files a destination but no
        // departure (e.g. a VFR plan created via CRC to KSMF while parked at OAK) would
        // otherwise load the destination's layout and reject every taxiway/parking lookup.
        if (aircraft.IsOnGround)
        {
            GroundDataRead physical = ReadOptionalLayout(RunwayOrContextAirport(aircraft), waits);
            if ((physical.Layout is not null) || !physical.IsLoaded)
            {
                return physical.Layout;
            }
        }

        GroundDataRead dep = ReadOptionalLayout(aircraft.FlightPlan.Departure, waits);
        GroundDataRead dest = ReadOptionalLayout(aircraft.FlightPlan.Destination, waits);
        if (!dep.IsLoaded || !dest.IsLoaded)
        {
            return null;
        }

        // Cold-call VFR aircraft (pattern work, full-stop requests) frequently file
        // neither departure nor destination. Treat the assigned arrival runway's
        // airport — or the spawn-time operational airport context — as the implicit
        // destination so the ground layout is available for runway exit and taxi
        // after landing, without writing into the flight plan.
        if ((dep.Layout is null) && (dest.Layout is null))
        {
            return ReadOptionalLayout(RunwayOrContextAirport(aircraft), waits).Layout;
        }

        return NearerLayout(aircraft, dep.Layout, dest.Layout);
    }

    // The assigned runway's airport, else the aircraft's spawn-time operational airport.
    private static string? RunwayOrContextAirport(AircraftState aircraft)
    {
        string? airportId = aircraft.Phases?.AssignedRunway?.AirportId;
        return string.IsNullOrEmpty(airportId) ? aircraft.AirportId : airportId;
    }

    // An airport the aircraft does not name reads as loaded with no layout.
    // A deferred airport read is added to waits when given.
    private GroundDataRead ReadOptionalLayout(string? airportId, List<string>? waits)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return new GroundDataRead(true, null, null);
        }

        GroundDataRead read = ReadLayoutGate(airportId);
        if ((waits is not null) && !read.IsLoaded && IsLayoutDeferred(airportId))
        {
            AddOrdinal(waits, NavigationDatabase.NormalizeAirport(airportId));
        }

        return read;
    }

    private static void AddOrdinal(List<string> codes, string code)
    {
        int index = codes.BinarySearch(code, StringComparer.Ordinal);
        if (index < 0)
        {
            codes.Insert(~index, code);
        }
    }

    private static readonly GroundDataRead LoadingRead = new(false, null, null);

    // The airports a replay found loading with no recorded deferral, by normalized code: warned once, and read as loading
    // for the rest of the replay, so a fetch landing mid-replay never shows at a second the tape did not record.
    private readonly List<string> _unrecordedLoadingWarned = [];

    /// <summary>
    /// The one gate every runtime layout read goes through. An airport in the room's deferred set
    /// (<see cref="SimScenarioState.DeferredLayoutAirports"/>) answers loading without reading the source, as does, on a
    /// replay, one this replay already found loading unrecorded. Otherwise the source answers; one still loading is
    /// deferred and the deferral recorded at this second when the run admits layouts from the source
    /// (<see cref="RunProfile.AdmitsLayoutsFromSource"/>), and on a replay — where only a recorded deferral may defer —
    /// answers loading for the rest of the replay and is logged once as a replay-fidelity break.
    /// </summary>
    private GroundDataRead ReadLayoutGate(string airportId)
    {
        if (IsLayoutDeferred(airportId) || (!RunProfile.AdmitsLayoutsFromSource && ContainsAirport(_unrecordedLoadingWarned, airportId)))
        {
            return LoadingRead;
        }

        GroundDataRead read = _groundData.Read(airportId);
        if (read.IsLoaded)
        {
            return read;
        }

        if (RunProfile.AdmitsLayoutsFromSource)
        {
            DeferLayout(NavigationDatabase.NormalizeAirport(airportId));
        }
        else
        {
            WarnUnrecordedLoading(NavigationDatabase.NormalizeAirport(airportId));
        }

        return read;
    }

    /// <summary>
    /// The layout a snapshot restore re-attaches an aircraft to: a deferred airport's is none, any other the source's. A
    /// restore is a reload read, so it never defers and never records.
    /// </summary>
    private AirportGroundLayout? ReadRestoredLayout(string airportId)
    {
        if ((World.GroundLayout is { } primary) && NavigationDatabase.AirportIdsMatch(airportId, primary.AirportId))
        {
            return primary;
        }

        return IsLayoutDeferred(airportId) ? null : _groundData.Read(airportId).Layout;
    }

    /// <summary>
    /// The layout a restored aircraft goes back to: its own airport's; none for a deferred airport, or for an aircraft
    /// with no airport that waits on a pick (it was detached live, and attaches at the admission); else the primary's.
    /// </summary>
    private AirportGroundLayout? RestoredAircraftLayout(AircraftGroundOpsDto ground, AirportGroundLayout? primary)
    {
        if (ground.LayoutAirportId is not { } layoutAirportId)
        {
            return ground.PendingLayoutAirportIds is { Count: > 0 } ? null : primary;
        }

        if (ReadRestoredLayout(layoutAirportId) is { } layout)
        {
            return layout;
        }

        return IsLayoutDeferred(layoutAirportId) ? null : primary;
    }

    /// <summary>Whether <paramref name="airportId"/>, in either form of its code, is in the room's deferred set. Allocates nothing.</summary>
    private bool IsLayoutDeferred(string airportId) => (Scenario is { } scenario) && ContainsAirport(scenario.DeferredLayoutAirports, airportId);

    /// <summary>
    /// Whether <paramref name="airportId"/>'s layout is loading: the airport is in the room's deferred set
    /// (<see cref="SimScenarioState.DeferredLayoutAirports"/>), the set the gate answers loading from. Reads no source,
    /// records nothing and logs nothing, so a per-tick reader may ask it every tick.
    /// </summary>
    internal bool IsLayoutLoading(string airportId) => IsLayoutDeferred(airportId);

    /// <summary>
    /// A landing clearance's layout attach, for the runway exit and the taxi in: an aircraft carrying no layout takes the
    /// pick's (<see cref="PickAmendedLayout"/>), or, while an airport that pick reads is loading, waits on it and attaches
    /// the admitted layout at its admission (<see cref="AttachWaitingAircraft"/>). An aircraft already carrying a layout,
    /// or already waiting on a pick, is left as it is.
    /// </summary>
    internal void AttachLayoutAfterClearance(AircraftState aircraft)
    {
        if ((aircraft.Ground.Layout is not null) || (aircraft.Ground.PendingLayoutAirportIds is { Count: > 0 }))
        {
            return;
        }

        PickAmendedLayout(aircraft);
    }

    /// <summary>
    /// Forgets the airports a replay found loading with no recorded deferral, so the run that follows reads them from the
    /// source again. The server calls this when a room leaves tape playback; a replay from t=0 starts with none.
    /// </summary>
    internal void ForgetUnrecordedLoadingLayouts() => _unrecordedLoadingWarned.Clear();

    // Whether normalized codes hold airportId in either form of its code. Allocates nothing.
    private static bool ContainsAirport(List<string> normalizedCodes, string airportId)
    {
        if (normalizedCodes.Count == 0)
        {
            return false;
        }

        ReadOnlySpan<char> key = airportId.AsSpan().Trim();
        if ((key.Length == 4) && (key[0] is 'K' or 'k'))
        {
            key = key[1..];
        }

        foreach (string code in normalizedCodes)
        {
            if (code.AsSpan().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void DeferLayout(string faa)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        scenario.AddDeferredLayoutAirport(faa);
        RecordAction(new RecordedLayoutDeferred(scenario.ElapsedSeconds, faa));
    }

    private void WarnUnrecordedLoading(string faa)
    {
        if (!_unrecordedLoadingWarned.Contains(faa))
        {
            _unrecordedLoadingWarned.Add(faa);
            _logger.LogWarning(
                "replay-fidelity: ground layout for {AirportId} is loading on replay with no recorded deferral; reading it as loading",
                faa
            );
        }
    }

    /// <summary>
    /// Live, at the opening of <paramref name="second"/>: every deferred airport whose layout has landed, in ordinal order,
    /// leaves the deferred set and its admission is recorded at this second. A replay admits only on the recorded admission.
    /// </summary>
    private void AdmitLandedLayouts(int second)
    {
        if (!RunProfile.AdmitsLayoutsFromSource || (Scenario is not { DeferredLayoutAirports.Count: > 0 } scenario))
        {
            return;
        }

        List<string> deferred = scenario.DeferredLayoutAirports;
        int i = 0;
        while (i < deferred.Count)
        {
            string faa = deferred[i];
            if (!_groundData.Read(faa).IsLoaded)
            {
                i++;
                continue;
            }

            deferred.RemoveAt(i);
            RecordAction(new RecordedLayoutAdmitted(second, faa));
            _layoutAdmittedThisSecond = true;
        }
    }

    // An airport left the deferred set at this second's opening; the attach pass runs once OpenSecond has applied the
    // second's pre-tick records.
    private bool _layoutAdmittedThisSecond;

    /// <summary>
    /// The attach pass (<see cref="AttachWaitingAircraft"/>) for a second in which an airport was admitted, live from the
    /// source or on a replay from the record, run once at the end of <see cref="OpenSecond"/>.
    /// </summary>
    private void AttachWaitingAircraftAfterAdmission()
    {
        if (!_layoutAdmittedThisSecond)
        {
            return;
        }

        _layoutAdmittedThisSecond = false;
        AttachWaitingAircraft();
    }

    /// <summary>A recorded deferral: the airport joins the deferred set. The pre-tick half of the router's <c>ApplyRecorded</c>.</summary>
    internal void ApplyRecordedLayoutDeferred(RecordedLayoutDeferred deferred) =>
        Scenario?.AddDeferredLayoutAirport(NavigationDatabase.NormalizeAirport(deferred.AirportId));

    /// <summary>
    /// A recorded admission: the airport leaves the deferred set, so its reads answer the source's layout, and the aircraft
    /// that waited on it attach as they did live, in the attach pass at the end of the second's opening.
    /// </summary>
    internal void ApplyRecordedLayoutAdmitted(RecordedLayoutAdmitted admitted)
    {
        if (Scenario?.DeferredLayoutAirports.Remove(NavigationDatabase.NormalizeAirport(admitted.AirportId)) == true)
        {
            _layoutAdmittedThisSecond = true;
        }
    }

    /// <summary>
    /// The layout of an airport that must already be loaded at <paramref name="site"/> (a scenario load after its
    /// prefetch, a restore, a recording or snapshot load, a generator spawn). One that is still loading reads as null
    /// and is logged as an error, once per airport for this engine.
    /// </summary>
    private AirportGroundLayout? ReadReadyLayout(string airportId, string site) =>
        (_readyLayouts ??= new ReadyLayoutReader(_groundData, _logger)).Read(airportId, site);

    // Departure and destination both resolved: the one whose nearest node is closer to the aircraft.
    private static AirportGroundLayout? NearerLayout(AircraftState aircraft, AirportGroundLayout? depLayout, AirportGroundLayout? destLayout)
    {
        if (depLayout is null)
        {
            return destLayout;
        }

        if (destLayout is null || destLayout == depLayout)
        {
            return depLayout;
        }

        GroundNode? depNode = depLayout.FindNearestNode(aircraft.Position);
        GroundNode? destNode = destLayout.FindNearestNode(aircraft.Position);

        double depDist = depNode is not null ? GeoMath.DistanceNm(aircraft.Position, depNode.Position) : double.MaxValue;
        double destDist = destNode is not null ? GeoMath.DistanceNm(aircraft.Position, destNode.Position) : double.MaxValue;

        return destDist < depDist ? destLayout : depLayout;
    }

    // --- Private tick methods ---
}
