using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Guards <see cref="CommandDescriber.ClassifyCommand"/> against its <c>_ =&gt; Immediate</c>
/// fallback silently swallowing new command types. A command classified
/// <see cref="TrackedCommandType.Immediate"/> completes the instant it is applied, so a chained
/// compound advances past it immediately — correct for one-shot commands, wrong for anything
/// target-seeking. Every type that classifies Immediate must appear in the curated allowlist
/// below, forcing a conscious classification decision for each new command.
/// </summary>
public class ClassifyCommandCompletenessTests(ITestOutputHelper output)
{
    /// <summary>
    /// Command types that intentionally classify as <see cref="TrackedCommandType.Immediate"/>:
    /// one-shot state changes, reports, force-set commands, and every phase-installing command
    /// (once a phase is installed, chain advancement is governed by the phase, not by the
    /// tracked-command predicate — see docs/command-chaining.md).
    /// A new command type landing here via the switch fallback fails this test; either add an
    /// explicit arm in ClassifyCommand or add the type here with justification.
    /// </summary>
    private static readonly HashSet<string> IntentionallyImmediate =
    [
        "AcceptAllHandoffsCommand",
        "AcceptHandoffCommand",
        "AcknowledgeCommand",
        "AcknowledgeConflictAlertCommand",
        "AcknowledgePilotContactCommand",
        "AddAircraftCommand",
        "AirTaxiCommand",
        "AsdexEditCommand",
        "AsdexEnableAllAlertsCommand",
        "AsdexVerbCommand",
        "AssignRunwayCommand",
        "AssumeCommand",
        "BlankCreateCommand",
        "BlankDeleteCommand",
        "BookmarkCommand",
        "BreakConflictCommand",
        "Cancel270Command",
        "CancelAutoDeleteCommand",
        "CancelHandoffCommand",
        "CancelIfrCommand",
        "CancelLandingClearanceCommand",
        "CancelTakeoffClearanceCommand",
        "CfrDepartureCommand",
        "ChangeDestinationCommand",
        "CircleAirportCommand",
        "ClearRunwayCommand",
        "ClearTurnRateCommand",
        "ClearedApproachCommand",
        "ClearedApproachStraightInCommand",
        "ClearedBravoAirspaceCommand",
        "ClearedForOptionCommand",
        "ClearedForTakeoffCommand",
        "ClearedTakeoffPresentCommand",
        "ClearedToLandCommand",
        "ClearedVisualApproachCommand",
        "ConeCommand",
        "ConsolidateCommand",
        "ContactCommand",
        "ConvertPointoutCommand",
        "CoordinationAcknowledgeCommand",
        "CoordinationAutoAckCommand",
        "CoordinationDeleteCommand",
        "CoordinationHoldCommand",
        "CoordinationModifyCommand",
        "CoordinationRecallCommand",
        "CoordinationReleaseCommand",
        "CoordinationReorderCommand",
        "CreateAbbreviatedFlightPlanCommand",
        "CreateFlightPlanCommand",
        "CrossRunwayCommand",
        "CruiseCommand",
        "DeconsolidateCommand",
        "DeleteCommand",
        "DeleteQueuedCommand",
        "DisarmHoldForReleaseCommand",
        "DropTrackCommand",
        "EnterFinalCommand",
        "EnterLeftBaseCommand",
        "EnterLeftCrosswindCommand",
        "EnterLeftDownwindCommand",
        "EnterRightBaseCommand",
        "EnterRightCrosswindCommand",
        "EnterRightDownwindCommand",
        "ExitLeftCommand",
        "ExitRightCommand",
        "ExitTaxiwayCommand",
        "ExpectApproachCommand",
        "ExpediteCommand",
        "ExtendPatternCommand",
        "FollowCommand",
        "FollowGroundCommand",
        "ForceAltitudeCommand",
        "ForceHandoffCommand",
        "ForceHeadingCommand",
        "ForceLandingCommand",
        "ForceQuicklookClearCommand",
        "ForceQuicklookCommand",
        "ForceSpeedCommand",
        "FrequencyChangeApprovedCommand",
        "GhostTrackCommand",
        "GiveWayCommand",
        "GoAroundCommand",
        "GoCommand",
        "HalfStripAmendCommand",
        "HalfStripCreateCommand",
        "HalfStripDeleteCommand",
        "HalfStripMoveCommand",
        "HalfStripOffsetCommand",
        "HalfStripSlideCommand",
        "HoldAtFixHoverCommand",
        "HoldAtFixOrbitCommand",
        "HoldForReleaseCommand",
        "HoldPositionCommand",
        "HoldPresentPosition360Command",
        "HoldPresentPositionHoverCommand",
        "HoldShortCommand",
        "IdentCommand",
        "InhibitConflictAlertCommand",
        "InhibitDuplicateBeaconCommand",
        "InitiateHandoffAllCommand",
        "InitiateHandoffCommand",
        "JRingCommand",
        "JoinApproachCommand",
        "JoinApproachStraightInCommand",
        "JoinFinalApproachCourseCommand",
        "LandAndHoldShortCommand",
        "LandCommand",
        "LeaderDirectionCommand",
        "LineUpAndWaitCommand",
        "ListApproachesCommand",
        "LowApproachCommand",
        "MakeLeft270Command",
        "MakeLeft360Command",
        "MakeLeftSTurnsCommand",
        "MakeLeftTrafficCommand",
        "MakeNormalApproachCommand",
        "MakeRight270Command",
        "MakeRight360Command",
        "MakeRightSTurnsCommand",
        "MakeRightTrafficCommand",
        "MakeShortApproachCommand",
        "NormalRateCommand",
        "NoteCommand",
        "OffsetLeftPatternCommand",
        "OffsetRightPatternCommand",
        "OnHandoffCommand",
        "PatternSizeCommand",
        "PauseCommand",
        "PilotReportedAltitudeCommand",
        "Plan270Command",
        "PointOutCommand",
        "PositionTurnAltitudeClearanceCommand",
        "PushbackCommand",
        "RandomSquawkCommand",
        "RejectPointoutCommand",
        "ReleaseDepartureCommand",
        "ReportCommand",
        "ReportFieldAdvisoryCommand",
        "ReportFieldInSightCommand",
        "ReportFieldInSightForcedCommand",
        "ReportTrafficAdvisoryCommand",
        "ReportTrafficInSightCommand",
        "ReportTrafficInSightForcedCommand",
        "ReportTrafficLandmarkCommand",
        "ReportTrafficPatternCommand",
        "ReportTrafficRelativeCommand",
        "RepositionMoveCommand",
        "RepositionToLocationCommand",
        "ResumeCommand",
        "RetractPointoutCommand",
        "SafetyAlertCommand",
        "SayAltitudeCommand",
        "SayCommand",
        "SayExitFixEstimateCommand",
        "SayExpectedApproachCommand",
        "SayHeadingCommand",
        "SayMachCommand",
        "SayPositionCommand",
        "SaySpeedCommand",
        "Scratchpad1Command",
        "Scratchpad2Command",
        "SeparatorCreateCommand",
        "SeparatorDeleteCommand",
        "SeparatorEditCommand",
        "SeparatorMoveCommand",
        "SetActivePositionCommand",
        "SetRemarksCommand",
        "SetTurnRateCommand",
        "ShowQueuedCommand",
        "SimRateCommand",
        "SpawnDelayCommand",
        "SpawnNowCommand",
        "SquawkAllCommand",
        "SquawkCommand",
        "SquawkNormalAllCommand",
        "SquawkNormalCommand",
        "SquawkResetCommand",
        "SquawkStandbyAllCommand",
        "SquawkStandbyCommand",
        "SquawkVfrCommand",
        "StopAndGoCommand",
        "StripAnnotateCommand",
        "StripDeleteCommand",
        "StripMoveCommand",
        "StripOffsetCommand",
        "StripScanCommand",
        "SuppressConflictAlertCommand",
        "TaxiAllCommand",
        "TaxiAutoCommand",
        "TaxiCommand",
        "TdlsDumpCommand",
        "TdlsOpsConfigCommand",
        "TdlsQueueCommand",
        "TdlsSendCommand",
        "TdlsWilcoCommand",
        "TemporaryAltitudeCommand",
        "TimerCommand",
        "TouchAndGoCommand",
        "TrackAircraftCommand",
        "TurnBaseCommand",
        "TurnCrosswindCommand",
        "TurnDownwindCommand",
        "UnpauseCommand",
        "UnsupportedCommand",
        "WakeAdvisoryCommand",
        "WarpCommand",
        "WarpGroundCommand",
    ];

