using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// Reads a scenario aircraft's strip steps from its preset commands and writes them back. A preset is an owned step
/// when its whole command is one strip command in the editor's spelling (aircraft-scoped, not keyed, no bay-scoped or
/// <c>HSTRIP_</c> half-strip form, the arrival id for an arrival's full strip) with its time in the preset's time
/// offset; any other preset holding a strip command is an external step, which Write never touches. Ownership is
/// derived on every read from the parser's records (<see cref="CommandParser.ParseCompound"/>), never stored.
/// </summary>
public static class StripPresets
{
    private static readonly ILogger Log = SimLog.CreateLogger("StripPresets");

    /// <summary>The route and fix context a preset's command parses with, from the aircraft's filed plan.</summary>
    private static (string? Route, FixResolutionContext Context) Parsing(ScenarioAircraft aircraft) =>
        aircraft.FlightPlan is { } plan
            ? (plan.Route, new FixResolutionContext(plan.Destination, plan.Departure))
            : (null, FixResolutionContext.None);

    /// <summary>The step kind of every strip command record; a command not listed is not a strip command.</summary>
    private static readonly Dictionary<Type, StripStepKind> KindByRecord = new()
    {
        [typeof(StripMoveCommand)] = StripStepKind.Move,
        [typeof(StripAnnotateCommand)] = StripStepKind.Annotate,
        [typeof(StripOffsetCommand)] = StripStepKind.Offset,
        [typeof(StripDeleteCommand)] = StripStepKind.Delete,
        [typeof(HalfStripCreateCommand)] = StripStepKind.HalfCreate,
        [typeof(HalfStripAmendCommand)] = StripStepKind.HalfAmend,
        [typeof(HalfStripMoveCommand)] = StripStepKind.HalfMove,
        [typeof(HalfStripOffsetCommand)] = StripStepKind.HalfOffset,
        [typeof(HalfStripSlideCommand)] = StripStepKind.HalfSlide,
        [typeof(HalfStripDeleteCommand)] = StripStepKind.HalfDelete,
        [typeof(StripScanCommand)] = StripStepKind.Other,
        [typeof(SeparatorCreateCommand)] = StripStepKind.Other,
        [typeof(SeparatorDeleteCommand)] = StripStepKind.Other,
        [typeof(SeparatorEditCommand)] = StripStepKind.Other,
        [typeof(SeparatorMoveCommand)] = StripStepKind.Other,
        [typeof(RackSortCommand)] = StripStepKind.Other,
        [typeof(BlankCreateCommand)] = StripStepKind.Other,
        [typeof(BlankDeleteCommand)] = StripStepKind.Other,
    };

    /// <summary>Reads the owned and external strip steps of an aircraft's own presets, in preset order.</summary>
    /// <param name="aircraft">The aircraft whose presets are read: its route, fix context and arrival id come from it.</param>
    /// <param name="primaryAirportId">The scenario's primary airport, which decides the arrival strip forms; null means no arrival.</param>
    public static (IReadOnlyList<StripStep> Owned, IReadOnlyList<StripStep> External) Read(ScenarioAircraft aircraft, string? primaryAirportId)
    {
        (string? route, FixResolutionContext context) = Parsing(aircraft);
        string? arrivalStripId = StripCommandText.ArrivalStripId(aircraft, primaryAirportId);
        var owned = new List<StripStep>();
        var external = new List<StripStep>();
        foreach (PresetCommand preset in aircraft.PresetCommands)
        {
            (StripStep? ownedStep, StripStep? externalStep) = Classify(preset, route, context, arrivalStripId);
            if (ownedStep is not null)
            {
                owned.Add(ownedStep);
            }
            else if (externalStep is not null)
            {
                external.Add(externalStep);
            }
        }

        return (owned, external);
    }

