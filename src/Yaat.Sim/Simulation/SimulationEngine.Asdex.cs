using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Spine;

namespace Yaat.Sim.Simulation;

// The ASDE-X and SAAB SAID half of the engine: the CRC-sourced mutations a controller's surface display sends, and the
// room-wide alert-inhibit sweep ASDXALERTS is. Everything they write is per-aircraft AircraftStarsState — snapshotted,
// so a replay, a rewind and a session restore all carry it — through the same TrackEngine bodies the typed ASDX verbs
// use. The one thing that leaves the simulation is a terminate: the live room turns the consumer call into its
// one-shot delete marker, which the broadcaster drains into DeleteAsdexTracks / DeleteSaabSaidTracks.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies one recorded CRC ASDE-X mutation. <c>EditDbFields</c> writes every display-field override the record
    /// carries (a null field is one the record does not touch); the per-aircraft verbs move the tag / terminate /
    /// suspend / alert-inhibit bits; <c>EnableAllAlerts</c> is the room-wide sweep. A mutation naming an aircraft the
    /// world no longer holds applies nothing, silently — the display it came from is one frame behind the world.
    /// <paramref name="host"/> is told about a terminate, and about nothing else.
    /// </summary>
    public void ApplyAsdexMutation(RecordedAsdexMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnAsdexAircraft(mutation, ac => ApplyAsdexEdit(ac, mutation));
                break;
            case "Tag":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Tag));
                break;
            case "Terminate":
                OnAsdexAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Terminate);
                        host.OnAsdexTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Suspend));
                break;
            case "Unsuspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Unsuspend));
                break;
            case "InhibitAlerts":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.InhibitAlerts));
                break;
            case "EnableAllAlerts":
                EnableAllAsdexAlerts();
                break;
        }
    }

    /// <summary>
    /// Applies one recorded CRC SAAB SAID mutation — the same shape as the ASDE-X one on the <c>Said*</c> fields,
    /// with no alerts to inhibit or enable. <paramref name="host"/> is told about a terminate.
    /// </summary>
    public void ApplySaidMutation(RecordedSaidMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnSaidAircraft(mutation, ac => ApplySaidEdit(ac, mutation));
                break;
            case "Tag":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Tag));
                break;
            case "Terminate":
                OnSaidAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleSaidVerb(ac, SaidVerb.Terminate);
                        host.OnSaidTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Suspend));
                break;
            case "Unsuspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Unsuspend));
                break;
        }
    }

    /// <summary>
    /// Applies one recorded CRC ASDE-X safety-logic configuration push: the facility's runway footprints, active
    /// runway configuration and inhibited arrival-alert positions replace that facility's configuration, which the
    /// surface-alert detector reads; every other facility's is left as it stands. Snapshotted scenario state, so a
    /// rewind onto a snapshot that predates the push returns to the configuration the snapshot holds (the load's seed
    /// before any push). With no scenario loaded there is nothing to write to and the push is dropped.
    /// </summary>
    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change)
    {
        if (Scenario is not { } scenario)
        {
            _logger.LogDebug(
                "ASDE-X safety-logic push for facility {FacilityId} at t={Seconds} dropped: no scenario is loaded",
                change.FacilityId,
                change.ElapsedSeconds
            );
            return;
        }

        AsdexSafetyLogicConfig? stored = scenario.AsdexSafetyLogicConfigs.GetValueOrDefault(change.FacilityId);
        AsdexSafetyLogicConfig config = stored is null ? FirstPush(change) : MergePush(stored, change);
        scenario.AsdexSafetyLogicConfigs = scenario.AsdexSafetyLogicConfigs.SetItem(change.FacilityId, config);
        _asdexConfigChangedFacilities.Add(change.FacilityId);
    }

    /// <summary>A push to a facility that held no configuration is taken whole, as chosen, each inhibit starting now.</summary>
    private static AsdexSafetyLogicConfig FirstPush(RecordedAsdexSafetyLogicChange change) =>
        change.Config with
        {
            RunwayConfigurationChosen = true,
            InhibitStartSeconds = InhibitStarts(
                change.Config.InhibitedArrivalAlertPositionIds,
                AsdexSafetyLogicConfig.NoSeconds,
                change.ElapsedSeconds
            ),
            SupersededConfigurationIds = AsdexSafetyLogicConfig.NoSeconds,
        };

    /// <summary>
    /// A push merged onto the facility's stored configuration. CRC pushes the display's whole view, each push changing one
    /// thing. A different runway configuration id is a user's choice (CRC's F2), which clears the inhibits, unless the id is
    /// one an <c>ARWY</c> re-pick moved away from within the last <see cref="AsdexSafetyLogicConfig.SupersededIdWindowSeconds"/>:
    /// then the display is stale, and the stored id stays. The push's closures always apply. Its inhibit list applies only
    /// when the push carries the stored id and its closures match the stored ones, since a stale display's push and a closure
    /// toggle from a display behind a lapse or a clear carry a stale list; a position kept keeps its start, one added starts now.
    /// </summary>
    private static AsdexSafetyLogicConfig MergePush(AsdexSafetyLogicConfig stored, RecordedAsdexSafetyLogicChange change)
    {
        AsdexSafetyLogicConfig pushed = change.Config;
        double now = change.ElapsedSeconds;
        IReadOnlyDictionary<string, double> liveSuperseded = LiveSuperseded(stored.SupersededConfigurationIds, now);
        bool idDiffers = pushed.RunwayConfigurationId != stored.RunwayConfigurationId;
        bool choice = idDiffers && !liveSuperseded.ContainsKey(pushed.RunwayConfigurationId);

        IReadOnlyList<string> inhibited;
        IReadOnlyDictionary<string, double> starts;
        if (choice)
        {
            inhibited = [];
            starts = AsdexSafetyLogicConfig.NoSeconds;
        }
        else if (!idDiffers && SameClosures(pushed, stored))
        {
            inhibited = pushed.InhibitedArrivalAlertPositionIds;
            starts = InhibitStarts(inhibited, stored.InhibitStartSeconds, now);
        }
        else
        {
            inhibited = stored.InhibitedArrivalAlertPositionIds;
            starts = stored.InhibitStartSeconds;
        }

        // Records compare their lists by reference, so the pushed list is kept whenever it is what the result holds.
        if (inhibited.SequenceEqual(pushed.InhibitedArrivalAlertPositionIds))
        {
            inhibited = pushed.InhibitedArrivalAlertPositionIds;
        }

        return pushed with
        {
            RunwayConfigurationId = choice ? pushed.RunwayConfigurationId : stored.RunwayConfigurationId,
            InhibitedArrivalAlertPositionIds = inhibited,
            RunwayConfigurationChosen = stored.RunwayConfigurationChosen || choice,
            InhibitStartSeconds = starts,
            SupersededConfigurationIds = liveSuperseded,
        };
    }

    /// <summary><paramref name="superseded"/> without the entries whose window has passed by <paramref name="now"/>.</summary>
    private static IReadOnlyDictionary<string, double> LiveSuperseded(IReadOnlyDictionary<string, double> superseded, double now) =>
        superseded.Values.All(second => second + AsdexSafetyLogicConfig.SupersededIdWindowSeconds > now)
            ? superseded
            : AsdexSafetyLogicConfig.Seconds(superseded.Where(entry => entry.Value + AsdexSafetyLogicConfig.SupersededIdWindowSeconds > now));

    /// <summary>
    /// True when each stored runway is in the push, matched by id (the first entry of an id), open or closed alike, and every
    /// runway only the push names is open. CRC never removes a runway it was sent, so a display's list can carry more runways,
    /// in another order; the footprints are not compared.
    /// </summary>
    private static bool SameClosures(AsdexSafetyLogicConfig pushed, AsdexSafetyLogicConfig stored)
    {
        Dictionary<string, bool> pushedClosed = [];
        foreach (AsdexRunwayConfig runway in pushed.Runways)
        {
            pushedClosed.TryAdd(runway.Id, runway.IsClosed);
        }

        HashSet<string> storedIds = [.. stored.Runways.Select(r => r.Id)];
        return stored.Runways.All(r => pushedClosed.TryGetValue(r.Id, out bool closed) && (closed == r.IsClosed))
            && pushedClosed.Where(entry => !storedIds.Contains(entry.Key)).All(entry => !entry.Value);
    }

    /// <summary>Each of <paramref name="positions"/> with its start in <paramref name="known"/>, or <paramref name="now"/> if it has none.</summary>
    private static IReadOnlyDictionary<string, double> InhibitStarts(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, double> known,
        double now
    ) => AsdexSafetyLogicConfig.Seconds(positions.Distinct().Select(id => KeyValuePair.Create(id, known.GetValueOrDefault(id, now))));

    /// <summary>
    /// Re-picks the runway configuration of every ASDE-X facility whose airport's active runways differ between
    /// <paramref name="before"/> and the scenario's current ones and whose configuration no CRC user has chosen
    /// (<see cref="AsdexSafetyLogicSeed.Repick"/>), recording each configuration moved away from as superseded now.
    /// Deterministic from scenario state, so a replay reproduces it. Each facility whose configuration changed is handed
    /// to the host on the next drain.
    /// </summary>
    public void RepickAsdexRunwayConfigurations(ActiveRunways before)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> previous = scenario.AsdexSafetyLogicConfigs;
        scenario.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Repick(
            previous,
            scenario.ArtccConfig,
            before,
            scenario.ActiveRunways,
            scenario.ElapsedSeconds,
            recordSuperseded: true
        );
        foreach ((string facilityId, AsdexSafetyLogicConfig config) in scenario.AsdexSafetyLogicConfigs)
        {
            if (!ReferenceEquals(config, previous.GetValueOrDefault(facilityId)))
            {
                _asdexConfigChangedFacilities.Add(facilityId);
            }
        }
    }

    // The facilities whose stored safety-logic configuration a push, a re-pick or a lapse changed since the last drain.
    // Transient, not snapshotted: it only says what the host has not been sent yet.
    private readonly SortedSet<string> _asdexConfigChangedFacilities = new(StringComparer.Ordinal);

    /// <summary>Hands the host the facilities whose configuration changed since the last drain, ordinal; nothing when none did.</summary>
    private void DrainAsdexSafetyLogicConfigChangesInto(IStateChangeConsumer host)
    {
        IReadOnlyList<string> changed = DrainAsdexSafetyLogicConfigChanges();
        if (changed.Count > 0)
        {
            host.OnAsdexSafetyLogicConfigsChanged(changed);
        }
    }

    /// <summary>
    /// Takes the facilities whose configuration changed since the last drain, ordinal, and clears them; empty when none did.
    /// For a path that mutates the engine outside both the action router's drain and the post-physics one.
    /// </summary>
    public IReadOnlyList<string> DrainAsdexSafetyLogicConfigChanges()
    {
        if (_asdexConfigChangedFacilities.Count == 0)
        {
            return [];
        }

        List<string> changed = [.. _asdexConfigChangedFacilities];
        _asdexConfigChangedFacilities.Clear();
        return changed;
    }

    /// <summary>
    /// Drops every inhibited position whose inhibit was set <see cref="AsdexSafetyLogicConfig.InhibitLapseSeconds"/> or more
    /// sim seconds ago. Worked out from stored state, never recorded, so every run kind lapses on the same second.
    /// </summary>
    private void LapseAsdexInhibits(SimScenarioState scenario)
    {
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig>.Builder? lapsed = null;
        foreach ((string facilityId, AsdexSafetyLogicConfig config) in scenario.AsdexSafetyLogicConfigs)
        {
            if (
                (config.InhibitStartSeconds.Count == 0)
                || !config.InhibitStartSeconds.Values.Any(start => start + AsdexSafetyLogicConfig.InhibitLapseSeconds <= scenario.ElapsedSeconds)
            )
            {
                continue;
            }

            HashSet<string> kept =
            [
                .. config
                    .InhibitStartSeconds.Where(entry => entry.Value + AsdexSafetyLogicConfig.InhibitLapseSeconds > scenario.ElapsedSeconds)
                    .Select(entry => entry.Key),
            ];
            lapsed ??= scenario.AsdexSafetyLogicConfigs.ToBuilder();
            lapsed[facilityId] = config with
            {
                InhibitedArrivalAlertPositionIds = [.. config.InhibitedArrivalAlertPositionIds.Where(kept.Contains)],
                InhibitStartSeconds = AsdexSafetyLogicConfig.Seconds(config.InhibitStartSeconds.Where(entry => kept.Contains(entry.Key))),
            };
            _asdexConfigChangedFacilities.Add(facilityId);
        }

        if (lapsed is not null)
        {
            scenario.AsdexSafetyLogicConfigs = lapsed.ToImmutable();
        }
    }

    // The surface airports resolved for this config and navigation-data instance; rebuilt when either is replaced.
    private ArtccConfigRoot? _surfaceAirportsConfig;
    private NavigationDatabase? _surfaceAirportsNavDb;
    private SurfaceAirports _surfaceAirports = SurfaceAirports.None;

    /// <summary>
    /// The post-physics surface-membership pass: each aircraft's ASDE-X and SAAB SAID display membership moves one
    /// second on (<see cref="SurfaceMembership"/>). Runs on every run kind — the hysteresis makes this second's
    /// membership depend on last second's, and the membership is snapshotted per-aircraft state. A membership only ever
    /// names airports the current ARTCC config declares, so with no config (or no navigation data) every membership
    /// clears to empty.
    /// </summary>
    public void TickSurfaceMembership()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        SurfaceAirports airports = ResolveSurfaceAirports(scenario.ArtccConfig, NavigationDatabase.InstanceOrNull);
        foreach (AircraftState ac in World.GetSnapshot())
        {
            EvaluateSurfaceMembership(ac, airports);
        }
    }

    /// <summary>
    /// One aircraft's ASDE-X and SAAB SAID membership, moved on from the one it holds by the rule the tick step applies.
    /// The step runs only on ticks while the CRC broadcast also runs paused, so a body that puts an aircraft in the world
    /// or moves it between ticks (a spawn, a warp) calls this to keep the displays current. An aircraft that has not moved
    /// since its last evaluation keeps the set it holds, so a call between ticks changes nothing the next step would not.
    /// </summary>
    public void EvaluateSurfaceMembership(AircraftState ac)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        EvaluateSurfaceMembership(ac, ResolveSurfaceAirports(scenario.ArtccConfig, NavigationDatabase.InstanceOrNull));
    }

    private static void EvaluateSurfaceMembership(AircraftState ac, SurfaceAirports airports)
    {
        AircraftStarsState stars = ac.Stars;
        stars.VisibleAsdexAirports = SurfaceMembership.EvaluateAsdex(ac, stars.VisibleAsdexAirports, airports);
        stars.VisibleSaidAirports = SurfaceMembership.EvaluateSaid(ac, stars.VisibleSaidAirports, airports);
    }

    /// <summary>
    /// The facility-tree walk behind the airport lists looks up a fix per facility and a field elevation per SAID
    /// airport, so the result is kept until the config or the navigation data is replaced.
    /// </summary>
    private SurfaceAirports ResolveSurfaceAirports(ArtccConfigRoot? config, NavigationDatabase? navDb)
    {
        if ((config is null) || (navDb is null))
        {
            return SurfaceAirports.None;
        }

        if (!ReferenceEquals(_surfaceAirportsConfig, config) || !ReferenceEquals(_surfaceAirportsNavDb, navDb))
        {
            _surfaceAirports = SurfaceAirports.Resolve(config, navDb);
            _surfaceAirportsConfig = config;
            _surfaceAirportsNavDb = navDb;
        }

        return _surfaceAirports;
    }

    /// <summary>Airports farther than this from a configured runway footprint are not its owner.</summary>
    private const double AsdexRunwayAirportSearchNm = 5;

    /// <summary>
    /// The post-physics ASDE-X Safety Logic pass. It clears facilities with no configuration and runway-less
    /// configurations first, then runs the detector per runway facility in ordinal order: the facility's configured
    /// runway footprints (plus the ground layout's taxiways at the layout's own airport) go to the stateless detector,
    /// with <c>closedRunwayAlertsOnly</c> decided each tick by whether the facility's runway configuration id is one of
    /// its configurations (<see cref="AsdexSafetyLogicSeed.NamesRunwayConfiguration"/>), and what it finds is diffed
    /// against the alerts that facility is already holding.
    /// With no navigation data the runway facilities keep their standing alerts.
    /// Only the difference leaves the engine — the alerts that appeared
    /// and the ids that went away, named by facility — because CRC's alert topic is additive with an explicit delete.
    /// Runs on every run kind: the standing set is snapshotted scenario state, so a replay, a rewind and a session
    /// restore all reach the alert picture the live room had.
    /// </summary>
    public void TickAsdexAlerts(IHostConsumers host)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        LapseAsdexInhibits(scenario);
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> configs = scenario.AsdexSafetyLogicConfigs;
        foreach (string facilityId in scenario.ActiveAsdexAlerts.Keys.Where(id => !configs.ContainsKey(id)).ToList())
        {
            ApplyAsdexDetection(scenario, facilityId, [], host);
        }

        foreach (string facilityId in configs.Where(entry => entry.Value.Runways.Count == 0).Select(entry => entry.Key))
        {
            ApplyAsdexDetection(scenario, facilityId, [], host);
        }

        if (NavigationDatabase.InstanceOrNull is not { } navDb)
        {
            return;
        }

        List<AircraftState>? aircraft = null;
        foreach ((string facilityId, AsdexSafetyLogicConfig config) in configs.Where(entry => entry.Value.Runways.Count > 0))
        {
            aircraft ??= World.GetSnapshot();
            double fieldElevationFt = FacilityFieldElevation(facilityId, navDb);
            List<AsdexRunwaySurface> runways = BuildAsdexRunwaySurfaces(config.Runways, navDb, fieldElevationFt);
            List<AsdexTaxiwaySegment> taxiways = IsLayoutAirport(World.GroundLayout, facilityId) ? BuildAsdexTaxiwaySegments(World.GroundLayout) : [];

            ApplyAsdexDetection(
                scenario,
                facilityId,
                AsdexSafetyLogicDetector.Detect(
                    runways,
                    taxiways,
                    aircraft,
                    fieldElevationFt,
                    closedRunwayAlertsOnly: !AsdexSafetyLogicSeed.NamesRunwayConfiguration(
                        scenario.ArtccConfig,
                        facilityId,
                        config.RunwayConfigurationId
                    )
                ),
                host
            );
        }
    }

    /// <summary>
    /// One facility's hold bars: the layout and safety-logic runways they were built from (with the footprints the
    /// configuration last carried), each runway's axis, the fixed set itself and its ids, and the bars last reported Active to
    /// the host. Immutable, so a reader off the tick thread takes the reference once.
    /// </summary>
    private sealed record AsdexHoldBarFacility(
        AirportGroundLayout Layout,
        IReadOnlyList<AsdexHoldBarRunway> Runways,
        IReadOnlyList<AsdexRunwayAxis> Axes,
        IReadOnlyList<AsdexHoldBar> Bars,
        FrozenSet<string> BarIds,
        ImmutableSortedDictionary<string, AsdexHoldBar> ReportedActive
    );

    private static readonly ImmutableSortedDictionary<string, AsdexHoldBar> NoActiveHoldBars = ImmutableSortedDictionary.Create<string, AsdexHoldBar>(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Each facility's hold-bar set and the statuses last reported for it. Transient engine state, never snapshotted: the
    /// statuses are derived from the aircraft every tick, and what was last reported is what the displays show, so it
    /// survives a snapshot restore (the next tick diffs against it) and a scenario load (the bars a load's world no longer
    /// lights are reported Suppressed).
    /// </summary>
    private ImmutableSortedDictionary<string, AsdexHoldBarFacility> _asdexHoldBars = ImmutableSortedDictionary.Create<string, AsdexHoldBarFacility>(
        StringComparer.Ordinal
    );

    /// <summary>
    /// The post-physics hold-bar pass, run on every run kind beside <see cref="TickAsdexAlerts"/>. Bars exist only at the
    /// facility whose airport is the loaded ground layout; the set is built once per facility and layout. Each tick the
    /// stateless <see cref="AsdexHoldBarStatusEvaluator"/> decides which bars are Active, and only the bars whose status
    /// differs from the one last reported reach the host. A facility that no longer has bars (another layout, no scenario)
    /// reports every bar it held Active as Suppressed and is dropped. With no navigation data nothing changes.
    /// </summary>
    public void TickAsdexHoldBars(IHostConsumers host)
    {
        if (NavigationDatabase.InstanceOrNull is not { } navDb)
        {
            return;
        }

        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> configs = Scenario?.AsdexSafetyLogicConfigs ?? AsdexSafetyLogicSeed.Empty;
        foreach (string facilityId in _asdexHoldBars.Keys.Where(id => !configs.ContainsKey(id) || !IsLayoutAirport(World.GroundLayout, id)).ToList())
        {
            AsdexHoldBarFacility retired = _asdexHoldBars[facilityId];
            _asdexHoldBars = _asdexHoldBars.Remove(facilityId);
            ReportHoldBars(host, facilityId, [.. retired.ReportedActive.Values.Select(bar => new AsdexHoldBarState(bar, IsActive: false))]);
        }

        List<AircraftState>? aircraft = null;
        foreach ((string facilityId, AsdexSafetyLogicConfig config) in configs)
        {
            if ((World.GroundLayout is not { } layout) || !IsLayoutAirport(layout, facilityId))
            {
                continue;
            }

            AsdexHoldBarFacility facility = WithCurrentAreas(facilityId, CurrentHoldBarFacility(facilityId, config, layout, navDb), config);
            aircraft ??= World.GetSnapshot();
            IReadOnlySet<string> active = AsdexHoldBarStatusEvaluator.ActiveBarIds(
                facility.Runways,
                facility.Axes,
                facility.Bars,
                aircraft,
                FacilityFieldElevation(facilityId, navDb)
            );
            ApplyHoldBarStatuses(host, facilityId, facility, active);
        }
    }

    /// <summary>
    /// The facility's set for <paramref name="layout"/>: the one already held, or a fresh build that keeps the statuses
    /// last reported, so bars a rebuilt set no longer lights are still reported Suppressed.
    /// </summary>
    private AsdexHoldBarFacility CurrentHoldBarFacility(
        string facilityId,
        AsdexSafetyLogicConfig config,
        AirportGroundLayout layout,
        NavigationDatabase navDb
    )
    {
        AsdexHoldBarFacility? held = _asdexHoldBars.GetValueOrDefault(facilityId);
        if ((held is not null) && ReferenceEquals(held.Layout, layout))
        {
            return held;
        }

        AsdexHoldBarFacility built = BuildHoldBarFacility(facilityId, config, layout, navDb) with
        {
            ReportedActive = held?.ReportedActive ?? NoActiveHoldBars,
        };
        _asdexHoldBars = _asdexHoldBars.SetItem(facilityId, built);
        _logger.LogDebug(
            "ASDE-X hold bars for {FacilityId}: {Count} built from the {Airport} layout",
            facilityId,
            built.Bars.Count,
            layout.AirportId
        );
        return built;
    }

    private static AsdexHoldBarFacility BuildHoldBarFacility(
        string facilityId,
        AsdexSafetyLogicConfig config,
        AirportGroundLayout layout,
        NavigationDatabase navDb
    )
    {
        IReadOnlyList<AsdexHoldBarRunway> runways = AsdexHoldBarBuilder.ResolveRunways(facilityId, config.Runways, navDb);
        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build(runways, layout);
        return new AsdexHoldBarFacility(
            layout,
            runways,
            [.. runways.Select(runway => new AsdexRunwayAxis(runway.Runway))],
            bars,
            bars.Select(bar => bar.Id).ToFrozenSet(StringComparer.Ordinal),
            NoActiveHoldBars
        );
    }

    /// <summary>
    /// The facility with the footprints its configuration carries now (a display may have pushed a new one), kept for the
    /// next tick; the same facility when no footprint changed.
    /// </summary>
    private AsdexHoldBarFacility WithCurrentAreas(string facilityId, AsdexHoldBarFacility facility, AsdexSafetyLogicConfig config)
    {
        List<AsdexHoldBarRunway>? updated = null;
        for (int i = 0; i < facility.Runways.Count; i++)
        {
            AsdexHoldBarRunway runway = facility.Runways[i];
            IReadOnlyList<LatLon> area = ConfiguredArea(config, runway.Id);
            if (!ReferenceEquals(area, runway.Area))
            {
                updated ??= [.. facility.Runways];
                updated[i] = runway with { Area = area };
            }
        }

        if (updated is null)
        {
            return facility;
        }

        AsdexHoldBarFacility refreshed = facility with { Runways = updated };
        _asdexHoldBars = _asdexHoldBars.SetItem(facilityId, refreshed);
        return refreshed;
    }

    /// <summary>The footprint the configuration carries for <paramref name="runwayId"/>, its first entry's; empty when it has none.</summary>
    private static IReadOnlyList<LatLon> ConfiguredArea(AsdexSafetyLogicConfig config, string runwayId)
    {
        foreach (AsdexRunwayConfig runway in config.Runways)
        {
            if (string.Equals(runway.Id, runwayId, StringComparison.Ordinal))
            {
                return runway.AreaPoints;
            }
        }

        return [];
    }

    /// <summary>Reports the bars whose status differs from the last report, and records what was reported.</summary>
    private void ApplyHoldBarStatuses(IHostConsumers host, string facilityId, AsdexHoldBarFacility facility, IReadOnlySet<string> active)
    {
        List<AsdexHoldBarState>? changed = null;
        foreach (AsdexHoldBar bar in facility.Bars)
        {
            bool isActive = active.Contains(bar.Id);
            if (isActive != facility.ReportedActive.ContainsKey(bar.Id))
            {
                (changed ??= []).Add(new AsdexHoldBarState(bar, isActive));
            }
        }

        // Bars an earlier set reported Active that this set no longer has.
        foreach (AsdexHoldBar bar in facility.ReportedActive.Values)
        {
            if (!facility.BarIds.Contains(bar.Id))
            {
                (changed ??= []).Add(new AsdexHoldBarState(bar, IsActive: false));
            }
        }

        if (changed is null)
        {
            return;
        }

        var reported = facility.ReportedActive.ToBuilder();
        foreach (AsdexHoldBarState state in changed)
        {
            if (state.IsActive)
            {
                reported[state.Bar.Id] = state.Bar;
            }
            else
            {
                reported.Remove(state.Bar.Id);
            }
        }

        _asdexHoldBars = _asdexHoldBars.SetItem(facilityId, facility with { ReportedActive = reported.ToImmutable() });
        ReportHoldBars(host, facilityId, changed);
    }

    private static void ReportHoldBars(IHostConsumers host, string facilityId, IReadOnlyList<AsdexHoldBarState> changed)
    {
        if (changed.Count > 0)
        {
            host.OnAsdexHoldBarsChanged(facilityId, changed);
        }
    }

    /// <summary>
    /// The facility's whole hold-bar set with the status last reported for each bar: what a display subscribing now must get.
    /// Before the first tick has built the set, it is built here (without being kept) and every bar is Suppressed; a facility
    /// with no bars (not the layout's airport, no configuration) gives an empty list. It reads engine state the tick writes, so
    /// a caller holds the room's tick gate, as every server caller does.
    /// </summary>
    public IReadOnlyList<AsdexHoldBarState> AsdexHoldBarStates(string facilityId)
    {
        if (_asdexHoldBars.GetValueOrDefault(facilityId) is { } held)
        {
            return [.. held.Bars.Select(bar => new AsdexHoldBarState(bar, held.ReportedActive.ContainsKey(bar.Id)))];
        }

        if (
            (Scenario?.AsdexSafetyLogicConfigs.GetValueOrDefault(facilityId) is { } config)
            && (World.GroundLayout is { } layout)
            && IsLayoutAirport(layout, facilityId)
            && (NavigationDatabase.InstanceOrNull is { } navDb)
        )
        {
            return [.. BuildHoldBarFacility(facilityId, config, layout, navDb).Bars.Select(bar => new AsdexHoldBarState(bar, IsActive: false))];
        }

        return [];
    }

    /// <summary>The facilities holding a hold-bar set, in ordinal order.</summary>
    public IReadOnlyList<string> AsdexHoldBarFacilityIds => [.. _asdexHoldBars.Keys];

    /// <summary>The bars last reported Active, by facility; facilities with none are left out.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AsdexHoldBar>> ReportedActiveAsdexHoldBars =>
        _asdexHoldBars
            .Where(entry => entry.Value.ReportedActive.Count > 0)
            .ToDictionary(entry => entry.Key, entry => (IReadOnlyList<AsdexHoldBar>)[.. entry.Value.ReportedActive.Values], StringComparer.Ordinal);

    /// <summary>The facility's field elevation from navdata, under its FAA id or the ICAO "K" form; 0 when navdata has neither.</summary>
    private static double FacilityFieldElevation(string facilityId, NavigationDatabase navDb) => navDb.GetAirportElevation(facilityId) ?? 0;

    /// <summary>True when the ground layout is the facility's airport, matched on the FAA id with a leading ICAO "K" stripped.</summary>
    private static bool IsLayoutAirport(AirportGroundLayout? layout, string facilityId) =>
        (layout is not null) && (NavigationDatabase.NormalizeAirport(layout.AirportId) == NavigationDatabase.NormalizeAirport(facilityId));

    /// <summary>The snapshot's standing alerts, grouped by facility; an empty or missing list restores none.</summary>
    private static ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, AsdexSafetyAlert>> RestoreAsdexAlerts(
        List<AsdexSafetyAlertDto>? alertDtos
    )
    {
        ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, AsdexSafetyAlert>>.Builder restored =
            ImmutableSortedDictionary.CreateBuilder<string, ImmutableSortedDictionary<string, AsdexSafetyAlert>>(StringComparer.Ordinal);
        foreach (IGrouping<string, AsdexSafetyAlertDto> facility in (alertDtos ?? []).GroupBy(dto => dto.FacilityId, StringComparer.Ordinal))
        {
            var alerts = SimScenarioState.NoAsdexAlerts.ToBuilder();
            foreach (AsdexSafetyAlertDto dto in facility)
            {
                alerts[dto.Id] = new AsdexSafetyAlert(
                    dto.Id,
                    dto.Kind,
                    [.. dto.RunwayIds],
                    [.. dto.Callsigns],
                    [.. dto.MessageLines],
                    dto.PlayAuralAlert,
                    dto.GoAround
                );
            }

            restored[facility.Key] = alerts.ToImmutable();
        }

        return restored.ToImmutable();
    }

    /// <summary>
    /// Folds one facility's detector findings for this tick into its standing set and tells the host what moved. An
    /// alert already standing is left as it is (its id is the identity the display keyed on); one the detector no longer
    /// reports is cleared by id. A tick that moves nothing leaves the standing set's reference alone, so a reader
    /// holding it keeps reading the same instance; a facility left with no alert loses its entry.
    /// </summary>
    private void ApplyAsdexDetection(SimScenarioState scenario, string facilityId, IReadOnlyList<AsdexSafetyAlert> detected, IHostConsumers host)
    {
        ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, AsdexSafetyAlert>> all = scenario.ActiveAsdexAlerts;
        ImmutableSortedDictionary<string, AsdexSafetyAlert> active = all.GetValueOrDefault(facilityId) ?? SimScenarioState.NoAsdexAlerts;
        var detectedById = detected.ToDictionary(alert => alert.Id);

        var newAlerts = detected.Where(alert => !active.ContainsKey(alert.Id)).ToList();
        var clearedIds = active.Keys.Where(id => !detectedById.ContainsKey(id)).ToList();

        if ((newAlerts.Count == 0) && (clearedIds.Count == 0))
        {
            return;
        }

        var next = active.ToBuilder();
        foreach (AsdexSafetyAlert alert in newAlerts)
        {
            next[alert.Id] = alert;
            _logger.LogWarning(
                "ASDE-X Safety Logic alert at {FacilityId}: {Kind} {Lines}",
                facilityId,
                alert.Kind,
                string.Join(" / ", alert.MessageLines)
            );
        }

        foreach (string id in clearedIds)
        {
            next.Remove(id);
        }

        scenario.ActiveAsdexAlerts = next.Count > 0 ? all.SetItem(facilityId, next.ToImmutable()) : all.Remove(facilityId);
        host.OnAsdexAlertsChanged(facilityId, newAlerts, clearedIds);
    }

    private static List<AsdexRunwaySurface> BuildAsdexRunwaySurfaces(
        IReadOnlyList<AsdexRunwayConfig> runways,
        NavigationDatabase navDb,
        double fieldElevationFt
    )
    {
        var surfaces = new List<AsdexRunwaySurface>(runways.Count);
        foreach (AsdexRunwayConfig runway in runways)
        {
            if (runway.AreaPoints.Count == 0)
            {
                continue;
            }

            var area = runway.AreaPoints.ToList();
            var centroid = new LatLon(area.Average(point => point.Lat), area.Average(point => point.Lon));
            double variation = MagneticDeclination.GetDeclination(centroid.Lat, centroid.Lon);
            double elevation = ResolveAsdexRunwayElevation(runway.Id, centroid, navDb) ?? fieldElevationFt;
            surfaces.Add(new AsdexRunwaySurface(runway.Id, area, runway.IsClosed, variation, elevation));
        }

        return surfaces;
    }

    /// <summary>The CRC safety-logic config names runways without an airport, so the owning airport is the
    /// nearest one to the footprint; the threshold elevation of the runway end the id names first ("28R" of
    /// "28R-10L") is the AGL datum for arrivals over it.</summary>
    private static double? ResolveAsdexRunwayElevation(string runwayId, LatLon centroid, NavigationDatabase navDb)
    {
        (string Id, double Lat, double Lon)? airport = navDb.FindNearestSizeableAirport(
            centroid,
            minRunwayLengthFt: 0,
            maxRangeNm: AsdexRunwayAirportSearchNm
        );
        return airport is null ? null : navDb.GetRunway(airport.Value.Id, runwayId.Split('-')[0])?.ElevationFt;
    }

    private static List<AsdexTaxiwaySegment> BuildAsdexTaxiwaySegments(AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return [];
        }

        var segments = new List<AsdexTaxiwaySegment>();
        foreach (GroundEdge edge in layout.Edges)
        {
            // Only true taxiways: not runway centerlines, runway-crossing links, or ramps.
            if (edge.IsRunwayCenterline || edge.IsRunwayCrossingLink || edge.IsRamp || (edge.Nodes.Length < 2))
            {
                continue;
            }

            LatLon start = edge.Nodes[0].Position;
            LatLon end = edge.Nodes[^1].Position;
            segments.Add(new AsdexTaxiwaySegment(edge.TaxiwayName, start, end));
        }

        return segments;
    }

    /// <summary>
    /// <c>ASDXALERTS</c> and a recorded <c>EnableAllAlerts</c>: clears every aircraft's ASDE-X alert inhibit. A pure
    /// per-aircraft sweep — there is no room-wide "alerts enabled" bit to hold.
    /// </summary>
    public CommandResult EnableAllAsdexAlerts()
    {
        foreach (AircraftState ac in World.GetSnapshot())
        {
            ac.Stars.AsdexAlertsInhibited = false;
        }

        return new CommandResult(true, "ASDX alerts enabled");
    }

    /// <summary>
    /// Resolves the mutation's aircraft by callsign alone. A surface mutation names a track, and a surface track id is
    /// the callsign: <c>AsdexTrackDto.Id</c> is <c>"CALLSIGN{callsign}"</c>, which the CRC handler strips before it
    /// builds the record (<c>CrcClientState.Asdex.cs</c>, <c>ReadTrackIdArg</c> / <c>ReadAircraftIdArg</c>), and an
    /// edit prefers the DTO's bare callsign field. Nothing on that wire carries a CID or a beacon code, so the
    /// room's FLID-aware resolver has nothing to add here.
    /// </summary>
    private void OnAsdexAircraft(RecordedAsdexMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private void OnSaidAircraft(RecordedSaidMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private static void ApplyAsdexEdit(AircraftState ac, RecordedAsdexMutation mutation)
    {
        Write(ac, AsdexEditField.Callsign, mutation.Callsign);
        Write(ac, AsdexEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, AsdexEditField.Category, mutation.Category);
        Write(ac, AsdexEditField.AircraftType, mutation.AircraftType);
        Write(ac, AsdexEditField.Fix, mutation.Fix);
        Write(ac, AsdexEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, AsdexEditField.Scratchpad2, mutation.Scratchpad2);
    }

    private static void ApplySaidEdit(AircraftState ac, RecordedSaidMutation mutation)
    {
        Write(ac, SaidEditField.Callsign, mutation.Callsign);
        Write(ac, SaidEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, SaidEditField.Category, mutation.Category);
        Write(ac, SaidEditField.AircraftType, mutation.AircraftType);
        Write(ac, SaidEditField.Fix, mutation.Fix);
        Write(ac, SaidEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, SaidEditField.Scratchpad2, mutation.Scratchpad2);
    }

    /// <summary>A field the record does not carry is left alone; one it carries is written as it stands, empty included.</summary>
    private static void Write(AircraftState ac, AsdexEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetAsdexField(ac, field, value);
        }
    }

    private static void Write(AircraftState ac, SaidEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetSaidField(ac, field, value);
        }
    }
}
