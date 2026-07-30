using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Outcome of checking a typed command against the controller's "VFR commands for IFR aircraft"
/// setting. <see cref="RejectionMessage"/> is set only when <see cref="Allowed"/> is false.
/// <see cref="BypassedForIfr"/> is true when the command models a VFR-only operation that the
/// setting let through for an IFR aircraft — the caller surfaces an advisory the first time it
/// happens for a given aircraft.
/// </summary>
public sealed record VfrGateResult(bool Allowed, string? RejectionMessage, bool BypassedForIfr)
{
    public static readonly VfrGateResult Pass = new(true, null, false);
}

/// <summary>
/// Enforces the VFR-only command restriction for typed commands, before they reach the wire.
///
/// <para>The simulation applies whatever it is given — it does not gate on IFR-vs-VFR — so this
/// check and <see cref="AircraftCommandApplicability"/> (which suppresses the equivalent
/// right-click menu items) are the whole of the restriction. The classification itself lives in
/// <see cref="VfrCommandPolicy"/> so the client and the command grammar cannot drift apart.</para>
/// </summary>
public static class VfrCommandGate
{
    /// <summary>Force-override prefix the RPO can put in front of a command.</summary>
    private const string ForcePrefix = "**";

    /// <summary>
    /// Checks a canonical command string against <paramref name="mode"/> for the given target.
    /// Passes when the target is unknown or VFR. A leading <c>**</c> force-override is stripped
    /// first: the free-text "Command…" flyout and favorites hand this method text the controller
    /// typed, which may carry the prefix.
    ///
    /// <para>Also passes when the string does not parse. The server owns the grammar and produces
    /// the better error, so an unparseable command is forwarded rather than swallowed here. Note the
    /// coupling that implies: <c>MainViewModel</c> produces the canonical through
    /// <c>CommandSchemeParser</c> and this re-parses it through <see cref="CommandParser"/>. If those
    /// two grammars ever diverge, a VFR-only command the second parser can't recognize reaches an
    /// IFR aircraft ungated — such a command would not dispatch server-side either, but a change to
    /// either parser should keep them in step.</para>
    /// </summary>
    public static VfrGateResult Evaluate(AircraftModel? target, string canonicalCommand, VfrCommandsForIfr mode)
    {
        if (target is null || AircraftCommandApplicability.IsVfr(target))
        {
            return VfrGateResult.Pass;
        }

        var text = canonicalCommand.TrimStart();
        if (text.StartsWith(ForcePrefix, StringComparison.Ordinal))
        {
            text = text[ForcePrefix.Length..];
        }

        var parsed = CommandParser.ParseCompound(text);
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            return VfrGateResult.Pass;
        }

        bool bypassed = false;
        foreach (var block in parsed.Value.Blocks)
        {
            foreach (var command in block.Commands)
            {
                if (!VfrCommandPolicy.IsVfrOnly(command))
                {
                    continue;
                }

                if (!VfrCommandPolicy.AllowsForIfr(command, mode))
                {
                    return new VfrGateResult(false, BuildRejection(target, command), false);
                }

                bypassed = true;
            }
        }

        return bypassed ? new VfrGateResult(true, null, true) : VfrGateResult.Pass;
    }

    /// <summary>
    /// Advisory shown the first time a VFR-only command is accepted for a given IFR aircraft, so
    /// the acceptance is visible rather than silent. The aircraft's flight rules are not changed.
    /// </summary>
    public static string BuildBypassAdvisory(string callsign) =>
        $"{callsign} is IFR — VFR-only command accepted under your \"VFR commands for IFR aircraft\" setting. Flight rules unchanged.";

    private static string BuildRejection(AircraftModel target, ParsedCommand command)
    {
        var verb = CommandDescriber.DescribeCommand(command);
        return $"{verb} requires a VFR aircraft — use CIFR on {target.Callsign} first, "
            + "or change Settings > Scenarios > VFR commands for IFR aircraft.";
    }
}