    /// <summary>
    /// The aircraft's new preset list, from its own presets: every preset that is not an owned step kept as the same
    /// object in its order, and the owned block replaced by <paramref name="steps"/> sorted by time, then move,
    /// annotate, offset, delete (ties keep the given order), placed where the first owned preset stood or appended when
    /// there was none. A step whose <see cref="StripStep.PresetId"/> names an owned preset keeps that id and its
    /// extension data; every other step gets an id from <paramref name="idFactory"/>. The aircraft is left unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">A step cannot be written (<see cref="StripCommandText.TryFormat"/>).</exception>
    public static List<PresetCommand> Write(
        ScenarioAircraft aircraft,
        string? primaryAirportId,
        IReadOnlyList<StripStep> steps,
        Func<string> idFactory
    )
    {
        ArgumentNullException.ThrowIfNull(idFactory);
        (string? route, FixResolutionContext context) = Parsing(aircraft);
        string? arrivalStripId = StripCommandText.ArrivalStripId(aircraft, primaryAirportId);
        var ownedById = new Dictionary<string, PresetCommand>(StringComparer.Ordinal);
        int blockAt = -1;
        var kept = new List<PresetCommand>();
        foreach (PresetCommand preset in aircraft.PresetCommands)
        {
            if (Classify(preset, route, context, arrivalStripId).Owned is null)
            {
                kept.Add(preset);
                continue;
            }

            blockAt = blockAt < 0 ? kept.Count : blockAt;
            ownedById.TryAdd(preset.Id, preset);
        }

        List<PresetCommand> block = BuildBlock(steps, ownedById, idFactory, arrivalStripId);
        kept.InsertRange(blockAt < 0 ? kept.Count : blockAt, block);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Wrote {Count} strip presets beside {Kept} others", block.Count, kept.Count - block.Count);
        }
        return kept;
    }

    private static List<PresetCommand> BuildBlock(
        IReadOnlyList<StripStep> steps,
        Dictionary<string, PresetCommand> ownedById,
        Func<string> idFactory,
        string? arrivalStripId
    )
    {
        var block = new List<PresetCommand>();
        foreach (StripStep step in steps.OrderBy(s => s.TimeOffsetSeconds).ThenBy(s => WriteRank(s.Kind)))
        {
            if (!StripCommandText.TryFormat(step, arrivalStripId, out string? command, out string? error))
            {
                throw new ArgumentException($"strip step {step.Kind} at {step.TimeOffsetSeconds}s cannot be written: {error}", nameof(steps));
            }

            PresetCommand? reused = ((step.PresetId is { } id) && ownedById.Remove(id, out PresetCommand? found)) ? found : null;
            block.Add(
                new PresetCommand
                {
                    Id = reused?.Id ?? idFactory(),
                    Command = command,
                    TimeOffset = step.TimeOffsetSeconds,
                    ExtensionData = reused?.ExtensionData,
                }
            );
        }

        return block;
    }

    /// <summary>The write order within one second: moves and creates, then annotations and amends, then offsets and slides, then deletes.</summary>
    private static int WriteRank(StripStepKind kind) =>
        kind switch
        {
            StripStepKind.Move or StripStepKind.HalfCreate or StripStepKind.HalfMove => 0,
            StripStepKind.Annotate or StripStepKind.HalfAmend => 1,
            StripStepKind.Offset or StripStepKind.HalfOffset or StripStepKind.HalfSlide => 2,
            _ => 3,
        };

    private static (StripStep? Owned, StripStep? External) Classify(
        PresetCommand preset,
        string? route,
        FixResolutionContext context,
        string? arrivalStripId
    )
    {
        if (string.IsNullOrWhiteSpace(preset.Command))
        {
            return (null, null);
        }

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(preset.Command, route, null, context);
        if (parsed.Value is not { } compound)
        {
            return (null, null);
        }

        if ((OwnedCommand(compound) is { } single) && (OwnedStep(single, arrivalStripId) is { } owned))
        {
            return (owned with { TimeOffsetSeconds = preset.TimeOffset, PresetId = preset.Id }, null);
        }

        return (null, External(compound, preset));
    }

    private static ParsedCommand? OwnedCommand(CompoundCommand compound) =>
        compound.Blocks is [{ Condition: null, Commands: [ParsedCommand single] }] ? single : null;

    private static StripStep? External(CompoundCommand compound, PresetCommand preset)
    {
        foreach (ParsedBlock block in compound.Blocks)
        {
            foreach (ParsedCommand command in block.Commands)
            {
                if (KindByRecord.TryGetValue(command.GetType(), out StripStepKind kind))
                {
                    return new StripStep(
                        kind,
                        preset.TimeOffset,
                        PresetId: preset.Id,
                        SourceText: preset.Command,
                        ConditionText: DescribeCondition(block.Condition)
                    );
                }
            }
        }

        return null;
    }

    /// <summary>The owned step one strip command reads as (time and preset id unset), or null when it is not in the editor's spelling.</summary>
    private static StripStep? OwnedStep(ParsedCommand command, string? arrivalStripId) =>
        command switch
        {
            StripAnnotateCommand annotate => OwnedAnnotate(annotate, arrivalStripId),
            StripMoveCommand move => OwnedMove(move, arrivalStripId),
            StripOffsetCommand offset => Bare(StripStepKind.Offset, IdMatches(offset.StripId, arrivalStripId)),
            StripDeleteCommand delete => Bare(StripStepKind.Delete, IdMatches(delete.StripId, arrivalStripId)),
            _ => OwnedHalfStep(command),
        };

    private static StripStep? OwnedHalfStep(ParsedCommand command) =>
        command switch
        {
            HalfStripCreateCommand create => OwnedHalfCreate(create),
            HalfStripAmendCommand amend => OwnedHalfAmend(amend),
            HalfStripMoveCommand move => BayStep(StripStepKind.HalfMove, move.Tokens),
            HalfStripOffsetCommand offset => Bare(StripStepKind.HalfOffset, Unscoped(offset.FacilityId, offset.LookupKey)),
            HalfStripSlideCommand slide => Bare(StripStepKind.HalfSlide, Unscoped(slide.FacilityId, slide.LookupKey)),
            HalfStripDeleteCommand delete => OwnedHalfDelete(delete),
            _ => null,
        };

    /// <summary><c>HSO</c> / <c>HSS</c> in the editor's spelling: no source bay and no first-line key.</summary>
    private static bool Unscoped(string? facilityId, string? lookupKey) => (facilityId is null) && (lookupKey is null);

    /// <summary><c>HSD</c> in the editor's spelling: no source bay and no half-strip id.</summary>
    private static StripStep? OwnedHalfDelete(HalfStripDeleteCommand delete) =>
        Bare(StripStepKind.HalfDelete, (delete.FacilityId is null) && (delete.Tokens.Count == 0));

    private static StripStep? Bare(StripStepKind kind, bool owned) => owned ? new StripStep(kind, 0) : null;

    /// <summary>True when a full-strip command's id is the editor's: none for a departure, the arrival id for an arrival.</summary>
    private static bool IdMatches(string? stripId, string? arrivalStripId) =>
        arrivalStripId is null ? stripId is null : string.Equals(stripId, arrivalStripId, StringComparison.OrdinalIgnoreCase);

    private static StripStep? OwnedAnnotate(StripAnnotateCommand annotate, string? arrivalStripId)
    {
        if ((!IdMatches(annotate.StripId, arrivalStripId)) || (StripBox.FromCanonical(annotate.Box) is not { } box))
        {
            return null;
        }

        return new StripStep(StripStepKind.Annotate, 0, Box: box, Text: annotate.Text);
    }

    private static StripStep? OwnedMove(StripMoveCommand move, string? arrivalStripId)
    {
        IReadOnlyList<string> tokens = move.Tokens;
        if (arrivalStripId is not null)
        {
            if ((tokens.Count == 0) || (!tokens[0].Equals(arrivalStripId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            tokens = [.. tokens.Skip(1)];
        }

        // A departure's STRIP_/ARRIVAL_ id token joins the facility segment ("STRIP_X OAK"), which BayStep refuses.
        return BayStep(StripStepKind.Move, tokens);
    }

    private static StripStep? OwnedHalfCreate(HalfStripCreateCommand create)
    {
        if ((create.Rack is not { } rack) || (create.Lines.Count > StripCommandText.MaxHalfStripUserLines))
        {
            return null;
        }

        return new StripStep(
            StripStepKind.HalfCreate,
            0,
            FacilityId: create.FacilityId,
            BayName: create.BayName,
            Rack: rack + 1,
            Lines: create.Lines
        );
    }

    private static StripStep? OwnedHalfAmend(HalfStripAmendCommand amend)
    {
        bool keyedById = (amend.Tokens.Count > 0) && amend.Tokens[0].StartsWith(StripMutations.HalfStripIdPrefix, StringComparison.OrdinalIgnoreCase);
        if ((amend.FacilityId is not null) || keyedById || (amend.Tokens.Count > StripCommandText.MaxHalfStripUserLines))
        {
            return null;
        }

        return new StripStep(StripStepKind.HalfAmend, 0, Lines: amend.Tokens);
    }

    /// <summary>A bay step from <c>FAC/Bay/rack[/slot]</c> tokens, names upper-cased as the parser's bay specs are; null for
    /// any other shape.</summary>
    private static StripStep? BayStep(StripStepKind kind, IReadOnlyList<string> tokens)
    {
        string[] segments = string.Join(' ', tokens).Split('/');
        string facility = segments[0].Trim();
        string bay = segments.Length > 1 ? segments[1].Trim() : "";
        (int? rack, int? slot) = ParseRackAndSlot(segments);
        if ((rack is null) || (facility.Length == 0) || facility.Contains(' ') || (bay.Length == 0))
        {
            return null;
        }

        return new StripStep(kind, 0, FacilityId: facility.ToUpperInvariant(), BayName: bay.ToUpperInvariant(), Rack: rack, Slot: slot);
    }

    /// <summary>The rack and optional slot of <c>FAC/Bay/rack[/slot]</c> segments; a null rack when either is malformed.</summary>
    private static (int? Rack, int? Slot) ParseRackAndSlot(string[] segments)
    {
        if (segments.Length is not (3 or 4))
        {
            return (null, null);
        }

        int? rack = ParseIndex(segments[2]);
        int? slot = segments.Length == 4 ? ParseIndex(segments[3]) : null;
        bool slotMalformed = (segments.Length == 4) && (slot is null);
        return slotMalformed ? (null, null) : (rack, slot);
    }

    /// <summary>A 1-based rack or slot number, or null for anything else.</summary>
    private static int? ParseIndex(string text) =>
        (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && (value >= 1)) ? value : null;

    private static string? DescribeCondition(BlockCondition? condition) =>
        condition switch
        {
            null => null,
            LevelCondition level => string.Create(CultureInfo.InvariantCulture, $"at {level.Altitude}ft"),
            AtFixCondition fix => $"at {fix.FixName}",
            AtGroundEntityCondition ground => $"at {ground.Token}",
            GiveWayCondition giveWay => $"giveway {giveWay.TargetCallsign}",
            DistanceFinalCondition final => string.Create(CultureInfo.InvariantCulture, $"at {final.DistanceNm}nm final"),
            _ => DescribeEventCondition(condition),
        };

    private static string DescribeEventCondition(BlockCondition condition) =>
        condition switch
        {
            OnHandoffCondition => "on handoff",
            OnHoldShortCondition => "on hold short",
            OnTheGoCondition => "on the go",
            _ => condition.GetType().Name,
        };
}