    [Fact]
    public void ClassifyCommand_ImmediateFallback_OnlyForAllowlistedTypes()
    {
        var unlisted = new List<string>();
        var unconstructible = new List<string>();

        foreach (var type in ParsedCommandDummyFactory.AllParsedCommandTypes)
        {
            var cmd = ParsedCommandDummyFactory.CreateDummy(type);
            if (cmd is null)
            {
                unconstructible.Add(type.Name);
                continue;
            }

            if (CommandDescriber.ClassifyCommand(cmd) == TrackedCommandType.Immediate && !IntentionallyImmediate.Contains(type.Name))
            {
                unlisted.Add(type.Name);
            }
        }

        if (unlisted.Count > 0)
        {
            output.WriteLine("Classified Immediate but not in the IntentionallyImmediate allowlist:");
            foreach (var name in unlisted)
            {
                output.WriteLine($"        \"{name}\",");
            }
        }

        Assert.True(unconstructible.Count == 0, $"CreateDummy cannot build: {string.Join(", ", unconstructible)}");
        Assert.True(
            unlisted.Count == 0,
            $"ClassifyCommand returned Immediate for types missing from IntentionallyImmediate: {string.Join(", ", unlisted)}. "
                + "Add an explicit ClassifyCommand arm for a target-seeking command, or allowlist it here if instant completion is intended."
        );
    }

    [Fact]
    public void ClassifyCommand_AllowlistEntriesStillClassifyImmediate()
    {
        var byName = ParsedCommandDummyFactory.AllParsedCommandTypes.ToDictionary(t => t.Name);
        var stale = new List<string>();

        foreach (var name in IntentionallyImmediate.OrderBy(n => n))
        {
            if (!byName.TryGetValue(name, out var type))
            {
                stale.Add($"{name} (type no longer exists)");
                continue;
            }

            var cmd = ParsedCommandDummyFactory.CreateDummy(type);
            if (cmd is null)
            {
                stale.Add($"{name} (dummy unconstructible)");
                continue;
            }

            if (CommandDescriber.ClassifyCommand(cmd) != TrackedCommandType.Immediate)
            {
                stale.Add($"{name} (now classifies {CommandDescriber.ClassifyCommand(cmd)})");
            }
        }

        Assert.True(stale.Count == 0, $"Stale IntentionallyImmediate entries: {string.Join(", ", stale)}");
    }

    [Fact]
    public void ClassifyCommand_ExpediteWithAltitude_IsAltitude()
    {
        // The dummy factory passes null for ExpediteCommand's nullable Altitude, so the sweep above
        // only exercises the bare-EXP (Immediate) arm; pin the EXP <alt> arm explicitly.
        Assert.Equal(TrackedCommandType.Altitude, CommandDescriber.ClassifyCommand(new ExpediteCommand(5000)));
    }
}
