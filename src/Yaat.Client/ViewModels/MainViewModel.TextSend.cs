using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Where <see cref="MainViewModel.SendTextAsync"/> reports every outcome of a line, so the line can come from an entry other
/// than the main command line: its own steps (say-again guard, chat, the dot-command fallthrough, bare-callsign select,
/// callsign resolution, RPO control, parse errors, the VFR and compound gates, the send to an aircraft) and the handlers it
/// hands the line to (the global verbs, the dot commands <c>.scope</c>, <c>.view</c>, scope markers, measure and CRC aliases,
/// the take/give/release control calls, <c>ResolveAircraft</c>'s ambiguity message, the measuring tool's reports and the bookmark
/// seeks' rewind status). Each handler takes the sink as a parameter.
/// </summary>
internal interface ICommandTextSink
{
    /// <summary>Shows an outcome of the line that is not an error: a handler's echo, or the empty status of a success.</summary>
    void ReportStatus(string status);

    /// <summary>Shows why the line failed: a usage or parse error, a refusal, the server's rejection or a send error.</summary>
    void ReportError(string message);

    /// <summary>Takes the line out of the entry it was typed in. A line never cleared stays there for re-editing.</summary>
    void ClearInput();

    /// <summary>Records the line in the command history, under <paramref name="callsign"/> (empty for none).</summary>
    void AddHistory(string callsign, string command);

    /// <summary>Adds a warning line to the terminal.</summary>
    void AddWarning(string message);
}

public partial class MainViewModel
{
    /// <summary>
    /// Runs one typed line as the main command line runs it on Enter: the staged say-again guard, the <c>** </c> override,
    /// chat, client dot commands, global verbs, a bare callsign that selects, then a command to an aircraft. Every outcome,
    /// the handlers' included, goes to <paramref name="sink"/>.
    /// </summary>
    /// <param name="text">The line as typed.</param>
    /// <param name="sink">Where the line's status, errors, clear, history and warnings go.</param>
    internal async Task SendTextAsync(string text, ICommandTextSink sink)
    {
        string trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        // A staged say-again transcript is never sent through any command path (in solo mode the natural-command mapper
        // would map it to an aircraft the decoder declined to name). The user may still edit it, which clears the staged text.
        if (StagedSayAgainStatus(trimmed) is { } sayAgainStatus)
        {
            sink.ReportStatus(sayAgainStatus);
            return;
        }

        var line = new TypedLine(trimmed, sink, _preferences.CommandScheme);
        StripOverridePrefix(line);
        bool handled =
            await TrySendChatAsync(line) || TryRunClientCommand(line) || await TryRunGlobalCommandAsync(line) || TrySelectBareCallsign(line);
        if (!handled)
        {
            await SendAircraftCommandAsync(line);
        }
    }

    /// <summary>The status a staged say-again transcript answers with when <paramref name="text"/> is that transcript, else null.</summary>
    private string? StagedSayAgainStatus(string text) =>
        ((_sayAgainTranscript is not null) && string.Equals(text, _sayAgainTranscript, StringComparison.Ordinal)) ? _sayAgainStatus : null;

    /// <summary>Assignment override: a <c>"** "</c> prefix bypasses the ownership check.</summary>
    private static void StripOverridePrefix(TypedLine line)
    {
        if (line.Text.StartsWith("** ", StringComparison.Ordinal))
        {
            line.ForceOverride = true;
            line.Text = line.Text[3..].TrimStart();
        }
    }

    /// <summary>Chat messages: a <c>'</c>, <c>/</c> or <c>&gt;</c> prefix broadcasts text, not a command.</summary>
    private async Task<bool> TrySendChatAsync(TypedLine line)
    {
        string text = line.Text;
        if (!((text.Length > 1) && CommandInputController.StartsWithChatPrefix(text)))
        {
            return false;
        }

        string chatMessage = text[1..].TrimStart();
        if (!string.IsNullOrEmpty(chatMessage))
        {
            try
            {
                await _connection.SendChatAsync(_preferences.UserInitials, chatMessage);
                line.Sink.AddHistory("", text);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Chat send failed");
                line.Sink.ReportError($"Chat error: {ex.Message}");
            }
        }

        line.Sink.ClearInput();
        return true;
    }

