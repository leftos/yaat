using System.Globalization;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handles CT (contact next controller) and FCA (frequency change approved). Both are pilot-speech
/// commands whose only state is the comms bookkeeping (the position the pilot is tuned to,
/// <see cref="AircraftState.TunedPosition"/>, and the handoff completion stamp, both student-origin only, except that a
/// scripted or AI contact to the student's position puts the pilot on the student's frequency).
/// Distinct from HOO/ACCEPT/DROP which are radar-side coordination actions invisible to the pilot (FAA 7110.65 §7-6-11).
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
        TrackOwner tunedTo;

        if (cmd.Target is { Length: > 0 } target)
        {
            ResolvedTarget resolution = ResolveExplicitTarget(target, ctx.ArtccConfig, ctx.FacilityHint);
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
                    tunedTo = ctx.ArtccConfig?.ResolvePosition(found.Position.Id) ?? TrackOwner.Elsewhere;
                    break;
                default:
                    return new CommandResult(false, $"unknown position {target}");
            }
        }
        else
        {
            TrackOwner? owner = aircraft.Track.HandoffPeer ?? (aircraft.Track.HandoffAccepted ? aircraft.Track.Owner : null);
            if (owner is null)
            {
                return new CommandResult(false, "no handoff target — issue HOO first or specify position");
            }
            PositionConfig? pos = ctx.ArtccConfig?.FindPositionByCallsign(owner.Callsign, owner.FacilityId);
            facilityName = pos is not null ? ResolveFacilityName(pos) : FacilityShortname.From(owner.Callsign);
            frequencyMhz = pos is not null ? pos.Frequency / 1_000_000.0 : null;
            handoffDetail = owner.Callsign;
            tunedTo = owner;
        }

        string? targetType = AtcPositionTypeClassifier.Classify(handoffDetail);
        if (string.Equals(targetType, "GND", StringComparison.OrdinalIgnoreCase))
        {
            aircraft.Ground.ReleasedToGround = true;
        }

        PilotSpeechText pilotSpeech = BuildContactReadback(aircraft, facilityName, frequencyMhz, ctx.SpeechContextFor(aircraft));
        PilotSpeechText canonicalSpeech = BuildContactReadback(aircraft, facilityName, frequencyMhz, PilotSpeechContext.Canonical);
        string warning = $"[Contact] {facilityName}" + (frequencyMhz is double f ? $" {f:0.000}" : "");
        bool toTowerPosition = string.Equals(targetType, "TWR", StringComparison.OrdinalIgnoreCase);
        MarkFrequencyChange(aircraft, ctx, toTowerPosition, tunedTo);
        RouteReadback(aircraft, ctx, pilotSpeech, canonicalSpeech, warning);
        StampHandoffCompletion(aircraft, ctx, handoffDetail);
        return new CommandResult(true, "");
    }

    // Prefer the position's published RadioName ("NorCal Approach", "Oakland Tower") over the
    // generic FacilityShortname ("Approach", "Tower") so the readback identifies the actual
    // facility. Falls back when RadioName is empty (rare in well-formed vNAS configs).
    private static string ResolveFacilityName(PositionConfig position) =>
        string.IsNullOrWhiteSpace(position.RadioName) ? FacilityShortname.From(position.Callsign) : position.RadioName.Trim();

    public static CommandResult HandleFrequencyChangeApproved(AircraftState aircraft, DispatchContext ctx)
    {
        aircraft.Ground.ReleasedToGround = true;
        PilotSpeechText pilotSpeech = PilotResponder.BuildFrequencyChangeApproved(aircraft, ctx.SpeechContextFor(aircraft));
        PilotSpeechText canonicalSpeech = PilotResponder.BuildFrequencyChangeApproved(aircraft, PilotSpeechContext.Canonical);
        // An FCA names no position: on the ground it counts as a transfer to the tower.
        MarkFrequencyChange(aircraft, ctx, towardTheTower: true, TrackOwner.Elsewhere);
        RouteReadback(aircraft, ctx, pilotSpeech, canonicalSpeech, "[FCA] frequency change approved");
        StampHandoffCompletion(aircraft, ctx, detail: null);
        return new CommandResult(true, "");
    }

    // First CT/FCA on this aircraft owns the completion stamp. A controller re-issuing CT
    // after a HOO bounce should not overwrite the original handoff time / target. A scripted or
    // AI-controller CT is not the student handing the aircraft off (an AI Ground sends a taxiing
    // departure *to* the tower), so it never ends the aircraft's session-report life.
    private static void StampHandoffCompletion(AircraftState aircraft, DispatchContext ctx, string? detail)
    {
        if (ctx.IsScenarioScripted || (aircraft.CompletionReason != Training.CompletionReason.Active))
        {
            return;
        }

        aircraft.CompletedAtSeconds = ctx.ScenarioElapsedSeconds;
        aircraft.CompletionReason = Training.CompletionReason.HandedOff;
        aircraft.CompletionDetail = detail;
    }

    private static PilotSpeechText BuildContactReadback(
        AircraftState aircraft,
        string facilityName,
        double? frequencyMhz,
        PilotSpeechContext speechContext
    ) =>
        frequencyMhz is double freq
            ? PilotResponder.BuildContactReadback(aircraft, facilityName, freq, speechContext)
            : BuildContactReadbackNoFreq(aircraft, facilityName, speechContext);

    private static void MarkFrequencyChange(AircraftState aircraft, DispatchContext ctx, bool towardTheTower, TrackOwner tunedTo)
    {
        // Retune the pilot to tunedTo in both solo and RPO modes. Track ownership and comms
        // are independent (an auto-track to departure does not mean the pilot is on
        // departure's freq yet), so behavior that depends on "the controller has actually
        // handed comms off" — e.g. InitialClimbPhase releasing a radar-vectors SID heading
        // hold — must key off the tuned position rather than Track.Owner. Only the student's own CT
        // moves the pilot off the student's frequency: a scripted or AI-controller CT is
        // another position's transfer. One naming the student's position (an AI Ground sending the
        // pilot to the student tower) puts the pilot on the student's frequency; any other leaves it. The student is matched
        // by callsign alone: a config can list the student's callsign under two facilities (ZOA's OAK_TWR under NCT and
        // O90), and a contact resolving to the other facility's entry is still the student's position.
        if (!ctx.IsScenarioScripted)
        {
            aircraft.TunedPosition = tunedTo;
        }
        else if ((ctx.StudentPosition is { } student) && string.Equals(tunedTo.Callsign, student.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            aircraft.TunedPosition = null;
        }

        // The student's first transfer of a ground aircraft toward the tower (a CT to a tower position, or an FCA) starts the
        // simulated tower's takeoff clock; a later transfer keeps the first stamp.
        if (!ctx.IsScenarioScripted && aircraft.IsOnGround && towardTheTower && (aircraft.Ground.LeftStudentFrequencyAtSeconds is null))
        {
            aircraft.Ground.LeftStudentFrequencyAtSeconds = ctx.ScenarioElapsedSeconds;
        }
    }

    /// <summary>
    /// Queues the pilot's read-back of the transfer, spoken as <paramref name="pilotSpeech"/> and metered as long as
    /// <paramref name="canonicalSpeech"/>, or shows it to the RPO outside solo training.
    /// </summary>
    private static void RouteReadback(
        AircraftState aircraft,
        DispatchContext ctx,
        PilotSpeechText pilotSpeech,
        PilotSpeechText canonicalSpeech,
        string warningText
    )
    {
        if (ctx.SoloTrainingMode)
        {
            PilotResponder.QueueSoloPilotTransmission(aircraft, pilotSpeech, canonicalSpeech, PilotTransmissionKind.Readback, initialContact: false);
            return;
        }
        PilotResponder.RouteRpoTransmission(aircraft, ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, pilotSpeech, warningText);
    }

    private static Pilot.PilotSpeechText BuildContactReadbackNoFreq(AircraftState aircraft, string facilityName, PilotSpeechContext speechContext)
    {
        string spoken = PilotResponder.SpokenReadbackCallsign(aircraft, speechContext);
        return new Pilot.PilotSpeechText($"{facilityName}, so long.", $"{facilityName}, {spoken}, so long.");
    }

    public abstract record ResolvedTarget
    {
        public sealed record Found(PositionConfig Position) : ResolvedTarget;

        public sealed record Ambiguous(IReadOnlyList<PositionConfig> Candidates) : ResolvedTarget;

        public sealed record NotFound : ResolvedTarget;
    }

    public static ResolvedTarget ResolveExplicitTarget(string target, ArtccConfigRoot? config, string? facilityHint)
    {
        if (config is null)
        {
            return new ResolvedTarget.NotFound();
        }
        string trimmed = target.Trim();

        if (LooksLikeFrequency(trimmed) && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
        {
            PositionConfig? byFreq = config.FindPositionByFrequency(mhz);
            return byFreq is null ? new ResolvedTarget.NotFound() : new ResolvedTarget.Found(byFreq);
        }

        if (trimmed.Contains('_'))
        {
            PositionConfig? byCallsign = config.FindPositionByCallsign(trimmed.ToUpperInvariant(), facilityHint);
            return byCallsign is null ? new ResolvedTarget.NotFound() : new ResolvedTarget.Found(byCallsign);
        }

        // TCP-code path can resolve to multiple positions (consolidated TWR + GND on shared
        // STARS scope). Force the controller to disambiguate rather than silently picking one.
        IReadOnlyList<PositionConfig> byTcp = config.FindPositionsByTcpCodeAnyFacility(trimmed.ToUpperInvariant());
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
        int dot = s.IndexOf('.');
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
