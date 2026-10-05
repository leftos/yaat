using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Where a ground spawn sits, as <see cref="InitialCallupClassifier.Classify"/> reads it: whether it is a runway spawn, and
/// whether its airport has a taxi graph.
/// </summary>
public readonly record struct InitialCallupSpawn(bool IsRunwaySpawn, bool HasTaxiGraph);

/// <summary>
/// What <see cref="InitialCallupClassifier.Classify"/> decides for one spawn: its plan and, for an
/// <see cref="InitialCallupPlan.AfterTaxiArrival"/> plan, the stop its preset taxi ends at (null when the preset names
/// none the call can be tied to, and for every other plan).
/// </summary>
public readonly record struct InitialCallupDecision(InitialCallupPlan Plan, PresetTaxiStop? TaxiStop)
{
    public static readonly InitialCallupDecision None = new(InitialCallupPlan.None, null);
}

/// <summary>
/// Decides a ground spawn's <see cref="InitialCallupPlan"/> from where it spawned and its timed presets (an airborne spawn
/// is never classified and keeps none). Rules, first match wins:
/// <list type="number">
/// <item>A runway spawn: a takeoff preset (CTO, CTOPP) → none; else a SAY preset → <see cref="InitialCallupPlan.RunwaySayOnly"/>;
/// else <see cref="InitialCallupPlan.RunwayNoPreset"/>.</item>
/// <item>No taxi graph → none (a SAY preset is still said; the aircraft makes no call of its own).</item>
/// <item>A TAXI, TAXIAUTO or ATXI preset whose destination is a runway → none: a scripted taxi never calls while it runs,
/// and at the runway the tower call takes over. A runway hold short counts as the destination when it is the route's
/// last stop (no spot or parking destination, and no spot hold short listed after it).</item>
/// <item>A TAXI, TAXIAUTO or ATXI preset to parking (a stand or helipad) → none.</item>
/// <item>A FOLLOWG preset → none: a scripted follow ends wherever its leader leads.</item>
/// <item>Any other TAXI, TAXIAUTO or ATXI preset → after it arrives, when it names the stop the call is made at (a spot, a
/// taxiway hold short, or for a TAXI with no destination the last taxiway of its path); else → none.</item>
/// <item>A PUSH or PUSHM preset and no taxi → after the push.</item>
/// <item>Otherwise → the stand call.</item>
/// </list>
/// </summary>
public static class InitialCallupClassifier
{
    private static readonly ILogger Log = SimLog.CreateLogger("InitialCallupClassifier");

    /// <summary>
    /// The plan for one loaded aircraft, with the stop its preset taxi ends at; <paramref name="callsign"/> names it in the
    /// warning for a preset that does not parse.
    /// </summary>
    public static InitialCallupDecision Classify(string callsign, IReadOnlyList<PresetCommand> presets, InitialCallupSpawn spawn)
    {
        if (spawn.IsRunwaySpawn)
        {
            return new InitialCallupDecision(DecideRunway(callsign, presets), null);
        }

        return spawn.HasTaxiGraph ? Decide(callsign, presets) : InitialCallupDecision.None;
    }

    /// <summary>
    /// A runway spawn's plan: a takeoff preset scripts its departure (none); else a SAY preset is what it says
    /// (<see cref="InitialCallupPlan.RunwaySayOnly"/>); else <see cref="InitialCallupPlan.RunwayNoPreset"/>.
    /// </summary>
    private static InitialCallupPlan DecideRunway(string callsign, IReadOnlyList<PresetCommand> presets)
    {
        bool say = false;
        foreach (ParsedCommand command in ParsedPresetCommands(callsign, presets))
        {
            if (command is ClearedForTakeoffCommand or ClearedTakeoffPresentCommand)
            {
                return InitialCallupPlan.None;
            }

            say |= command is SayCommand;
        }

        return say ? InitialCallupPlan.RunwaySayOnly : InitialCallupPlan.RunwayNoPreset;
    }

    private static IEnumerable<ParsedCommand> ParsedPresetCommands(string callsign, IReadOnlyList<PresetCommand> presets)
    {
        foreach (PresetCommand preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Command) || (TryParse(callsign, preset.Command) is not { } compound))
            {
                continue;
            }

