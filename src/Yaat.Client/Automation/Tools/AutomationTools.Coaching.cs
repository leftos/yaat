using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Automation.Tools;

/// <summary>
/// The tools a student agent scans the traffic, the radio and the airport with as data rather than from screenshots, and the one
/// that frames the primary ground view. Every answer is one JSON object whose absent or empty fields are left out.
/// </summary>
public sealed partial class AutomationTools
{
    /// <summary>The most entries one <c>read_terminal</c> answer carries; the rest wait for the next call.</summary>
    private const int MaxTerminalEntries = 200;

    private static readonly JsonSerializerOptions CoachingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [AutomationTool(
        "get_traffic",
        "Lists the spawned aircraft the Aircraft List shows (a delayed spawn appears once it joins the sim) as one JSON object: sim "
            + "time, pause state and one row per aircraft, ordered by "
            + "callsign (type, rules, route ends, position, motion, phase, ground state, runway queue, pending pilot request). Fields "
            + "with no value are left out; activeOnly true leaves out the aircraft standing quiet on the ground.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> GetTraffic(
        [Description("Airport id such as OAK or KOAK; empty for every aircraft.")] string airport,
        [Description("True to leave out aircraft standing quiet on the ground: stopped, never in contact and with no pending request.")]
            bool activeOnly
    )
    {
        string? wanted = string.IsNullOrWhiteSpace(airport) ? null : NormalizeAirport(airport);
        LiveTrafficListFilter liveFilter = _viewModel.LiveTrafficListFilter;
        TrafficRow[] rows =
        [
            .. _viewModel
                .Aircraft.Where(aircraft => MainViewModel.IsAircraftVisible(aircraft, showOnlyActive: true, filter: "", liveFilter))
                .Where(aircraft => (wanted is null) || ServesAirport(aircraft, wanted))
                .Where(aircraft => (!activeOnly) || (!IsQuietOnTheGround(aircraft)))
                .OrderBy(aircraft => aircraft.Callsign, StringComparer.Ordinal)
                .Select(BuildRow),
        ];
        TrafficAnswer answer = new(Math.Round(_state.ScenarioElapsedSeconds, 1), _state.IsPaused, rows);
        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(answer, CoachingJsonOptions)));
    }

    [AutomationTool(
        "get_aircraft",
        "Reports one aircraft as one JSON object: every get_traffic row field, plus its route, cruise altitude, remarks, squawks, "
            + "speeds, assignments, clearances, exits ahead, phase sequence, ownership, scratchpads and armed reports.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> GetAircraft([Description("The aircraft's callsign, matched case-insensitively.")] string callsign)
    {
        if (FindAircraft(callsign) is not { } aircraft)
        {
            return Task.FromResult(NoAircraft(callsign));
        }

        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(BuildDetail(aircraft), CoachingJsonOptions)));
    }

    [AutomationTool(
        "read_terminal",
        "Reads the terminal's lines after a cursor as one JSON object: the cursor to pass next, whether more lines wait, and up to "
            + "200 lines oldest first (sequence, sim seconds, kind, pilot transmission kind, callsign, initials, text). Lines rebuilt "
            + "from a loaded recording are skipped.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> ReadTerminal(
        [Description("Return entries with a sequence above this; 0 for every entry still in the log.")] int sinceSequence
    )
    {
        TerminalEntry[] all = [.. _state.TerminalEntriesSince(sinceSequence).Where(entry => !entry.IsHistory)];
        TerminalEntry[] returned = [.. all.Take(MaxTerminalEntries)];
        bool more = all.Length > returned.Length;
        long cursor = more ? returned[^1].Sequence : _state.TerminalCursor;
        TerminalAnswer answer = new(cursor, more ? true : null, [.. returned.Select(BuildTerminalLine)]);
        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(answer, CoachingJsonOptions)));
    }

    [AutomationTool(
        "get_airport_context",
        "Reports one airport's setting as one JSON object: its METAR, wind, altimeter and ceiling, the runways in use, the "
            + "position the controller works, the solo-training settings and the sim clock.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> GetAirportContext([Description("Airport id such as OAK or KOAK.")] string airport)
    {
        if (string.IsNullOrWhiteSpace(airport))
        {
            throw new AppToolArgumentException("airport", "'airport' must name an airport, such as OAK or KOAK.");
        }

        string id = airport.Trim().ToUpperInvariant();
        string wanted = NormalizeAirport(id);
        WeatherDisplayInfo? weather = _viewModel.GroundWeatherFor(id);
        AirportContext context = new()
        {
            Airport = id,
            Metar = _viewModel.Metars.FirstOrDefault(metar => AirportMatches(metar.StationId, wanted))?.Raw,
            Wind = weather is null
                ? null
                : new ContextWind(weather.WindDirectionDeg, weather.WindSpeedKts, weather.WindGustKts, weather.WindVarFromDeg, weather.WindVarToDeg),
            Altimeter = weather?.AltimeterInHg,
            Ceiling = weather?.CeilingFeetAgl,
            RunwaysInUse = _viewModel.RoomActiveRunways.FirstOrDefault(entry => AirportMatches(entry.Key, wanted)).Value,
            Position = BuildPosition(_viewModel.ActivePosition),
            Solo = new ContextSolo(
                _viewModel.SessionSoloTrainingMode,
                _viewModel.SessionSoloParkingInitialCallupRatePercent,
                _viewModel.SessionSoloParkingInitialCallupIntervalSeconds,
                _viewModel.SessionSoloArrivalGeneratorRatePercent,
                _viewModel.SessionSoloGoAroundProbabilityPercent
            ),
            Sim = new ContextSim(Math.Round(_state.ScenarioElapsedSeconds, 1), _state.SimRate, _state.IsPaused),
        };
        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(context, CoachingJsonOptions)));
    }

    /// <summary>
    /// Resolves the target as an aircraft first, then as the <c>.rbl</c> command resolves a fix endpoint (<see cref="FrdResolver"/>
    /// over the loaded navigation data).
    /// </summary>
    [AutomationTool(
        "center_ground",
        "Centres the primary ground view on an aircraft, fix or airport and answers the view's centre and zoom as one JSON object. The "
            + "zoom is the ground canvas's viewport zoom factor: larger is closer.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> CenterGround(
        [Description("An aircraft's callsign (matched case-insensitively), else a fix or airport identifier such as OAK.")] string target,
        [Description("The ground view's zoom factor, as a previous center_ground answer reports it; 0 keeps the current zoom.")] double zoom
    )
    {
        if (zoom < 0)
        {
            throw new AppToolArgumentException(
                "zoom",
                string.Create(CultureInfo.InvariantCulture, $"'zoom' must be 0 to keep the zoom, or a positive zoom factor, not {zoom}.")
            );
        }

        LatLon centre;
        if (FindAircraft(target) is { } aircraft)
        {
            centre = aircraft.Position;
        }
        else if (!_viewModel.CommandInput.NavDbReady)
        {
            return Task.FromResult(AppToolOutcome.Unavailable("The navigation data is still loading."));
        }
        else if (FrdResolver.Resolve(target.Trim().ToUpperInvariant(), NavigationDatabase.Instance) is { } fix)
        {
            centre = new LatLon(fix.Lat, fix.Lon);
        }
        else
        {
            return Task.FromResult(AppToolOutcome.Unavailable($"No aircraft, fix or airport named {target}."));
        }

        GroundViewModel ground = _viewModel.Ground;
        ground.ViewCenterLat = centre.Lat;
        ground.ViewCenterLon = centre.Lon;
        if (zoom > 0)
        {
            ground.ViewZoom = zoom;
        }

        GroundFraming framing = new(ground.ViewCenterLat, ground.ViewCenterLon, ground.ViewZoom);
        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(framing, CoachingJsonOptions)));
    }

    /// <summary>An airport id in the form two ids are compared in: upper-cased, a 4-letter id starting with K without the K.</summary>
    private static string NormalizeAirport(string airportId)
    {
        string trimmed = airportId.Trim().ToUpperInvariant();
        return ((trimmed.Length == 4) && (trimmed[0] == 'K')) ? trimmed[1..] : trimmed;
    }

    /// <summary>Whether <paramref name="candidate"/> names the airport whose normalized id is <paramref name="normalized"/>.</summary>
    private static bool AirportMatches(string? candidate, string normalized) =>
        !string.IsNullOrWhiteSpace(candidate) && string.Equals(NormalizeAirport(candidate), normalized, StringComparison.OrdinalIgnoreCase);

    private static bool ServesAirport(AircraftModel aircraft, string normalized) =>
        AirportMatches(aircraft.GroundAirportId, normalized)
        || AirportMatches(aircraft.Departure, normalized)
        || AirportMatches(aircraft.Destination, normalized);

    /// <summary>
    /// Whether <paramref name="aircraft"/> stands quiet on the ground: stopped, never in contact and with no pending pilot request.
    /// </summary>
    private static bool IsQuietOnTheGround(AircraftModel aircraft) =>
        (aircraft.IsOnGround) && (aircraft.GroundSpeed < 1.0) && (!aircraft.HasMadeInitialContact) && (aircraft.PendingPilotRequest is null);

    /// <summary>The text, or null when it is empty, so the answer leaves the field out.</summary>
    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Squawk(uint code) => code.ToString("D4", CultureInfo.InvariantCulture);

    private static int Whole(double value) => (int)Math.Round(value);

    private static TrafficRow BuildRow(AircraftModel aircraft) =>
        new()
        {
            Callsign = aircraft.Callsign,
            Type = Text(aircraft.AircraftType),
            Cwt = Text(aircraft.CwtCode),
            Rules = Text(aircraft.FlightRules),
            Dep = Text(aircraft.Departure),
            Dest = Text(aircraft.Destination),
            Lat = Math.Round(aircraft.Position.Lat, 5),
            Lon = Math.Round(aircraft.Position.Lon, 5),
            Hdg = Whole(aircraft.MagneticHeadingDegrees),
            Gs = Whole(aircraft.GroundSpeed),
            Alt = Whole(aircraft.Altitude),
            OnGround = aircraft.IsOnGround,
            Phase = Text(aircraft.CurrentPhase),
            Situation = aircraft.Situation.ToString(),
            Status = Text(aircraft.SmartStatus),
            Parking = Text(aircraft.ParkingSpot),
            Taxiway = Text(aircraft.CurrentTaxiway),
            TaxiRoute = Text(aircraft.TaxiRoute),
            TaxiDest = Text(aircraft.TaxiDestination),
            Hold = Text(aircraft.HoldKind),
            HoldFor = Text(aircraft.HoldYieldTarget ?? aircraft.AutoYieldTarget),
            HoldShortAhead = aircraft.HasTaxiwayHoldShortAhead ? true : null,
            NextCrossing = Text(aircraft.NextCrossingRunway),
            Queue = BuildQueue(aircraft),
            Rwy = Text(aircraft.AssignedRunway),
            DepRwy = Text(aircraft.DepartureRunway),
            Meet = aircraft.MeetPoints is { Count: > 0 } meet ? [.. meet.Select(point => point.Callsign)] : null,
            Conflict = Text(aircraft.ConflictPeerCallsign),
            Pending = Text(aircraft.PendingCommands),
            Request = BuildRequest(aircraft.PendingPilotRequest),
            Contacted = aircraft.HasMadeInitialContact,
        };

    private static RowQueue? BuildQueue(AircraftModel aircraft) =>
        aircraft.RunwayQueuePosition > 0
            ? new RowQueue(Text(aircraft.RunwayQueueRunway), aircraft.RunwayQueuePosition, Text(aircraft.RunwayQueueIntersection))
            : null;

    private static RowRequest? BuildRequest(PendingPilotRequestDto? request) =>
        request is null
            ? null
            : new RowRequest(
                Text(request.Kind),
                Text(request.ResponseState),
                Text(request.LastPilotLine),
                Math.Round(request.FirstRequestedAtSeconds, 1)
            );

    /// <summary>The <c>get_aircraft</c> answer: every field of the aircraft's <see cref="TrafficRow"/>, copied from it, then the rest.</summary>
    private static AircraftDetail BuildDetail(AircraftModel aircraft)
    {
        TrafficRow row = BuildRow(aircraft);
        return new AircraftDetail
        {
            Callsign = row.Callsign,
            Type = row.Type,
            Cwt = row.Cwt,
            Rules = row.Rules,
            Dep = row.Dep,
            Dest = row.Dest,
            Lat = row.Lat,
            Lon = row.Lon,
            Hdg = row.Hdg,
            Gs = row.Gs,
            Alt = row.Alt,
            OnGround = row.OnGround,
            Phase = row.Phase,
            Situation = row.Situation,
            Status = row.Status,
            Parking = row.Parking,
            Taxiway = row.Taxiway,
            TaxiRoute = row.TaxiRoute,
            TaxiDest = row.TaxiDest,
            Hold = row.Hold,
            HoldFor = row.HoldFor,
            HoldShortAhead = row.HoldShortAhead,
            NextCrossing = row.NextCrossing,
            Queue = row.Queue,
            Rwy = row.Rwy,
            DepRwy = row.DepRwy,
            Meet = row.Meet,
            Conflict = row.Conflict,
            Pending = row.Pending,
            Request = row.Request,
            Contacted = row.Contacted,
            Route = Text(aircraft.Route),
            CruiseAlt = aircraft.CruiseAltitude,
            Remarks = Text(aircraft.Remarks),
            Squawk = Squawk(aircraft.BeaconCode),
            AssignedSquawk = aircraft.AssignedBeaconCode == 0 ? null : Squawk(aircraft.AssignedBeaconCode),
            Ias = Whole(aircraft.IndicatedAirspeed),
            Vs = Whole(aircraft.VerticalSpeed),
            AssignedHdg = aircraft.AssignedHeading is { } heading ? Whole(heading.Degrees) : null,
            AssignedAlt = aircraft.AssignedAltitude is { } altitude ? Whole(altitude) : null,
            AssignedSpd = aircraft.AssignedSpeed is { } speed ? Whole(speed) : null,
            LandingClearance = Text(aircraft.LandingClearance),
            ClearedRunway = Text(aircraft.ClearedRunway),
            ExitsAhead = aircraft.ExitsAhead is { Count: > 0 } exits
                ? [.. exits.Select(exit => new DetailExit(exit.Taxiway, exit.Side.ToString(), exit.DistanceFt, exit.Planned))]
                : null,
            PhaseSequence = Text(aircraft.PhaseSequence),
            ActivePhaseIndex = aircraft.ActivePhaseIndex,
            Owner = Text(aircraft.Owner),
            HandoffPeer = Text(aircraft.HandoffPeer),
            Scratchpad1 = Text(aircraft.Scratchpad1),
            Scratchpad2 = Text(aircraft.Scratchpad2),
            ArmedReports = aircraft.ArmedReports,
        };
    }

    private static TerminalLine BuildTerminalLine(TerminalEntry entry) =>
        new()
        {
            Seq = entry.Sequence,
            T = entry.ElapsedSeconds is { } seconds ? Math.Round(seconds, 1) : null,
            Kind = entry.Kind.ToString(),
            PilotKind = Text(entry.PilotKind),
            Callsign = Text(entry.Callsign),
            Initials = Text(entry.Initials),
            Message = Text(entry.Message),
        };

    private static ContextPosition? BuildPosition(ActivePositionDto? position) =>
        position is null
            ? null
            : new ContextPosition
            {
                Callsign = Text(position.Callsign),
                Name = Text(position.Name),
                RadioName = Text(position.RadioName),
                Frequency = Text(position.Frequency),
                Type = Text(position.PositionType),
                Facility = Text(position.FacilityId),
            };

    /// <summary>The <c>get_traffic</c> object; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record TrafficAnswer(double SimSeconds, bool Paused, IReadOnlyList<TrafficRow> Aircraft);

    /// <summary>One <c>get_traffic</c> row; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record TrafficRow
    {
        public required string Callsign { get; init; }
        public required string? Type { get; init; }
        public required string? Cwt { get; init; }
        public required string? Rules { get; init; }
        public required string? Dep { get; init; }
        public required string? Dest { get; init; }
        public required double Lat { get; init; }
        public required double Lon { get; init; }
        public required int Hdg { get; init; }
        public required int Gs { get; init; }
        public required int Alt { get; init; }
        public required bool OnGround { get; init; }
        public required string? Phase { get; init; }
        public required string Situation { get; init; }
        public required string? Status { get; init; }
        public required string? Parking { get; init; }
        public required string? Taxiway { get; init; }
        public required string? TaxiRoute { get; init; }
        public required string? TaxiDest { get; init; }
        public required string? Hold { get; init; }
        public required string? HoldFor { get; init; }
        public required bool? HoldShortAhead { get; init; }
        public required string? NextCrossing { get; init; }
        public required RowQueue? Queue { get; init; }
        public required string? Rwy { get; init; }
        public required string? DepRwy { get; init; }
        public required IReadOnlyList<string>? Meet { get; init; }
        public required string? Conflict { get; init; }
        public required string? Pending { get; init; }
        public required RowRequest? Request { get; init; }
        public required bool Contacted { get; init; }
    }

    private sealed record RowQueue(string? Runway, int Position, string? Intersection);

    private sealed record RowRequest(string? Kind, string? State, string? Line, double Since);

    /// <summary>The <c>get_aircraft</c> object: the <see cref="TrafficRow"/> fields first, then the detail, in JSON order.</summary>
    private sealed record AircraftDetail
    {
        public required string Callsign { get; init; }
        public required string? Type { get; init; }
        public required string? Cwt { get; init; }
        public required string? Rules { get; init; }
        public required string? Dep { get; init; }
        public required string? Dest { get; init; }
        public required double Lat { get; init; }
        public required double Lon { get; init; }
        public required int Hdg { get; init; }
        public required int Gs { get; init; }
        public required int Alt { get; init; }
        public required bool OnGround { get; init; }
        public required string? Phase { get; init; }
        public required string Situation { get; init; }
        public required string? Status { get; init; }
        public required string? Parking { get; init; }
        public required string? Taxiway { get; init; }
        public required string? TaxiRoute { get; init; }
        public required string? TaxiDest { get; init; }
        public required string? Hold { get; init; }
        public required string? HoldFor { get; init; }
        public required bool? HoldShortAhead { get; init; }
        public required string? NextCrossing { get; init; }
        public required RowQueue? Queue { get; init; }
        public required string? Rwy { get; init; }
        public required string? DepRwy { get; init; }
        public required IReadOnlyList<string>? Meet { get; init; }
        public required string? Conflict { get; init; }
        public required string? Pending { get; init; }
        public required RowRequest? Request { get; init; }
        public required bool Contacted { get; init; }
        public required string? Route { get; init; }
        public required int CruiseAlt { get; init; }
        public required string? Remarks { get; init; }
        public required string Squawk { get; init; }
        public required string? AssignedSquawk { get; init; }
        public required int Ias { get; init; }
        public required int Vs { get; init; }
        public required int? AssignedHdg { get; init; }
        public required int? AssignedAlt { get; init; }
        public required int? AssignedSpd { get; init; }
        public required string? LandingClearance { get; init; }
        public required string? ClearedRunway { get; init; }
        public required IReadOnlyList<DetailExit>? ExitsAhead { get; init; }
        public required string? PhaseSequence { get; init; }
        public required int ActivePhaseIndex { get; init; }
        public required string? Owner { get; init; }
        public required string? HandoffPeer { get; init; }
        public required string? Scratchpad1 { get; init; }
        public required string? Scratchpad2 { get; init; }
        public required ArmedReportsDto? ArmedReports { get; init; }
    }

    private sealed record DetailExit(string Taxiway, string Side, int DistanceFt, bool Planned);

    /// <summary>The <c>read_terminal</c> object; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record TerminalAnswer(long Cursor, bool? More, IReadOnlyList<TerminalLine> Entries);

    private sealed record TerminalLine
    {
        public required long Seq { get; init; }
        public required double? T { get; init; }
        public required string Kind { get; init; }
        public required string? PilotKind { get; init; }
        public required string? Callsign { get; init; }
        public required string? Initials { get; init; }
        public required string? Message { get; init; }
    }

    /// <summary>The <c>get_airport_context</c> object; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record AirportContext
    {
        public required string Airport { get; init; }
        public required string? Metar { get; init; }
        public required ContextWind? Wind { get; init; }
        public required double? Altimeter { get; init; }
        public required int? Ceiling { get; init; }
        public required IReadOnlyList<string>? RunwaysInUse { get; init; }
        public required ContextPosition? Position { get; init; }
        public required ContextSolo Solo { get; init; }
        public required ContextSim Sim { get; init; }
    }

    private sealed record ContextWind(int? Dir, int? Spd, int? Gust, int? VarFrom, int? VarTo);

    private sealed record ContextPosition
    {
        public required string? Callsign { get; init; }
        public required string? Name { get; init; }
        public required string? RadioName { get; init; }
        public required string? Frequency { get; init; }
        public required string? Type { get; init; }
        public required string? Facility { get; init; }
    }

    private sealed record ContextSolo(
        bool Enabled,
        int ParkingCallupRatePercent,
        int ParkingCallupIntervalSeconds,
        int ArrivalRatePercent,
        int GoAroundPercent
    );

    private sealed record ContextSim(double Seconds, int Rate, bool Paused);

    /// <summary>The <c>center_ground</c> object: the primary ground view's centre and viewport zoom after the call.</summary>
    private sealed record GroundFraming(double CenterLat, double CenterLon, double Zoom);
}
