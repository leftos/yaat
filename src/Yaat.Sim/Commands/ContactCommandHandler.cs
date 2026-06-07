using System.Globalization;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handles CT (contact next controller) and FCA (frequency change approved). Both are pure
/// pilot-speech commands — no state mutation. Distinct from HOO/ACCEPT/DROP which are radar-side
/// coordination actions invisible to the pilot (FAA 7110.65 §7-6-11).
///
/// <para>CT supports three argument forms for disambiguation:</para>
/// <list type="bullet">
///   <item><description><b>Position callsign</b> ("OAK_TWR") — exact match. Use this when multiple
///   positions share a STARS scope (e.g. OAK_TWR and OAK_GND both on TCP 3O).</description></item>
///   <item><description><b>Frequency in MHz</b> ("121.9", "128.525") — tolerance ±5 kHz.</description></item>
///   <item><description><b>TCP code</b> ("3O", "2B") — convenient when unambiguous; first match wins.</description></item>
/// </list>
///
/// <para>With no argument, auto-resolves to <c>Track.HandoffPeer</c> (HOO initiated, awaiting/just
/// accepted) or <c>Track.Owner</c> after <c>HandoffAccepted</c> flips true. Rejects when neither
/// is available.</para>
/// </summary>
public static class ContactCommandHandler
{
    public static CommandResult HandleContact(ContactCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        string facilityName;
        string handoffDetail;
        double? frequencyMhz;

        if (cmd.Target is { Length: > 0 } target)
        {
            var resolution = ResolveExplicitTarget(target, ctx.ArtccConfig);
            switch (resolution)
            {
                case ResolvedTarget.NotFound:
                    return new CommandResult(false, $"unknown position {target}");
                case ResolvedTarget.Ambiguous a:
                    return new CommandResult(
                        false,
                        $"ambiguous TCP {target.ToUpperInvariant()} — try {string.Join(" or ", a.Candidates.Select(p => p.Callsign))}"
                    );
                case ResolvedTarget.Found found:
                    facilityName = ResolveFacilityName(found.Position);
                    frequencyMhz = found.Position.Frequency / 1_000_000.0;
                    handoffDetail = found.Position.Callsign;
                    break;
                default:
                    return new CommandResult(false, $"unknown position {target}");
            }
        }
        else
        {
            var owner = aircraft.Track.HandoffPeer ?? (aircraft.Track.HandoffAccepted ? aircraft.Track.Owner : null);
            if (owner is null)
            {
                return new CommandResult(false, "no handoff target — issue HOO first or specify position");
            }
            var pos = ctx.ArtccConfig?.FindPositionByCallsign(owner.Callsign);
            facilityName = pos is not null ? ResolveFacilityName(pos) : FacilityShortname.From(owner.Callsign);
            frequencyMhz = pos is not null ? pos.Frequency / 1_000_000.0 : null;
            handoffDetail = owner.Callsign;
        }

        var pilotSpeech = frequencyMhz is double freq
            ? PilotResponder.BuildContactReadback(aircraft, facilityName, freq)
            : BuildContactReadbackNoFreq(aircraft, facilityName);
        var warning = $"[Contact] {facilityName}" + (frequencyMhz is double f ? $" {f:0.000}" : "");
        Route(aircraft, ctx, pilotSpeech, warning);
        StampHandoffCompletion(aircraft, ctx, handoffDetail);
        return new CommandResult(true, "");
    }

    // Prefer the position's published RadioName ("NorCal Approach", "Oakland Tower") over the
    // generic FacilityShortname ("Approach", "Tower") so the readback identifies the actual
    // facility. Falls back when RadioName is empty (rare in well-formed vNAS configs).
    private static string ResolveFacilityName(PositionConfig position)
    {
        return string.IsNullOrWhiteSpace(position.RadioName) ? FacilityShortname.From(position.Callsign) : position.RadioName.Trim();
    }

    public static CommandResult HandleFrequencyChangeApproved(AircraftState aircraft, DispatchContext ctx)
    {
        var pilotSpeech = PilotResponder.BuildFrequencyChangeApproved(aircraft);
        Route(aircraft, ctx, pilotSpeech, "[FCA] frequency change approved");
        StampHandoffCompletion(aircraft, ctx, detail: null);
        return new CommandResult(true, "");
    }

