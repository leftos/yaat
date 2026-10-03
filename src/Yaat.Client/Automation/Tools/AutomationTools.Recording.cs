using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    private static readonly TimeSpan VideoMapPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Loads the recording through the same method as File &gt; Load Recording after its file pick, and answers once the client has
    /// applied it and rebuilt its terminal: the message is the status line, naming the scenario or the failure.
    /// </summary>
    [AutomationTool("load_recording", "Loads a recording file into the room and waits until the client shows it.", nameof(CannotLoadRecording))]
    public async Task<AppToolOutcome> LoadRecording(
        [Description("The recording's full path: a .yaat-recording.zip/.br/.json or a .yaat-bug-report-bundle.zip.")] string path
    )
    {
        ValidateRecordingPath(path);
        RecordingLoadOutcome outcome = await _viewModel.LoadRecordingFromFileAsync(path);
        return AppToolOutcome.Done(outcome.Status);
    }

    /// <summary>Moves the sim through the same method as the timeline's buttons and scrub, and answers with the status line it ends on.</summary>
    [AutomationTool("seek", "Moves the room's sim to a sim time, as the timeline does, and returns the status line it ends on.", nameof(NoSeek))]
    public async Task<AppToolOutcome> Seek(
        [Description("The sim time to move to, in seconds from the scenario's start; 0 or more.")] double simSeconds
    )
    {
        ValidateSeek("simSeconds", simSeconds);
        RewindOutcome outcome = await _viewModel.RewindToSeconds(simSeconds);
        return AppToolOutcome.Done(outcome.Status);
    }

    /// <summary>
    /// Checks every argument, then loads the recording, waits for its video maps and their saved settings, seeks, pauses, and frames
    /// the primary radar, so a take starts from the same picture every time; the agent starts it with <c>play</c>. A step that fails
    /// after the load began is a <see cref="AppToolOutcome.Done"/> naming the step, as <c>load_recording</c> reports a failed load.
    /// </summary>
    // The flat parameter list is what list_app_tools/call_app_tool binding requires: one scalar argument per parameter, by name.
    [AutomationTool(
        "prepare_take",
        "Loads a recording and frames it for a take: waits for the video maps, seeks, pauses, sets the maps, centre, range, PTL, the "
            + "primary radar's RBLs and sim rate, and returns the framing. Leaves the sim paused; start the take with play.",
        nameof(CannotLoadRecording)
    )]
    public async Task<AppToolOutcome> PrepareTake(
        [Description("The recording's full path: a .yaat-recording.zip/.br/.json or a .yaat-bug-report-bundle.zip.")] string path,
        [Description("The radar centre's latitude in degrees, from -90 to 90.")] double centerLat,
        [Description("The radar centre's longitude in degrees, from -180 to 180.")] double centerLon,
        [Description("The radar range to show, in nm, from 1 to 256.")] double rangeNm,
        [Description("The STARS ids of the video maps to show, comma-separated (e.g. \"5,12\"); every other map is turned off. May be empty.")]
            string videoMaps,
        [Description("The PTL length in minutes, from 0.5 to 3.0 in 0.5 steps; 0 leaves the length as it is and turns all-tracks PTL off.")]
            double ptlMinutes,
        [Description("True to show a PTL on every track; ignored when ptlMinutes is 0.")] bool ptlAll,
        [Description(
            "The primary radar's RBLs, ';'-separated 'from,to' pairs of callsigns, fixes or FRDs (e.g. \"UAL1,AAL9;OAK,SFO\"); they "
                + "replace the primary radar's RBLs. May be empty."
        )]
            string rbls,
        [Description("The sim rate to set, one the room accepts (e.g. 1, 2, 4, 8).")] int simRate,
        [Description("The sim time to seek to, in seconds; 0 stays where the recording loads.")] double seekSeconds
    )
    {
        ValidateTakeRun(path, simRate, seekSeconds);
        var framing = new TakeFraming(
            CenterLat: centerLat,
            CenterLon: centerLon,
            RangeNm: rangeNm,
            VideoMaps: ParseVideoMaps(videoMaps),
            PtlMinutes: ptlMinutes,
            PtlAll: ptlAll,
            Rbls: ParseRbls(rbls)
        );
        ValidateFraming(framing);
        string? stopped = await RunTakeAsync(path, framing, simRate, seekSeconds);
        // The room's pause and rate state reaches the client by a UI-thread post, so report the values asked for, not _state read back.
        return AppToolOutcome.Done(stopped ?? $"Take ready (paused): {FramingJson(simRate, paused: true)}");
    }

    /// <summary>
    /// Sets the primary radar's maps, centre and range, PTL and RBLs, in that order; returns null, or the failed step's
    /// <c>prepare_take</c> answer. An unknown STARS id stops it before any map changes.
    /// </summary>
    public string? FrameTake(TakeFraming framing)
    {
        if (ApplyTakeMaps(framing.VideoMaps) is { } mapError)
        {
            return TakeStopped("set the video maps", mapError);
        }

        CentreRadar(framing.CenterLat, framing.CenterLon, framing.RangeNm);
        RadarViewModel radar = _viewModel.Radar;
        if (framing.PtlMinutes == 0)
        {
            radar.PtlAll = false;
        }
        else
        {
            radar.PtlLengthMinutes = framing.PtlMinutes;
            radar.PtlAll = framing.PtlAll;
        }

        ClearPrimaryRadarRbls();
        foreach ((string from, string to) in framing.Rbls)
        {
            if (_viewModel.PlaceMeasurementFromText(from, to).Error is { } error)
            {
                return TakeStopped($"place the RBL {from},{to}", error);
            }
        }

        return null;
    }

    private async Task<string?> RunTakeAsync(string path, TakeFraming framing, int simRate, double seekSeconds)
    {
        if (await LoadTakeRecordingAsync(path) is { } loadStop)
        {
            return loadStop;
        }

        return await RunTakeStepsAsync(framing, simRate, seekSeconds, SessionTimeout);
    }

    /// <summary>
    /// The <c>prepare_take</c> steps once the recording is loaded: waits up to <paramref name="videoMapWait"/> for the video maps
    /// and their saved settings, seeks, pauses, frames the radar and sets the sim rate; returns null, or the failed step's answer.
    /// </summary>
    public async Task<string?> RunTakeStepsAsync(TakeFraming framing, int simRate, double seekSeconds, TimeSpan videoMapWait)
    {
        if (await WaitForVideoMapsAsync(videoMapWait) is { } mapsStop)
        {
            return mapsStop;
        }

        if (await SeekTakeAsync(seekSeconds) is { } seekStop)
        {
            return seekStop;
        }

        if (await PauseTakeAsync() is { } pauseStop)
        {
            return pauseStop;
        }

        if (FrameTake(framing) is { } frameStop)
        {
            return frameStop;
        }

        return await SetTakeRateAsync(simRate);
    }

    /// <summary>The answer of a <c>prepare_take</c> step that failed: which step, and why.</summary>
    private static string TakeStopped(string step, string reason) => $"prepare_take stopped at '{step}': {reason}";

    private async Task<string?> LoadTakeRecordingAsync(string path)
    {
        RecordingLoadOutcome outcome = await _viewModel.LoadRecordingFromFileAsync(path);
        return outcome.Loaded ? null : TakeStopped("load the recording", outcome.Status);
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the load's video maps and their saved settings, which would overwrite any
    /// framing set sooner.
    /// </summary>
    private async Task<string?> WaitForVideoMapsAsync(TimeSpan wait)
    {
        var waited = Stopwatch.StartNew();
        while (!_viewModel.Radar.VideoMapsReady)
        {
            if (waited.Elapsed >= wait)
            {
                return TakeStopped(
                    "wait for the video maps",
                    string.Create(CultureInfo.InvariantCulture, $"still loading after {wait.TotalSeconds:0.##} s.")
                );
            }

            await Task.Delay(VideoMapPollInterval);
        }

        return null;
    }

    /// <summary>Seeks to <paramref name="seekSeconds"/> when it is above 0.</summary>
    private async Task<string?> SeekTakeAsync(double seekSeconds)
    {
        if (seekSeconds <= 0)
        {
            return null;
        }

        RewindOutcome outcome = await _viewModel.RewindToSeconds(seekSeconds);
        return outcome.Rewound ? null : TakeStopped("seek", outcome.Status);
    }

    /// <summary>Pauses the sim unless it already is, so the room is not asked to pause a paused sim.</summary>
    private async Task<string?> PauseTakeAsync()
    {
        if (_state.IsPaused)
        {
            return null;
        }

        AutomationActionOutcome paused = await _state.PauseAsync();
        return paused.Ok ? null : TakeStopped("pause", paused.Error ?? "no reason given");
    }

    /// <summary>
    /// Turns on exactly the maps in <paramref name="starsIds"/>, each as <c>set_video_map</c> does; returns null, or the ids the
    /// scenario lacks, before changing any map.
    /// </summary>
    private string? ApplyTakeMaps(IReadOnlySet<int> starsIds)
    {
        RadarViewModel radar = _viewModel.Radar;
        int[] missing = [.. starsIds.Where(starsId => radar.FindToggleByStarsId(starsId) is null).Order()];
        if (missing.Length > 0)
        {
            return $"no video map with STARS id {string.Join(", ", missing)} in this scenario.";
        }

        foreach (VideoMapToggleItem toggle in radar.MapToggles.ToList())
        {
            bool enabled = starsIds.Contains(toggle.StarsId);
            if (toggle.IsEnabled != enabled)
            {
                toggle.IsEnabled = enabled;
                radar.SyncShortcutState(toggle.StarsId, enabled);
            }
        }

        return null;
    }

    private async Task<string?> SetTakeRateAsync(int simRate)
    {
        AutomationActionOutcome outcome = await _state.SetRateAsync(simRate);
        return outcome.Ok ? null : TakeStopped("set the sim rate", outcome.Error ?? "no reason given");
    }

    private static void ValidateRecordingPath(string path)
    {
        if (!File.Exists(path))
        {
            throw new AppToolArgumentException("path", $"No recording file at '{path}'; give the full path of an existing file.");
        }
    }

    private static void ValidateSeek(string parameter, double simSeconds)
    {
        if (simSeconds < 0)
        {
            throw new AppToolArgumentException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"'{parameter}' must be 0 or more seconds, not {simSeconds}.")
            );
        }
    }

    /// <summary>Checks the <c>prepare_take</c> arguments that are not part of the framing.</summary>
    private static void ValidateTakeRun(string path, int simRate, double seekSeconds)
    {
        ValidateRecordingPath(path);
        if (simRate <= 0)
        {
            throw new AppToolArgumentException("simRate", $"'simRate' must be more than 0, not {simRate}.");
        }

        ValidateSeek("seekSeconds", seekSeconds);
    }

    /// <summary>Checks the framing's numbers; the map and RBL lists were checked as they were parsed.</summary>
    private static void ValidateFraming(TakeFraming framing)
    {
        ValidateLatitude("centerLat", framing.CenterLat);
        ValidateLongitude("centerLon", framing.CenterLon);
        ValidateRange(framing.RangeNm);
        if (framing.PtlMinutes != 0)
        {
            ValidatePtlLength("ptlMinutes", framing.PtlMinutes);
        }
    }

    private static HashSet<int> ParseVideoMaps(string videoMaps)
    {
        HashSet<int> starsIds = [];
        foreach (string item in videoMaps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int starsId) || (starsId <= 0))
            {
                throw new AppToolArgumentException(
                    "videoMaps",
                    $"'videoMaps' must be comma-separated STARS ids such as \"5,12\", or empty; '{item}' is not a STARS id."
                );
            }

            starsIds.Add(starsId);
        }

        return starsIds;
    }

    private static List<(string From, string To)> ParseRbls(string rbls)
    {
        List<(string From, string To)> lines = [];
        foreach (string pair in rbls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ends = pair.Split(',', StringSplitOptions.TrimEntries);
            if ((ends.Length != 2) || (ends[0].Length == 0) || (ends[1].Length == 0))
            {
                throw new AppToolArgumentException(
                    "rbls",
                    $"'rbls' must be ';'-separated 'from,to' pairs such as \"UAL1,AAL9;OAK,SFO\", or empty; '{pair}' is not a pair."
                );
            }

            lines.Add((ends[0], ends[1]));
        }

        if (lines.Count > RangeBearingLineStore.MaxLines)
        {
            throw new AppToolArgumentException("rbls", $"'rbls' names {lines.Count} RBLs; the radar holds at most {RangeBearingLineStore.MaxLines}.");
        }

        return lines;
    }
}

/// <summary>The primary radar's picture for a take, as <c>prepare_take</c> sets it once the recording is loaded and paused.</summary>
/// <param name="CenterLat">The radar centre's latitude in degrees.</param>
/// <param name="CenterLon">The radar centre's longitude in degrees.</param>
/// <param name="RangeNm">The radar range in nm.</param>
/// <param name="VideoMaps">The STARS ids of the maps to show; every other map is turned off.</param>
/// <param name="PtlMinutes">The PTL length in minutes, or 0 to leave the length and turn all-tracks PTL off.</param>
/// <param name="PtlAll">Whether every track shows a PTL; ignored when <paramref name="PtlMinutes"/> is 0.</param>
/// <param name="Rbls">The RBLs that replace the primary radar's, each a pair of callsigns, fixes or FRDs.</param>
public sealed record TakeFraming(
    double CenterLat,
    double CenterLon,
    double RangeNm,
    IReadOnlySet<int> VideoMaps,
    double PtlMinutes,
    bool PtlAll,
    IReadOnlyList<(string From, string To)> Rbls
);
