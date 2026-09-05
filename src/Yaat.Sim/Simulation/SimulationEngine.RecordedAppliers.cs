using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

// The bodies the router's spawn arms and derived-record appliers call: queued-spawn control, and the
// setting / weather / generator appliers a recorded change replays through.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// <c>SPAWN</c>: pulls a still-queued delayed spawn into the world now and dispatches its presets. Returns the
    /// spawned aircraft, or null when nothing by that callsign is queued.
    /// </summary>
    internal AircraftState? SpawnNow(string callsign)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return null;
        }

        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        scenario.DelayedQueue.Remove(entry);
        entry.Aircraft.State.SpawnedAtSeconds = scenario.ElapsedSeconds;
        World.AddAircraft(entry.Aircraft.State);
        DispatchPresetCommands(entry.Aircraft);
        return entry.Aircraft.State;
    }

    /// <summary>
    /// <c>SPAWNDELAY</c>: re-times a still-queued delayed spawn to <paramref name="seconds"/> from now. False when nothing
    /// by that callsign is queued.
    /// </summary>
    internal bool SpawnDelay(string callsign, int seconds)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return false;
        }

        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        entry.SpawnAtSeconds = (int)scenario.ElapsedSeconds + seconds;
        return true;
    }

    /// <summary>A recorded weather load (JSON present) or clear (JSON null), including the METAR re-issuance intent it carried.</summary>
    internal void ApplyRecordedWeatherChange(RecordedWeatherChange weather)
    {
        if (weather.WeatherJson is not null)
        {
            ApplyWeatherJson(weather.WeatherJson);
            if (Scenario is not null)
            {
                Scenario.MetarReissuanceEnabled = weather.ReconstructMetars;
            }

            return;
        }

        World.Weather = null;
        if (Scenario is not null)
        {
            Scenario.WeatherTimeline = null;
            Scenario.WeatherSourceJson = null;
            Scenario.MetarReissuanceEnabled = false;
        }
    }

    internal void ApplySettingChange(RecordedSettingChange setting)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        // Mirrors yaat-server's SimControlService recorders. Every setting the
        // server records mid-session must round-trip through replay so bundle
        // playback (and snapshot regeneration at export time) matches what the
        // user actually saw live.
        switch (setting.Setting)
        {
            case "AutoClearedToLand":
                if (bool.TryParse(setting.Value, out var ctl))
                {
                    scenario.AutoClearedToLand = ctl;
                }
                break;
            case "AutoCrossRunway":
                if (bool.TryParse(setting.Value, out var acr))
                {
                    scenario.AutoCrossRunway = acr;
                    World.ApplyAutoCrossToActiveTaxiRoutes(acr);
                }
                break;
            case "AutoPullUpToParallel":
                // Only affects future landing exits — no active-route walk needed.
                if (bool.TryParse(setting.Value, out var apup))
                {
                    scenario.AutoPullUpToParallel = apup;
                }
                break;
            case "AutoGoAroundOnOccupiedRunway":
                if (bool.TryParse(setting.Value, out var agor))
                {
                    scenario.AutoGoAroundOnOccupiedRunway = agor;
                }
                break;
            case "AutoRejectTakeoffOnOccupiedRunway":
                if (bool.TryParse(setting.Value, out var arto))
                {
                    scenario.AutoRejectTakeoffOnOccupiedRunway = arto;
                }
                break;
            case "LiveTrafficEnabled":
                if (bool.TryParse(setting.Value, out var live))
                {
                    scenario.LiveTrafficEnabled = live;
                }
                break;
            case "LiveTrafficCeilingFt":
                if (int.TryParse(setting.Value, out var ceiling))
                {
                    scenario.LiveTrafficCeilingFt = ceiling;
                }
                break;
            case "LiveTrafficFilter":
                scenario.LiveTrafficFilter = setting.Value ?? "";
                break;
            case "LiveTrafficFeedTimeUtc":
                // Diagnostic: where the room stood in the feed. Replay is driven by the recorded samples themselves.
                break;
            case "AutoAcceptDelay":
                if (int.TryParse(setting.Value, out var seconds))
                {
                    scenario.AutoAcceptDelay = seconds < 0 ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
                }
                break;
            case "CommandRunDelayMinSeconds":
                if (int.TryParse(setting.Value, out var crdMin))
                {
                    scenario.CommandRunDelayMinSeconds = Math.Clamp(crdMin, 0, 60);
                }
                break;
            case "CommandRunDelayMaxSeconds":
                if (int.TryParse(setting.Value, out var crdMax))
                {
                    scenario.CommandRunDelayMaxSeconds = Math.Clamp(crdMax, 0, 60);
                }
                break;
            case "AutoDeleteMode":
                // Server writes ClientAutoDeleteOverride, not ScenarioAutoDeleteMode.
                // Null/empty string is a valid value: it means "clear the override and
                // fall back to the scenario default".
                scenario.ClientAutoDeleteOverride = string.IsNullOrEmpty(setting.Value) ? null : setting.Value;
                break;
            case "ValidateDctFixes":
                if (bool.TryParse(setting.Value, out var validate))
                {
                    scenario.ValidateDctFixes = validate;
                }
                break;
            case "SoloTrainingMode":
                if (bool.TryParse(setting.Value, out var soloTrainingMode))
                {
                    scenario.SoloTrainingMode = soloTrainingMode;
                }
                break;
            case "SoloParkingInitialCallupRatePercent":
                if (int.TryParse(setting.Value, out var parkingRate))
                {
                    ApplySoloPacingRates(
                        parkingRate,
                        scenario.SoloArrivalGeneratorRatePercent,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloArrivalGeneratorRatePercent":
                if (int.TryParse(setting.Value, out var arrivalRate))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        arrivalRate,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloGoAroundProbabilityPercent":
                if (int.TryParse(setting.Value, out var goAroundPct))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        scenario.SoloArrivalGeneratorRatePercent,
                        goAroundPct,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "RpoShowPilotSpeech":
                if (bool.TryParse(setting.Value, out var rpoShowPilotSpeech))
                {
                    scenario.RpoShowPilotSpeech = rpoShowPilotSpeech;
                }
                break;
        }
    }

    /// <summary>
    /// Replaces every generator on the live scenario from a <see cref="GeneratorsPayload"/> JSON document.
    /// A generator whose id survives the edit keeps its spawn cadence and activation, so toggling one row
    /// does not re-phase the rest of the traffic; a newly added generator starts one interval from now.
    /// Already-spawned aircraft keep flying. The swap is best-effort per generator: entries with
    /// unresolvable runways are dropped and reported in the returned warnings.
    /// </summary>
    public List<string> ApplyGeneratorsJson(string generatorsJson)
    {
        var warnings = new List<string>();
        var scenario = Scenario;
        if (scenario is null)
        {
            warnings.Add("No active scenario");
            return warnings;
        }

        GeneratorsPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GeneratorsPayload>(generatorsJson);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Invalid generators JSON: {ex.Message}");
            return warnings;
        }

        if (payload is null)
        {
            warnings.Add("Generators JSON deserialized to null");
            return warnings;
        }

        var priorCadence = scenario
            .Generators.Cast<IGeneratorRuntimeState>()
            .Concat(scenario.VfrArrivalGenerators)
            .Concat(scenario.OverflightGenerators)
            .GroupBy(g => g.ConfigBase.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().NextSpawnSeconds, g.First().WasActive), StringComparer.Ordinal);

        var navDb = NavigationDatabase.Instance;
        var airportId = scenario.PrimaryAirportId ?? "";

        var newArrivals = new List<GeneratorState>();
        foreach (var cfg in payload.AircraftGenerators)
        {
            var runwayId = cfg.Runway ?? "";
            var runway = navDb.GetRunway(airportId, runwayId);
            if (runway is null)
            {
                warnings.Add($"Generator '{cfg.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found at {airportId}");
                continue;
            }

            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: true);
            newArrivals.Add(
                new GeneratorState
                {
                    Config = cfg,
                    Runway = runway,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        var newVfrArrivals = new List<VfrArrivalGeneratorState>();
        foreach (var cfg in payload.VfrArrivalGenerators)
        {
            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: true);
            newVfrArrivals.Add(
                new VfrArrivalGeneratorState
                {
                    Config = cfg,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        var newOverflights = new List<OverflightGeneratorState>();
        foreach (var cfg in payload.OverflightGenerators)
        {
            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: false);
            newOverflights.Add(
                new OverflightGeneratorState
                {
                    Config = cfg,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        scenario.Generators.Clear();
        scenario.Generators.AddRange(newArrivals);
        scenario.VfrArrivalGenerators.Clear();
        scenario.VfrArrivalGenerators.AddRange(newVfrArrivals);
        scenario.OverflightGenerators.Clear();
        scenario.OverflightGenerators.AddRange(newOverflights);
        return warnings;

        (double NextSpawnSeconds, bool WasActive) ResumeCadence(IGeneratorConfig cfg, bool scaledByArrivalRate)
        {
            if (priorCadence.TryGetValue(cfg.Id, out var prior))
            {
                return prior;
            }

            var interval = scaledByArrivalRate
                ? ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(cfg.IntervalTime, scenario.SoloArrivalGeneratorRatePercent)
                : cfg.IntervalTime;
            return (scenario.ElapsedSeconds + interval, false);
        }
    }

    internal void ApplyWeatherJson(string weatherJson)
    {
        var parseResult = WeatherTimelineParser.Parse(weatherJson);
        if (parseResult.IsTimeline)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = parseResult.Timeline;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Timeline!.GetWeatherAt(Scenario?.ElapsedSeconds ?? 0);
        }
        else if (parseResult.IsProfile)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = null;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Profile;
        }
    }
}