    // First CT/FCA on this aircraft owns the completion stamp. A controller re-issuing CT
    // after a HOO bounce should not overwrite the original handoff time / target.
    private static void StampHandoffCompletion(AircraftState aircraft, DispatchContext ctx, string? detail)
    {
        if (aircraft.CompletionReason != Training.CompletionReason.Active)
        {
            return;
        }

        aircraft.CompletedAtSeconds = ctx.ScenarioElapsedSeconds;
        aircraft.CompletionReason = Training.CompletionReason.HandedOff;
        aircraft.CompletionDetail = detail;
    }

    private static void Route(AircraftState aircraft, DispatchContext ctx, Pilot.PilotSpeechText pilotSpeech, string warningText)
    {
        // Mark frequency change in both solo and RPO modes. Track ownership and comms
        // are independent (an auto-track to departure does not mean the pilot is on
        // departure's freq yet), so behavior that depends on "the controller has actually
        // handed comms off" — e.g. InitialClimbPhase releasing a radar-vectors SID heading
        // hold — must key off this flag rather than Track.Owner.
        aircraft.HasLeftStudentFrequency = true;

        if (ctx.SoloTrainingMode)
        {
            PilotResponder.QueueSoloPilotTransmission(aircraft, pilotSpeech, PilotTransmissionKind.Readback, PilotResponder.SourceResponse);
            return;
        }
        PilotResponder.RouteRpoTransmission(aircraft, ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, pilotSpeech.Tts, warningText);
    }

    private static Pilot.PilotSpeechText BuildContactReadbackNoFreq(AircraftState aircraft, string facilityName)
    {
        var spoken = Yaat.Sim.Speech.CallsignParser.IcaoToSpoken(aircraft.Callsign);
        return new Pilot.PilotSpeechText($"{facilityName}, so long.", $"{facilityName}, {spoken}, so long.");
    }

    private abstract record ResolvedTarget
    {
        public sealed record Found(PositionConfig Position) : ResolvedTarget;

        public sealed record Ambiguous(IReadOnlyList<PositionConfig> Candidates) : ResolvedTarget;

        public sealed record NotFound : ResolvedTarget;
    }

    private static ResolvedTarget ResolveExplicitTarget(string target, ArtccConfigRoot? config)
    {
        if (config is null)
        {
            return new ResolvedTarget.NotFound();
        }
        var trimmed = target.Trim();

        if (LooksLikeFrequency(trimmed) && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
        {
            var byFreq = config.FindPositionByFrequency(mhz);
            return byFreq is null ? new ResolvedTarget.NotFound() : new ResolvedTarget.Found(byFreq);
        }

        if (trimmed.Contains('_'))
        {
            var byCallsign = config.FindPositionByCallsign(trimmed.ToUpperInvariant());
            return byCallsign is null ? new ResolvedTarget.NotFound() : new ResolvedTarget.Found(byCallsign);
        }

        // TCP-code path can resolve to multiple positions (consolidated TWR + GND on shared
        // STARS scope). Force the controller to disambiguate rather than silently picking one.
        var byTcp = config.FindPositionsByTcpCodeAnyFacility(trimmed.ToUpperInvariant());
        return byTcp.Count switch
        {
            0 => new ResolvedTarget.NotFound(),
            1 => new ResolvedTarget.Found(byTcp[0]),
            _ => new ResolvedTarget.Ambiguous(byTcp),
        };
    }

    private static bool LooksLikeFrequency(string s)
    {
        // "121.9" / "128.525" — three digits, dot, one or more digits, in the VHF aviation band.
        var dot = s.IndexOf('.');
        if (dot < 1 || dot >= s.Length - 1)
        {
            return false;
        }
        for (int i = 0; i < s.Length; i++)
        {
            if (i == dot)
            {
                continue;
            }
            if (!char.IsDigit(s[i]))
            {
                return false;
            }
        }
        return true;
    }
}