            foreach (ParsedCommand command in compound.Blocks.SelectMany(b => b.Commands))
            {
                yield return command;
            }
        }
    }

    /// <summary>
    /// The plan a ground spawn on a taxi graph gets from its presets alone (the rules after the taxi-graph one) — the part of
    /// <see cref="Classify"/> a reader without a loaded layout (the client's pre-load hint) can evaluate.
    /// </summary>
    public static InitialCallupPlan ClassifyPresets(string callsign, IReadOnlyList<PresetCommand> presets) => Decide(callsign, presets).Plan;

    private static InitialCallupDecision Decide(string callsign, IReadOnlyList<PresetCommand> presets)
    {
        PresetGroundIntent intent = ReadGroundIntent(callsign, presets);
        if (intent.TaxiToRunway || intent.TaxiToParking || intent.FollowGround)
        {
            return InitialCallupDecision.None;
        }

        // An after-taxi-arrival call needs the stop it is made at; a taxi that names none gives no call at all.
        if (intent.OtherTaxi)
        {
            return intent.TaxiStop is { } stop ? new InitialCallupDecision(InitialCallupPlan.AfterTaxiArrival, stop) : InitialCallupDecision.None;
        }

        return new InitialCallupDecision(intent.Push ? InitialCallupPlan.AfterPush : InitialCallupPlan.StandCall, null);
    }

    /// <summary>
    /// Where a TAXI that goes neither to a runway nor to parking first comes to rest for its call: its <c>$spot</c>
    /// destination; else a trailing spot hold short; else its first taxiway hold short (the aircraft stops at the first
    /// bar it reaches); else, with no destination, the last taxiway of its path. Null for a TAXI with none of these.
    /// </summary>
    private static PresetTaxiStop? TaxiStopOf(TaxiCommand taxi)
    {
        if (taxi.DestinationSpot is { Length: > 0 } spot)
        {
            return new PresetTaxiStop(PresetTaxiStopKind.Spot, spot, null);
        }

        if ((taxi.HoldShorts.Count > 0) && taxi.HoldShorts[^1] is { IsSpot: true } trailingSpot)
        {
            return new PresetTaxiStop(PresetTaxiStopKind.Spot, trailingSpot.Target, null);
        }

        foreach (HoldShortTarget holdShort in taxi.HoldShorts)
        {
            if (!holdShort.IsSpot && !IsRunwayHoldShort(holdShort))
            {
                return new PresetTaxiStop(PresetTaxiStopKind.TaxiwayHoldShort, holdShort.Target, holdShort.OnTaxiway);
            }
        }

        return taxi.Path.Count > 0 ? new PresetTaxiStop(PresetTaxiStopKind.RouteEnd, taxi.Path[^1], null) : null;
    }

    private static PresetGroundIntent ReadGroundIntent(string callsign, IReadOnlyList<PresetCommand> presets)
    {
        var intent = new PresetGroundIntent();
        foreach (ParsedCommand command in ParsedPresetCommands(callsign, presets))
        {
            intent.Add(command);
        }

        return intent;
    }

    private static CompoundCommand? TryParse(string callsign, string text)
    {
        ParseResult<CompoundCommand> parsed;
        try
        {
            parsed = CommandParser.ParseCompound(text);
        }
        catch (InvalidOperationException ex)
        {
            // A fix-bearing preset reaches the navigation database, which a pre-load reader may not have yet.
            Log.LogWarning(ex, "{Callsign}: preset '{Preset}' could not be parsed; skipped for the initial call-up plan", callsign, text);
            return null;
        }

        if (parsed.Value is null)
        {
            Log.LogWarning(
                "{Callsign}: preset '{Preset}' does not parse ({Reason}); skipped for the initial call-up plan",
                callsign,
                text,
                parsed.Reason
            );
        }

        return parsed.Value;
    }

    private static bool IsRunwayHoldShort(HoldShortTarget target) => !target.IsSpot && CommandParser.IsRunwayArg(target.Target);

    /// <summary>
    /// A TAXI ends at a runway when it names one as its destination, or when a runway hold short is its last stop: no spot
    /// or parking destination, and no spot hold short listed after the last runway one (the parser puts every token after
    /// <c>HS</c> in the hold-short list, so <c>TAXI B HS 28L $5</c> holds short of 28L on the way to spot 5).
    /// </summary>
    private static bool TaxisToARunway(TaxiCommand taxi)
    {
        if (taxi.DestinationRunway is not null)
        {
            return true;
        }

        if ((taxi.DestinationSpot is not null) || (taxi.DestinationParking is not null))
        {
            return false;
        }

        int lastRunwayHoldShort = taxi.HoldShorts.FindLastIndex(IsRunwayHoldShort);
        return (lastRunwayHoldShort >= 0) && !taxi.HoldShorts.Skip(lastRunwayHoldShort + 1).Any(target => target.IsSpot);
    }

    private sealed class PresetGroundIntent
    {
        public bool TaxiToRunway { get; private set; }
        public bool TaxiToParking { get; private set; }
        public bool OtherTaxi { get; private set; }
        public bool FollowGround { get; private set; }
        public bool Push { get; private set; }

        /// <summary>The stop the last other taxi (<see cref="OtherTaxi"/>) ends at, or null.</summary>
        public PresetTaxiStop? TaxiStop { get; private set; }

        public void Add(ParsedCommand command)
        {
            switch (command)
            {
                case TaxiCommand taxi:
                    AddTaxi(TaxisToARunway(taxi), taxi.DestinationParking is not null, TaxiStopOf(taxi));
                    break;
                case TaxiAutoCommand auto:
                    AddTaxi(
                        auto.DestinationRunway is not null,
                        auto.DestinationParking is not null,
                        auto.DestinationSpot is { Length: > 0 } spot ? new PresetTaxiStop(PresetTaxiStopKind.Spot, spot, null) : null
                    );
                    break;
                case AirTaxiCommand airTaxi:
                    AddTaxi(
                        (airTaxi.Destination is not null) && (airTaxi.TargetKind == AirTaxiTargetKind.Runway),
                        (airTaxi.Destination is not null) && (airTaxi.TargetKind == AirTaxiTargetKind.Stand),
                        (airTaxi.TargetKind == AirTaxiTargetKind.Spot) && (airTaxi.Destination is { Length: > 0 } airSpot)
                            ? new PresetTaxiStop(PresetTaxiStopKind.Spot, airSpot, null)
                            : null
                    );
                    break;
                case FollowGroundCommand:
                    FollowGround = true;
                    break;
                case PushbackCommand or PushbackMultiCommand:
                    Push = true;
                    break;
            }
        }

        private void AddTaxi(bool toRunway, bool toParking, PresetTaxiStop? stop)
        {
            if (toRunway)
            {
                TaxiToRunway = true;
            }
            else if (toParking)
            {
                TaxiToParking = true;
            }
            else
            {
                OtherTaxi = true;
                TaxiStop = stop;
            }
        }
    }
}
