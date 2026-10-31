using System.Collections.Immutable;
using Yaat.Sim;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Test convenience factory for <see cref="DispatchContext"/>. Production code
/// constructs the record explicitly (no optional params) so the compiler enforces
/// wiring when new context fields are added. Tests use this factory to stay
/// concise — they rarely care about the ground layout, weather, or aircraft lookup.
/// </summary>
internal static class TestDispatch
{
    /// <summary>A taxi dispatch with no world and no auto-cross: no other aircraft in view, no active runways, crossings left to a CROSS.</summary>
    public static GroundCommandHandler.TaxiDispatch EmptyTaxi { get; } =
        new(AutoCrossRunwayMode.Off, ActiveRunways.Empty, IsScenarioScripted: false, ResultReachesController: true, ListAircraft: null);

    public static DispatchContext Context(
        Random rng,
        bool validateDctFixes = true,
        AirportGroundLayout? groundLayout = null,
        AutoCrossRunwayMode autoCrossRunwayMode = AutoCrossRunwayMode.Off,
        WeatherProfile? weather = null,
        Func<string, AircraftState?>? findAircraft = null,
        Func<IReadOnlyList<AircraftState>>? listAircraft = null,
        bool soloTrainingMode = false,
        bool rpoShowPilotSpeech = false,
        Action<TerminalEntry>? terminalEmitter = null,
        ArtccConfigRoot? artccConfig = null,
        double scenarioElapsedSeconds = 0,
        DateTime? sessionStartUtc = null,
        bool preserveConditionals = false,
        bool isScenarioScripted = false,
        ToweredAirports? toweredAirports = null,
        ActiveRunways? activeRunways = null,
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig>? asdexSafetyLogicConfigs = null
    ) =>
        new()
        {
            GroundLayout = groundLayout,
            Rng = rng,
            Weather = weather,
            FindAircraft = findAircraft,
            ListAircraft = listAircraft,
            ValidateDctFixes = validateDctFixes,
            AutoCrossRunwayMode = autoCrossRunwayMode,
            SoloTrainingMode = soloTrainingMode,
            SoloRpoCommandsAllowed = false,
            RpoShowPilotSpeech = rpoShowPilotSpeech,
            TerminalEmitter = terminalEmitter,
            ArtccConfig = artccConfig,
            AsdexSafetyLogicConfigs = asdexSafetyLogicConfigs ?? AsdexSafetyLogicSeed.Empty,
            ToweredAirports = toweredAirports ?? ToweredAirports.Empty,
            ActiveRunways = activeRunways ?? ActiveRunways.Empty,
            ScenarioElapsedSeconds = scenarioElapsedSeconds,
            SessionStartUtc = sessionStartUtc ?? SimScenarioState.ProcessDayUtc,
            PreserveConditionals = preserveConditionals,
            IsScenarioScripted = isScenarioScripted,
            FacilityHint = null,
            LayoutForAirport = null,
            SpeechContextFor = _ => PilotSpeechContext.Canonical,
        };
}
