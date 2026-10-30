using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
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

    /// <summary>
    /// The post-physics ASDE-X Safety Logic pass. It clears facilities with no configuration and runway-less
    /// configurations first, then runs the detector per runway facility in ordinal order: the facility's configured
    /// runways as navdata resolves them, with their cached axes and current footprints (plus the ground layout's taxiways
    /// at the layout's own airport), go to the detector with the facility's opposite-direction pair memory, whether or not
    /// a layout is loaded, with <c>closedRunwayAlertsOnly</c> decided each tick by whether the facility's runway
    /// configuration id is one of its configurations (<see cref="AsdexSafetyLogicSeed.NamesRunwayConfiguration"/>), and what
    /// it finds is diffed against the alerts that facility is already holding; the memory it returns replaces the old one.
    /// With no navigation data the runway facilities keep their standing alerts and their memory.
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
        List<string> unconfigured =
        [
            .. scenario
                .ActiveAsdexAlerts.Keys.Union(scenario.AsdexOppDirPairs.Keys, StringComparer.Ordinal)
                .Where(id => !configs.ContainsKey(id))
                .Order(StringComparer.Ordinal),
        ];
        foreach (string facilityId in unconfigured)
        {
            ApplyAsdexDetection(scenario, facilityId, AsdexDetection.None, host);
        }

        foreach (string facilityId in configs.Where(entry => entry.Value.Runways.Count == 0).Select(entry => entry.Key))
        {
            ApplyAsdexDetection(scenario, facilityId, AsdexDetection.None, host);
        }

        if (NavigationDatabase.InstanceOrNull is not { } navDb)
        {
            return;
        }

        foreach (string facilityId in _asdexRunways.Keys.Where(id => !configs.ContainsKey(id)).ToList())
        {
            _asdexRunways.Remove(facilityId);
        }

        List<AircraftState>? aircraft = null;
        foreach ((string facilityId, AsdexSafetyLogicConfig config) in configs.Where(entry => entry.Value.Runways.Count > 0))
        {
            aircraft ??= World.GetSnapshot();
            double fieldElevationFt = FacilityFieldElevation(facilityId, navDb);
            AsdexFacilityRunways runways = CurrentAsdexRunways(facilityId, config, navDb);
            IReadOnlyList<AsdexTaxiwaySegment> taxiways = AsdexConfiguredTaxiways.Filter(
                IsLayoutAirport(World.GroundLayout, facilityId) ? BuildAsdexTaxiwaySegments(World.GroundLayout) : [],
                navDb.AirportSidecars.GetAsdexLandingTaxiways(facilityId)
            );

            ApplyAsdexDetection(
                scenario,
                facilityId,
                AsdexSafetyLogicDetector.Detect(
                    runways.Alerting,
                    taxiways,
                    aircraft,
                    fieldElevationFt,
                    new AsdexDetectionInputs(
                        ClosedRunwayAlertsOnly: !AsdexSafetyLogicSeed.NamesRunwayConfiguration(
                            scenario.ArtccConfig,
                            facilityId,
                            config.RunwayConfigurationId
                        ),
                        OppDirPairs: scenario.AsdexOppDirPairs.GetValueOrDefault(facilityId) ?? AsdexDetection.NoOppDirPairs
                    )
                ),
                host
            );
        }
    }

    /// <summary>
    /// One facility's hold bars: the layout and the runway resolution they were built from, the fixed set itself and its ids,
    /// and the bars last reported Active to the host. Immutable, so a reader off the tick thread takes the reference once.
    /// </summary>
    private sealed record AsdexHoldBarFacility(
        AirportGroundLayout Layout,
        AsdexRunwayResolution Resolution,
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
    /// facility whose airport is the loaded ground layout; the set is built from the runway cache the alerts read too, once
    /// per facility, layout and runway resolution, so a configuration that changes the runway list switches the bars and the
    /// alerts in the same tick. Each tick the
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

            AsdexFacilityRunways runways = CurrentAsdexRunways(facilityId, config, navDb);
            AsdexHoldBarFacility facility = CurrentHoldBarFacility(facilityId, runways, layout);
            aircraft ??= World.GetSnapshot();
            IReadOnlySet<string> active = AsdexHoldBarStatusEvaluator.ActiveBarIds(
                runways.Runways,
                runways.Resolution.Axes,
                facility.Bars,
                aircraft,
                FacilityFieldElevation(facilityId, navDb)
            );
            ApplyHoldBarStatuses(host, facilityId, facility, active);
        }
    }

    /// <summary>
    /// The facility's set for <paramref name="layout"/> and the current runway resolution: the one already held, or a fresh
    /// build (a new layout, or a configuration naming other runways) that keeps the statuses last reported, so bars a rebuilt
    /// set no longer has are still reported Suppressed.
    /// </summary>
    private AsdexHoldBarFacility CurrentHoldBarFacility(string facilityId, AsdexFacilityRunways runways, AirportGroundLayout layout)
    {
        AsdexHoldBarFacility? held = _asdexHoldBars.GetValueOrDefault(facilityId);
        if ((held is not null) && ReferenceEquals(held.Layout, layout) && ReferenceEquals(held.Resolution, runways.Resolution))
        {
            return held;
        }

        AsdexHoldBarFacility built = BuildHoldBarFacility(runways, layout) with { ReportedActive = held?.ReportedActive ?? NoActiveHoldBars };
        _asdexHoldBars = _asdexHoldBars.SetItem(facilityId, built);
        _logger.LogDebug(
            "ASDE-X hold bars for {FacilityId}: {Count} built from the {Airport} layout",
            facilityId,
            built.Bars.Count,
            layout.AirportId
        );
        return built;
    }

    private static AsdexHoldBarFacility BuildHoldBarFacility(AsdexFacilityRunways runways, AirportGroundLayout layout)
    {
        IReadOnlyList<AsdexHoldBar> bars = AsdexHoldBarBuilder.Build(runways.Runways, layout);
        return new AsdexHoldBarFacility(
            layout,
            runways.Resolution,
            bars,
            bars.Select(bar => bar.Id).ToFrozenSet(StringComparer.Ordinal),
            NoActiveHoldBars
        );
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
            AsdexFacilityRunways runways = ResolveAsdexRunways(facilityId, config, navDb);
            return [.. BuildHoldBarFacility(runways, layout).Bars.Select(bar => new AsdexHoldBarState(bar, IsActive: false))];
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
                    dto.GoAround,
                    dto.RunwayEnds is { } ends ? [.. ends] : null
                );
            }

            restored[facility.Key] = alerts.ToImmutable();
        }

        return restored.ToImmutable();
    }

    /// <summary>The snapshot's opposite-direction pair memory, grouped by facility; an empty or missing list restores none.</summary>
    private static ImmutableSortedDictionary<string, ImmutableSortedSet<string>> RestoreAsdexOppDirPairs(List<AsdexOppDirPairDto>? pairDtos)
    {
        ImmutableSortedDictionary<string, ImmutableSortedSet<string>>.Builder restored = ImmutableSortedDictionary.CreateBuilder<
            string,
            ImmutableSortedSet<string>
        >(StringComparer.Ordinal);
        foreach (IGrouping<string, AsdexOppDirPairDto> facility in (pairDtos ?? []).GroupBy(dto => dto.FacilityId, StringComparer.Ordinal))
        {
            restored[facility.Key] = facility.Select(dto => dto.PairKey).ToImmutableSortedSet(StringComparer.Ordinal);
        }

        return restored.ToImmutable();
    }

    /// <summary>
    /// Folds one facility's detector findings for this tick into its standing set and tells the host what moved. An
    /// alert already standing is left as it is (its id is the identity the display keyed on); one the detector no longer
    /// reports is cleared by id. A tick that moves nothing leaves the standing set's reference alone, so a reader
    /// holding it keeps reading the same instance; a facility left with no alert loses its entry. The detection's pair
    /// memory is written first, whether or not any alert moved (a pair forgotten while no alert stands is a change too).
    /// </summary>
    private void ApplyAsdexDetection(SimScenarioState scenario, string facilityId, AsdexDetection detection, IHostConsumers host)
    {
        ImmutableSortedDictionary<string, ImmutableSortedSet<string>> allPairs = scenario.AsdexOppDirPairs;
        ImmutableSortedSet<string> pairs = allPairs.GetValueOrDefault(facilityId) ?? AsdexDetection.NoOppDirPairs;
        if (!pairs.SetEquals(detection.OppDirPairs))
        {
            scenario.AsdexOppDirPairs =
                (detection.OppDirPairs.Count > 0) ? allPairs.SetItem(facilityId, detection.OppDirPairs) : allPairs.Remove(facilityId);
        }

        IReadOnlyList<AsdexSafetyAlert> detected = detection.Alerts;
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

    /// <summary>
    /// One facility's safety-logic runways resolved against navigation data: the navigation data and the configuration's runway
    /// ids they were resolved from, and for each runway navdata resolves (in configuration order) the index of its own entry in
    /// the configuration and its axis. Built once and kept until the ids or the navigation data change; the hold bars are
    /// rebuilt when it is.
    /// </summary>
    private sealed record AsdexRunwayResolution(
        NavigationDatabase NavDb,
        IReadOnlyList<string> Ids,
        IReadOnlyList<int> ConfigIndices,
        IReadOnlyList<AsdexRunwayAxis> Axes
    );

    /// <summary>
    /// One facility's safety-logic runways as the hold bars and the alerts both read them: the resolution, each resolved runway
    /// with the footprint its configuration entry carries now, and the detector's input (those runways with the entries'
    /// closed states, and every entry's footprint, resolved or not).
    /// </summary>
    private sealed record AsdexFacilityRunways(
        AsdexRunwayResolution Resolution,
        IReadOnlyList<AsdexHoldBarRunway> Runways,
        AsdexSafetyLogicRunways Alerting
    );

    /// <summary>
    /// Each facility's safety-logic runways, read by <see cref="TickAsdexAlerts"/> and <see cref="TickAsdexHoldBars"/> alike.
    /// Transient engine state, never snapshotted: everything in it is derived from the configuration and navigation data, so a
    /// restore or a replay rebuilds the same runways and axes.
    /// </summary>
    private readonly Dictionary<string, AsdexFacilityRunways> _asdexRunways = new(StringComparer.Ordinal);

    /// <summary>
    /// The facility's safety-logic runways with the footprints and closed states its configuration carries now: the cached
    /// resolution while the runway ids and navigation data are unchanged, a fresh one otherwise.
    /// </summary>
    private AsdexFacilityRunways CurrentAsdexRunways(string facilityId, AsdexSafetyLogicConfig config, NavigationDatabase navDb)
    {
        if (
            !_asdexRunways.TryGetValue(facilityId, out AsdexFacilityRunways? held)
            || !ReferenceEquals(held.Resolution.NavDb, navDb)
            || !held.Resolution.Ids.SequenceEqual(config.Runways.Select(runway => runway.Id), StringComparer.Ordinal)
        )
        {
            held = ResolveAsdexRunways(facilityId, config, navDb);
            _asdexRunways[facilityId] = held;
            return held;
        }

        if (HasNewFootprintsOrClosedStates(held, config))
        {
            held = FromConfiguration(held.Resolution, held.Runways, config);
            _asdexRunways[facilityId] = held;
        }

        return held;
    }

    /// <summary>The facility's safety-logic runways resolved afresh against <paramref name="navDb"/>, nothing kept.</summary>
    private static AsdexFacilityRunways ResolveAsdexRunways(string facilityId, AsdexSafetyLogicConfig config, NavigationDatabase navDb)
    {
        IReadOnlyList<RunwayInfo> navRunways = AsdexSafetyLogicSeed.FacilityRunways(facilityId, navDb);
        var indices = new List<int>(config.Runways.Count);
        var resolved = new List<AsdexHoldBarRunway>(config.Runways.Count);
        var axes = new List<AsdexRunwayAxis>(config.Runways.Count);
        for (int j = 0; j < config.Runways.Count; j++)
        {
            if (AsdexHoldBarBuilder.ResolveRunway(facilityId, config.Runways[j], navRunways) is { } runway)
            {
                indices.Add(j);
                resolved.Add(runway);
                axes.Add(new AsdexRunwayAxis(runway.Runway));
            }
        }

        var resolution = new AsdexRunwayResolution(navDb, [.. config.Runways.Select(runway => runway.Id)], indices, axes);
        return FromConfiguration(resolution, resolved, config);
    }

    /// <summary>True when an entry of <paramref name="config"/> carries another footprint or closed state than <paramref name="held"/>.</summary>
    private static bool HasNewFootprintsOrClosedStates(AsdexFacilityRunways held, AsdexSafetyLogicConfig config)
    {
        for (int j = 0; j < config.Runways.Count; j++)
        {
            if (!ReferenceEquals(config.Runways[j].AreaPoints, held.Alerting.Footprints[j]))
            {
                return true;
            }
        }

        for (int i = 0; i < held.Runways.Count; i++)
        {
            if (config.Runways[held.Resolution.ConfigIndices[i]].IsClosed != held.Alerting.Surfaces[i].IsClosed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The resolved runways with the footprint and closed state of each one's own configuration entry (by index, so two
    /// entries with one id each keep their own), and every entry's footprint.
    /// </summary>
    private static AsdexFacilityRunways FromConfiguration(
        AsdexRunwayResolution resolution,
        IReadOnlyList<AsdexHoldBarRunway> resolved,
        AsdexSafetyLogicConfig config
    )
    {
        var runways = new List<AsdexHoldBarRunway>(resolved.Count);
        var surfaces = new List<AsdexRunwaySurface>(resolved.Count);
        for (int i = 0; i < resolved.Count; i++)
        {
            AsdexRunwayConfig entry = config.Runways[resolution.ConfigIndices[i]];
            AsdexHoldBarRunway runway = resolved[i] with { Area = entry.AreaPoints };
            runways.Add(runway);
            surfaces.Add(new AsdexRunwaySurface(runway, resolution.Axes[i], entry.IsClosed));
        }

        List<IReadOnlyList<LatLon>> footprints = [.. config.Runways.Select(entry => entry.AreaPoints)];
        return new AsdexFacilityRunways(resolution, runways, new AsdexSafetyLogicRunways(surfaces, footprints));
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