    /// <summary>
    /// Client-only dot commands. Built-in scope markers (CRC .ff / .marker / .markers / .nomarkers) and the measuring tool
    /// (.rbl / .norbl, with *T as the CRC STARS spelling) win, then CRC aliases read from the user's CRC install. Neither
    /// ever reaches the server.
    /// </summary>
    private bool TryRunClientCommand(TypedLine line)
    {
        bool forceAlias = StripCrcPrefix(line);
        string text = line.Text;
        if (!forceAlias && !text.StartsWith('.') && !IsStarTCommand(text))
        {
            return false;
        }

        if (!TryHandleClientCommand(text, forceAlias, line.Sink))
        {
            string verb = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            line.Sink.ReportError($"Unknown command or alias: {verb}");
        }

        line.Sink.AddHistory("", text);
        line.Sink.ClearInput();
        return true;
    }

    /// <summary>"CRC " forces alias resolution, reaching an alias that a built-in dot command would shadow.</summary>
    private static bool StripCrcPrefix(TypedLine line)
    {
        if (!line.Text.StartsWith("CRC ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        line.Text = line.Text[4..].TrimStart();
        return true;
    }

    /// <summary>Runs a dot command or CRC alias; false when nothing knows <paramref name="text"/>.</summary>
    private bool TryHandleClientCommand(string text, bool forceAlias, ICommandTextSink sink)
    {
        bool handled =
            (!forceAlias)
            && (
                TryHandleScopeMarkerCommand(text, sink)
                || TryHandleMeasureCommand(text, sink)
                || TryHandleScopeCommand(text, sink)
                || TryHandleViewCommand(text, sink)
            );
        return handled || TryHandleCrcAlias(text, sink);
    }

    /// <summary>
    /// Global commands first (no callsign needed). SetActivePosition ("AS {tcp}") is global only when it's a standalone
    /// command. The prefix form "AS {tcp} {track_command}" is per-aircraft (server's ExtractAsPrefix strips the prefix and
    /// resolves RPO identity).
    /// </summary>
    private async Task<bool> TryRunGlobalCommandAsync(TypedLine line)
    {
        line.GlobalParsed = AsUnaddressedSayAgain(CommandSchemeParser.Parse(line.Text, line.Scheme), SelectedAircraft is not null);
        if ((line.GlobalParsed is not { } globalParsed) || !IsGlobalCommand(globalParsed.Type))
        {
            return false;
        }

        bool isAsPrefix = (globalParsed.Type == CanonicalCommandType.SetActivePosition) && (globalParsed.Argument?.Contains(' ') == true);
        if (isAsPrefix)
        {
            return false;
        }

        await HandleGlobalCommand(globalParsed, line.Sink);
        return true;
    }

    /// <summary>
    /// A single token that matches a callsign just selects it. A complete command verb (e.g. "TB" = turn base) must win over
    /// a *partial* (substring) callsign match, so a bare command never silently selects an aircraft whose callsign merely
    /// contains those letters. An exact callsign still selects; ambiguous substrings already fall through to command dispatch.
    /// </summary>
    private bool TrySelectBareCallsign(TypedLine line)
    {
        string text = line.Text;
        if (text.Contains(' ') || text.Contains(',') || text.Contains(';'))
        {
            return false;
        }

        (AircraftModel? callsignMatch, CallsignMatcher.Outcome outcome, IReadOnlyList<AircraftModel> _) = CallsignMatcher.Match(text, Aircraft);
        bool completeCommandOverrides = (line.GlobalParsed is not null) && (outcome == CallsignMatcher.Outcome.UniqueSubstring);
        if ((callsignMatch is null) || completeCommandOverrides)
        {
            return false;
        }

        SelectedAircraft = callsignMatch;
        line.Sink.ClearInput();
        return true;
    }

    /// <summary>Resolves the line's aircraft and command, then sends it, to that aircraft or, for a half-strip verb, to none.</summary>
    private async Task SendAircraftCommandAsync(TypedLine line)
    {
        if (!TryExpandMacros(line) || !TryResolveCallsigns(line))
        {
            return;
        }

        // RPO control commands (client-local, bypass command pipeline)
        if (await TryHandleRpoCommand(line))
        {
            return;
        }

        CompoundParseResult? compound = await ParseCompoundOrReportAsync(line);
        if (compound is null)
        {
            return;
        }

        if (line.Target is not { } target)
        {
            await SendWithoutTargetAsync(line, compound);
            return;
        }

        await SendToTargetAsync(line, target, compound);
    }

    /// <summary>Expands macros first so callsign prefix resolution sees real commands, and starts from the selected aircraft.</summary>
    private bool TryExpandMacros(TypedLine line)
    {
        line.OriginalInput = line.Text;
        if (!TypedCommandText.TryExpandMacros(line.Text, _preferences.Macros, out string commandText, out string? macroError))
        {
            line.Sink.ReportError(macroError);
            return false;
        }

        line.Command = commandText;
        line.Target = SelectedAircraft;
        _log.LogDebug(
            "SendCommand target resolution: SelectedAircraft={Selected}, target={Target}",
            SelectedAircraft?.Callsign ?? "(none)",
            line.Target?.Callsign ?? "(none)"
        );
        return true;
    }

    /// <summary>
    /// Resolves a callsign prefix from the (possibly expanded) input, then rewrites partial callsign arguments (FOLLOW, RTIS,
    /// CVA FOLLOW, ...) into canonical callsigns before parsing, matching the first-word partial-match behavior.
    /// </summary>
    private bool TryResolveCallsigns(TypedLine line)
    {
        CallsignPrefixResolver.Result prefixResult = CallsignPrefixResolver.Resolve(line.Command, line.Scheme, Aircraft);
        if (prefixResult is CallsignPrefixResolver.Ambiguous ambiguousPrefix)
        {
            line.Sink.ReportError(ambiguousPrefix.Message);
            return false;
        }

        if (prefixResult is CallsignPrefixResolver.Resolved resolvedPrefix)
        {
            line.Target = resolvedPrefix.Aircraft;
            line.Command = resolvedPrefix.Remainder;
            line.ResolvedCallsign = resolvedPrefix.Aircraft.Callsign;
        }

        CallsignArgumentResolver.Result rewrite = CallsignArgumentResolver.TryRewrite(line.Command, line.Scheme, Aircraft);
        if (rewrite.Error is not null)
        {
            line.Sink.ReportError(rewrite.Error);
            return false;
        }

        line.Command = rewrite.Text ?? line.Command;
        return true;
    }

    private async Task<bool> TryHandleRpoCommand(TypedLine line)
    {
        string upper = line.Command.Trim().ToUpperInvariant();
        bool isGive = upper.StartsWith("GIVE ", StringComparison.Ordinal);
        if ((upper != "TAKE") && !isGive && (upper != "GIVEUP"))
        {
            return false;
        }

        if (line.Target is not { } target)
        {
            line.Sink.ReportError("Select an aircraft first");
            return true;
        }

        string initials = isGive ? upper[5..].Trim() : "";
        if (isGive && (initials.Length == 0))
        {
            line.Sink.ReportError("Usage: GIVE <initials>");
            return true;
        }

        await RunRpoControlAsync(upper, target.Callsign, initials, line.Sink);
        line.Sink.AddHistory(target.Callsign, line.OriginalInput);
        line.Sink.ClearInput();
        return true;
    }

    /// <summary>Takes, gives or gives up control of <paramref name="callsign"/> for the RPO verb in <paramref name="upper"/>.</summary>
    private Task RunRpoControlAsync(string upper, string callsign, string initials, ICommandTextSink sink) =>
        upper switch
        {
            "TAKE" => TakeControlAsync(callsign, sink),
            "GIVEUP" => ReleaseControlAsync(callsign, sink),
            _ => GiveControlAsync(callsign, initials, sink),
        };

    /// <summary>
    /// Parses the line as a compound command (single and multi-block). A line that does not parse is tried as a solo natural
    /// command, else answered with the error; either way the result is null.
    /// </summary>
    private async Task<CompoundParseResult?> ParseCompoundOrReportAsync(TypedLine line)
    {
        AircraftModel? target = line.Target;
        CompoundParseResult? compound = CommandSchemeParser.ParseCompound(
            line.Command,
            line.Scheme,
            out ParseFailure? parseFailure,
            target is null ? FixResolutionContext.None : new FixResolutionContext(target.Destination, target.Departure)
        );
        if (compound is not null)
        {
            return compound;
        }

        if (SessionSoloTrainingMode && await TryDispatchSoloNaturalCommandAsync(line))
        {
            return null;
        }

        // Never label a known callsign (partial or complete match) as the bad command —
        // focus the error on the verb that follows it.
        CommandErrorFormatter.Result commandError = CommandErrorFormatter.Format(line.Command, parseFailure, line.Scheme, Aircraft);
        _log.LogWarning("Command '{Verb}' {Reason} in input '{Input}'", commandError.Verb, commandError.Reason, line.Command);
        line.Sink.ReportError(commandError.StatusText);
        return null;
    }

    /// <summary>
    /// Half-strip commands (HSC/HSA/HSD) are dual-mode: when no aircraft is targeted, they run globally with an empty callsign
    /// and the server treats them as freeform. Any other command without a target is answered here.
    /// </summary>
    private async Task SendWithoutTargetAsync(TypedLine line, CompoundParseResult compound)
    {
        if (!(TryGetCanonicalVerb(compound.CanonicalString, out string verb) && IsHalfStripVerb(verb)))
        {
            line.Sink.ReportError("No aircraft matched — type a callsign (or partial) before the command");
            return;
        }

        try
        {
            string canonical = compound.CanonicalString;
            _log.LogDebug("SendCommand (global half-strip): '{Canonical}' (input: '{Input}')", canonical, line.OriginalInput);
            CommandResultDto result = await SubmitCommandAsync("", canonical);

            // Always drop the typed text: even when the server rejects, the RPO has
            // seen the result and should not retype the whole command. The error
            // message surfaces through StatusText and the terminal history.
            line.Sink.AddHistory("", line.OriginalInput);
            line.Sink.ClearInput();

            ReportResult(line.Sink, result, "(half-strip)");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Half-strip command failed");
            line.Sink.ReportError($"Command error: {ex.Message}");
        }
    }

    private async Task SendToTargetAsync(TypedLine line, AircraftModel target, CompoundParseResult compound)
    {
        SelectedAircraft = target;

        VfrGateResult gate = VfrCommandGate.Evaluate(target, compound.CanonicalString, VfrCommandsForIfr);
        if (!gate.Allowed)
        {
            line.Sink.ReportError(gate.RejectionMessage!);
            line.Sink.AddWarning(gate.RejectionMessage!);
            return;
        }

        if (CompoundRejection(target, compound.CanonicalString) is { } rejectMsg)
        {
            line.Sink.ReportError(rejectMsg);
            line.Sink.AddWarning(rejectMsg);
            return;
        }

        try
        {
            string canonical = line.ForceOverride ? $"** {compound.CanonicalString}" : compound.CanonicalString;
            _log.LogDebug("SendCommand: {Callsign} '{Canonical}' (input: '{Input}')", target.Callsign, canonical, line.OriginalInput);
            CommandResultDto result = await SubmitCommandAsync(target.Callsign, canonical);
            NoteVfrBypassIfNeeded(gate, target.Callsign, result.Success);

            if (result.Success)
            {
                RecordCommandMarker(target.Callsign, compound.CanonicalString, result.ServerElapsedSeconds);
            }

            // Always drop the typed text: even when the server rejects (or the pilot
            // soft-fails e.g. RTIS "looking"), the RPO has seen the result and should
            // not retype the whole command. The error surfaces through StatusText and
            // the terminal history. The leading callsign is stripped so up-arrow recall
            // produces the canonical command alone — easy to rerun on a new aircraft.
            line.Sink.AddHistory(
                target.Callsign,
                CommandHistoryFormatter.Format(line.OriginalInput, line.ResolvedCallsign, compound.CanonicalString)
            );
            line.Sink.ClearInput();

            ReportResult(line.Sink, result, target.Callsign);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command failed");
            line.Sink.ReportError($"Command error: {ex.Message}");
        }
    }

    /// <summary>Reports the server's answer: an error with its reason when it rejected the command, else an empty status.</summary>
    private static void ReportResult(ICommandTextSink sink, CommandResultDto result, string contextLabel)
    {
        string status = CommandStatusResolver.Resolve(result, contextLabel);
        if (result.Success)
        {
            sink.ReportStatus(status);
        }
        else
        {
            sink.ReportError(status);
        }
    }

    /// <summary>
    /// Mirror of the server's chained non-compoundable rejection and of its paired takeoff-clearance-plus-turn rejection
    /// (`CTO, R270`, the mis-spelling of `CTO MR270`) — CompoundPolicy is the single shared predicate for both, so the
    /// verdicts cannot drift; this just saves the round-trip and gives the RPO immediate feedback while the typed text is
    /// still in the box. Null when the command may be sent.
    /// </summary>
    private static string? CompoundRejection(AircraftModel target, string canonical)
    {
        var targetFixes = new FixResolutionContext(target.Destination, target.Departure);
        if (CompoundPolicy.FindNonCompoundableInChain(canonical, targetFixes) is { } nonCompoundable)
        {
            return $"{CommandDescriber.DescribeCommand(nonCompoundable)} cannot be part of a chained command";
        }

        return CompoundPolicy.FindTakeoffPairedWithImmediateTurn(canonical, targetFixes) is { } pairedTurn
            ? CompoundPolicy.TakeoffPairedWithImmediateTurnMessage(pairedTurn)
            : null;
    }

    private async Task<bool> TryDispatchSoloNaturalCommandAsync(TypedLine line)
    {
        NaturalCommandNormalization? normalization = await NormalizeSoloNaturalCommandAsync(line);
        if (normalization is null)
        {
            return false;
        }

        // The target first: the mapped command is parsed with its airports, so a fix that is only a CIFP terminal
        // waypoint of them splits and resolves the way the server's parse does.
        AircraftModel? target = ResolveSoloTarget(line, normalization, out string? historyCallsign);
        if (target is null)
        {
            return false;
        }

        CompoundParseResult? mappedCompound = CommandSchemeParser.ParseCompound(
            normalization.CanonicalCommand,
            _preferences.CommandScheme,
            out ParseFailure? mappedFailure,
            new FixResolutionContext(target.Destination, target.Departure)
        );
        if (mappedCompound is null)
        {
            _log.LogDebug(
                "Solo natural mapping did not produce a dispatchable command. Input='{Input}', Mapped='{Mapped}', Reason='{Reason}'",
                line.Command,
                normalization.CanonicalCommand,
                mappedFailure?.Reason ?? "parse failed"
            );
            return false;
        }

        await SendSoloMappedCommandAsync(line, target, mappedCompound, historyCallsign, normalization.UsedLlmFallback);
        return true;
    }

    private Task<NaturalCommandNormalization?> NormalizeSoloNaturalCommandAsync(TypedLine line)
    {
        bool allowLlmFallback = _preferences.SpeechEnabled && _llmService.IsConfigured;
        return NaturalCommandNormalizer.TryNormalizeAsync(
            line.OriginalInput,
            BuildSpeechContext(includeRoom: false, line.Target),
            _ruleMapper,
            allowLlmFallback ? _llmMapper : null,
            allowLlmFallback ? _llmCallsignResolver : null,
            CancellationToken.None
        );
    }

    /// <summary>The aircraft the mapped command goes to: the one it names, else the line's own target; null when neither resolves.</summary>
    private AircraftModel? ResolveSoloTarget(TypedLine line, NaturalCommandNormalization normalization, out string? historyCallsign)
    {
        historyCallsign = line.ResolvedCallsign;
        if (string.IsNullOrWhiteSpace(normalization.Callsign))
        {
            return line.Target;
        }

        historyCallsign = normalization.Callsign;
        return ResolveAircraft(normalization.Callsign, line.Sink);
    }

    private async Task SendSoloMappedCommandAsync(
        TypedLine line,
        AircraftModel target,
        CompoundParseResult mappedCompound,
        string? historyCallsign,
        bool usedLlmFallback
    )
    {
        SelectedAircraft = target;

        VfrGateResult gate = VfrCommandGate.Evaluate(target, mappedCompound.CanonicalString, VfrCommandsForIfr);
        if (!gate.Allowed)
        {
            line.Sink.ReportError(gate.RejectionMessage!);
            line.Sink.AddWarning(gate.RejectionMessage!);
            return;
        }

        try
        {
            string canonical = line.ForceOverride ? $"** {mappedCompound.CanonicalString}" : mappedCompound.CanonicalString;
            _log.LogInformation(
                "Solo natural command mapped: {Input} -> {Callsign} {Canonical} (LLM fallback: {UsedLlmFallback})",
                line.OriginalInput,
                target.Callsign,
                canonical,
                usedLlmFallback
            );
            CommandResultDto result = await SubmitCommandAsync(target.Callsign, canonical);
            NoteVfrBypassIfNeeded(gate, target.Callsign, result.Success);

            if (result.Success)
            {
                RecordCommandMarker(target.Callsign, mappedCompound.CanonicalString, result.ServerElapsedSeconds);
            }

            line.Sink.AddHistory(
                target.Callsign,
                CommandHistoryFormatter.Format(line.OriginalInput, historyCallsign, mappedCompound.CanonicalString)
            );
            line.Sink.ClearInput();

            ReportResult(line.Sink, result, target.Callsign);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Solo natural command failed");
            line.Sink.ReportError($"Command error: {ex.Message}");
        }
    }

    /// <summary>
    /// The main command line as an <see cref="ICommandTextSink"/>: its status line, its text box, the history and terminal. Two
    /// for the same <see cref="Owner"/> are equal, as they write to the same place.
    /// </summary>
    private sealed record CommandLineSink(MainViewModel Owner) : ICommandTextSink
    {
        public void ReportStatus(string status) => Owner.StatusText = status;

        public void ReportError(string message) => Owner.StatusText = message;

        public void ClearInput()
        {
            Owner._commandInput.DismissSuggestions();
            Owner._commandInput.ResetHistoryNavigation();
            Owner.CommandText = "";
        }

        public void AddHistory(string callsign, string command) => Owner.AddHistory(callsign, command);

        public void AddWarning(string message) => Owner.AddWarningEntry(message);
    }

    /// <summary>One typed line as the send steps narrow it down from the text to a command for an aircraft.</summary>
    private sealed class TypedLine(string text, ICommandTextSink sink, CommandScheme scheme)
    {
        public ICommandTextSink Sink { get; } = sink;

        /// <summary>The line once the <c>** </c> and <c>CRC </c> prefixes are off.</summary>
        public string Text { get; set; } = text;

        public bool ForceOverride { get; set; }

        public CommandScheme Scheme { get; } = scheme;

        /// <summary>The line parsed as a single command, read for global verbs; null when it is none.</summary>
        public ParsedInput? GlobalParsed { get; set; }

        /// <summary>The line before macro expansion, as the history and the logs show it.</summary>
        public string OriginalInput { get; set; } = "";

        /// <summary>The command to parse: macros expanded, the callsign prefix off, callsign arguments rewritten.</summary>
        public string Command { get; set; } = "";

        public AircraftModel? Target { get; set; }

        /// <summary>The callsign the typed prefix named, null when the line named none.</summary>
        public string? ResolvedCallsign { get; set; }
    }
}
