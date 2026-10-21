using System.Globalization;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>How the check line draws one of its pieces.</summary>
public enum AdvisoryTone
{
    Plain,
    Hint,
    Callsign,
    Good,
    Off,
    Bad,
}

/// <summary>One piece of the check line: its words and how they are drawn.</summary>
public sealed record AdvisorySegment(string Text, AdvisoryTone Tone);

/// <summary>Where the live check stands with the call typed so far.</summary>
public enum AdvisoryCheckState
{
    /// <summary>Nothing to say: the commanded aircraft is not among the aircraft the client holds.</summary>
    None,
    Empty,
    Filter,
    Incomplete,
    Invalid,
    Refused,
    Resolved,
}

/// <summary>
/// The live check's verdict on the call: the line it shows, as pieces (<see cref="Segments"/>) and as one string
/// (<see cref="Text"/>), and the aircraft the call resolves to.
/// </summary>
public sealed record AdvisoryCheck(AdvisoryCheckState State, IReadOnlyList<AdvisorySegment> Segments, string? ResolvedCallsign)
{
    /// <summary>The line as one string, its pieces joined by a space.</summary>
    public string Text => string.Join(' ', Segments.Select(segment => segment.Text));
}

/// <summary>What the ghost under the box offers: its words, the word Tab adds, or the callsign Tab picks.</summary>
public sealed record AdvisoryGhost(string Text, string? Token, string? PickCallsign);

/// <summary>
/// The solo advisory's model: the live check (<see cref="Check"/>), which runs the sim's own traffic-advisory matcher over the
/// aircraft the client holds and grades each part; the true parts of a pointed aircraft for each form (<see cref="TruthOf"/>),
/// which seed Tab (<see cref="Ghost"/>) as the pre-fill setting allows; why a form is greyed for it; and the spoken line.
/// </summary>
public static class SoloAdvisoryModel
{
    /// <summary>The most reporting points the over form offers for one aircraft.</summary>
    public const int MaxReportingPoints = 6;

    /// <summary>The check line with nothing typed.</summary>
    public const string EmptyHint = "type the call as you would say it";

    /// <summary>The check line's verdict when no aircraft fits the call.</summary>
    public const string NoFit = "✗ no traffic fits";

    /// <summary>The ghost once every part is set.</summary>
    public const string CompleteGhost = "complete · Enter sends";

    /// <summary>The ghost for a filter matching no row.</summary>
    public const string NoPickGhost = "no callsign or type matches";

    /// <summary>
    /// The live check of <paramref name="reading"/> for <paramref name="recipientCallsign"/> against
    /// <paramref name="candidates"/>, every aircraft the client holds: the hint, the filter, the parts still needed, the sim's
    /// parse refusal, no fit with the sim's reply, or the aircraft the call resolves to with a mark per part. A reporting point is
    /// placed with <paramref name="fixPosition"/>.
    /// </summary>
    public static AdvisoryCheck Check(
        AdvisoryReading reading,
        string recipientCallsign,
        IReadOnlyList<TrafficAdvisoryCandidate> candidates,
        Func<string, LatLon?> fixPosition
    )
    {
        if (reading.IsEmpty)
        {
            return new AdvisoryCheck(AdvisoryCheckState.Empty, [new AdvisorySegment(EmptyHint, AdvisoryTone.Hint)], null);
        }

        if (reading.FilterText is { } filter)
        {
            return new AdvisoryCheck(
                AdvisoryCheckState.Filter,
                [new AdvisorySegment("filtering the list by", AdvisoryTone.Plain), new AdvisorySegment(filter, AdvisoryTone.Callsign)],
                null
            );
        }

        if (!reading.IsComplete)
        {
            string needs = $"needs {string.Join(' ', reading.Missing.Select(SoloAdvisoryText.Gap))}";
            return new AdvisoryCheck(AdvisoryCheckState.Incomplete, [new AdvisorySegment(needs, AdvisoryTone.Plain)], null);
        }

        ParseResult<ParsedCommand> parsed = SoloAdvisoryText.Parse(reading);
        if (parsed.Value is not { } command)
        {
            return new AdvisoryCheck(AdvisoryCheckState.Invalid, [new AdvisorySegment($"✗ {ReasonText(parsed.Reason)}", AdvisoryTone.Bad)], null);
        }

        TrafficAdvisoryCandidate? recipient = candidates.FirstOrDefault(c => string.Equals(c.Callsign, recipientCallsign, StringComparison.Ordinal));
        if (recipient is null)
        {
            return new AdvisoryCheck(AdvisoryCheckState.None, [], null);
        }

        (TrafficAdvisoryTargetMatch? match, string error, List<AdvisorySegment> marks) = Resolve(command, recipient, candidates, fixPosition);
        if (match is null)
        {
            return new AdvisoryCheck(
                AdvisoryCheckState.Refused,
                [new AdvisorySegment(NoFit, AdvisoryTone.Bad), new AdvisorySegment($"the sim would answer “{error}”", AdvisoryTone.Plain)],
                null
            );
        }

        List<AdvisorySegment> line =
        [
            new("→", AdvisoryTone.Plain),
            new(match.Target.Callsign, AdvisoryTone.Callsign),
            .. marks,
            new((match.Quality == AdvisoryMatchQuality.Exact) ? "· scores clean" : "· imprecise", AdvisoryTone.Plain),
        ];
        return new AdvisoryCheck(AdvisoryCheckState.Resolved, line, match.Target.Callsign);
    }

    /// <summary>
    /// The line the pilot hears for a complete call the sim parses, from the sim's own phrase builders; null for any other call.
    /// </summary>
    public static string? Spoken(AdvisoryReading reading)
    {
        if (!reading.IsComplete)
        {
            return null;
        }

        return SoloAdvisoryText.Parse(reading).Value switch
        {
            ReportTrafficAdvisoryCommand { Details: { } details } => CommandDescriber.FormatTrafficAdvisoryPhrase(details),
            ReportTrafficRelativeCommand { Details: { } details } => CommandDescriber.FormatTrafficRelativePhrase(details),
            ReportTrafficPatternCommand { Details: { } details } => CommandDescriber.FormatTrafficPatternPhrase(details),
            ReportTrafficLandmarkCommand { Details: { } details } => CommandDescriber.FormatTrafficLandmarkPhrase(details),
            _ => null,
        };
    }

    /// <summary>
    /// The true parts of <paramref name="target"/> seen from <paramref name="recipient"/> in <paramref name="form"/>, as the box
    /// would take them: the clock, miles, direction, type and altitude (<c>035</c>); the position off the nose, miles and type;
    /// the leg, side, miles, runway and type of its pattern position; the nearest of <paramref name="reportingPoints"/> and the
    /// type. Null where the form does not apply: no pattern position or runway, or no reporting point near.
    /// </summary>
    public static IReadOnlyDictionary<AdvisoryPart, string>? TruthOf(
        AdvisoryForm form,
        TrafficAdvisoryCandidate recipient,
        TrafficAdvisoryCandidate target,
        IReadOnlyList<string> reportingPoints
    ) =>
        form switch
        {
            AdvisoryForm.Radar => new Dictionary<AdvisoryPart, string>
            {
                [AdvisoryPart.Clock] = Whole(TrafficAdvisoryMatcher.ClockFrom(recipient, target)),
                [AdvisoryPart.Miles] = Whole(TrafficAdvisoryMatcher.MilesFrom(recipient, target)),
                [AdvisoryPart.Direction] = TrafficAdvisoryMatcher.DirectionFrom(target),
                [AdvisoryPart.Type] = target.BaseAircraftType,
                [AdvisoryPart.Altitude] = Hundreds(target.AltitudeFt).ToString("000", CultureInfo.InvariantCulture),
            },
            AdvisoryForm.Nose => new Dictionary<AdvisoryPart, string>
            {
                [AdvisoryPart.Position] = SoloAdvisoryText.Positions[TrafficAdvisoryMatcher.RelativeBearingOctant(recipient, target)],
                [AdvisoryPart.Miles] = Whole(TrafficAdvisoryMatcher.MilesFrom(recipient, target)),
                [AdvisoryPart.Type] = target.BaseAircraftType,
            },
            AdvisoryForm.Pattern => PatternTruth(target),
            _ => (reportingPoints.Count == 0)
                ? null
                : new Dictionary<AdvisoryPart, string> { [AdvisoryPart.Fix] = reportingPoints[0], [AdvisoryPart.Type] = target.BaseAircraftType },
        };

    /// <summary>
    /// Why <paramref name="form"/> is greyed for <paramref name="callsign"/>, whose true parts in it are <paramref name="truth"/>:
    /// <c>N4417Q is in no pattern leg</c>, <c>N4417Q is not within 2 NM of a reporting point</c>; null when the form applies.
    /// </summary>
    public static string? UnavailableReason(AdvisoryForm form, string callsign, IReadOnlyDictionary<AdvisoryPart, string>? truth) =>
        (truth is not null)
            ? null
            : form switch
            {
                AdvisoryForm.Pattern => $"{callsign} is in no pattern leg",
                AdvisoryForm.Over => $"{callsign} is not within {LandmarkRadiusText} NM of a reporting point",
                _ => null,
            };

    /// <summary>The matcher's landmark radius (<see cref="TrafficAdvisoryMatcher.LandmarkRadiusNm"/>) as the flyout writes it: <c>2</c>.</summary>
    public static string LandmarkRadiusText => TrafficAdvisoryMatcher.LandmarkRadiusNm.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the pre-fill setting's "left for the student" withholds <paramref name="part"/> of <paramref name="form"/>: the
    /// clock, miles and direction; the position and miles; the leg, side and miles; the reporting point.
    /// </summary>
    public static bool IsJudged(AdvisoryForm form, AdvisoryPart part) =>
        form switch
        {
            AdvisoryForm.Radar => part is AdvisoryPart.Clock or AdvisoryPart.Miles or AdvisoryPart.Direction,
            AdvisoryForm.Nose => part is AdvisoryPart.Position or AdvisoryPart.Miles,
            AdvisoryForm.Pattern => part is AdvisoryPart.Leg or AdvisoryPart.Side or AdvisoryPart.Miles,
            _ => part is AdvisoryPart.Fix,
        };

    /// <summary>
    /// The ghost under the box for <paramref name="reading"/>, a call (not a filter, <see cref="PickGhost"/>) in
    /// <paramref name="form"/>, the form typed or, with nothing typed, the builder's: once complete, <see cref="CompleteGhost"/>;
    /// else the next part, which Tab fills from <paramref name="pointedTruth"/>, the pointed aircraft's true parts in that form,
    /// unless the pre-fill setting leaves it for the student; with no aircraft pointed at (<paramref name="hasPointed"/>), a hint
    /// to point at one. With nothing typed, the over form's first part comes with its <c>OVER</c>.
    /// </summary>
    public static AdvisoryGhost Ghost(
        AdvisoryReading reading,
        AdvisoryForm form,
        IReadOnlyDictionary<AdvisoryPart, string>? pointedTruth,
        bool hasPointed,
        SoloAdvisoryPrefill prefill
    )
    {
        AdvisoryPart? next = reading.IsEmpty ? SoloAdvisoryText.OrderOf(form, false)[0] : reading.Next;
        if (next is not { } part)
        {
            return new AdvisoryGhost(CompleteGhost, null, null);
        }

        string lead = (reading.IsEmpty && (form == AdvisoryForm.Over)) ? "OVER " : "";
        string optional = SoloAdvisoryText.IsOptional(part) ? " (optional)" : "";
        bool offered = (prefill == SoloAdvisoryPrefill.TrueValues) || !IsJudged(form, part);
        if (offered && (pointedTruth is not null) && pointedTruth.TryGetValue(part, out string? token))
        {
            return new AdvisoryGhost($"Tab {lead}{token} {SoloAdvisoryText.KindWord(part)}{optional}", $"{lead}{token}", null);
        }

        string help = hasPointed ? "" : " · point at a row for help";
        return new AdvisoryGhost($"next: {lead}{SoloAdvisoryText.Gap(part)}{optional}{help}", null, null);
    }

    /// <summary>
    /// The ghost for the filter <paramref name="filter"/>: the row Tab picks among <paramref name="rows"/>, the rows the list
    /// shows for it (callsigns containing the text, then types starting with it); <see cref="NoPickGhost"/> for none.
    /// </summary>
    public static AdvisoryGhost PickGhost(string filter, IReadOnlyList<MenuAirborneTrafficRow> rows)
    {
        MenuAirborneTrafficRow? pick =
            rows.FirstOrDefault(row => row.Callsign.Contains(filter, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => row.Traffic.AircraftType.StartsWith(filter, StringComparison.OrdinalIgnoreCase));
        return (pick is null) ? new AdvisoryGhost(NoPickGhost, null, null) : new AdvisoryGhost($"Tab picks {pick.Callsign}", null, pick.Callsign);
    }

    /// <summary>
    /// The reporting points within the matcher's landmark radius (<see cref="TrafficAdvisoryMatcher.LandmarkRadiusNm"/>) of
    /// <paramref name="position"/> among <paramref name="near"/> (every fix, nearest first), at most <see cref="MaxReportingPoints"/>.
    /// </summary>
    public static IReadOnlyList<string> ReportingPoints(IEnumerable<(string Name, double DistanceNm)> near) =>
        [.. near.TakeWhile(fix => fix.DistanceNm <= TrafficAdvisoryMatcher.LandmarkRadiusNm).Take(MaxReportingPoints).Select(fix => fix.Name)];

    private static (TrafficAdvisoryTargetMatch? Match, string Error, List<AdvisorySegment> Marks) Resolve(
        ParsedCommand command,
        TrafficAdvisoryCandidate recipient,
        IReadOnlyList<TrafficAdvisoryCandidate> candidates,
        Func<string, LatLon?> fixPosition
    )
    {
        TrafficAdvisoryTargetMatch? match;
        string error;
        switch (command)
        {
            case ReportTrafficAdvisoryCommand { Details: { } radar }:
                match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(recipient, radar, candidates, out error);
                return (match, error, (match is null) ? [] : RadarMarks(recipient, match.Target, radar));
            case ReportTrafficRelativeCommand { Details: { } nose }:
                match = TrafficAdvisoryMatcher.ResolveRelativeTrafficTarget(recipient, nose, candidates, out error);
                return (match, error, (match is null) ? [] : NoseMarks(recipient, match.Target, nose));
            case ReportTrafficPatternCommand { Details: { } pattern }:
                match = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(recipient, pattern, candidates, out error);
                return (match, error, (match is null) ? [] : PatternMarks(match.Target, pattern));
            case ReportTrafficLandmarkCommand { Details: { } over }:
                return ResolveOver(over, recipient, candidates, fixPosition);
            default:
                return (null, "Unable, no traffic specified", []);
        }
    }

    private static (TrafficAdvisoryTargetMatch? Match, string Error, List<AdvisorySegment> Marks) ResolveOver(
        TrafficLandmarkDetails over,
        TrafficAdvisoryCandidate recipient,
        IReadOnlyList<TrafficAdvisoryCandidate> candidates,
        Func<string, LatLon?> fixPosition
    )
    {
        if (fixPosition(over.FixName) is not { } landmark)
        {
            return (null, $"Unable, unknown landmark {over.FixName}", []);
        }

        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveLandmarkTrafficTarget(
            recipient,
            landmark,
            over.AircraftType,
            candidates,
            out string error
        );
        if (match is null)
        {
            return (null, error, []);
        }

        double offNm = GeoMath.DistanceNm(match.Target.Position, landmark);
        AdvisorySegment point = (offNm <= 1.0) ? new("point ✓", AdvisoryTone.Good) : new($"point ≈ {offNm:0.0} NM off", AdvisoryTone.Off);
        return (match, error, [point, TypeMark(match.Target, over.AircraftType)]);
    }

    private static List<AdvisorySegment> RadarMarks(TrafficAdvisoryCandidate recipient, TrafficAdvisoryCandidate target, TrafficAdvisoryDetails radar)
    {
        int clockError = SectorDistance(TrafficAdvisoryMatcher.ClockFrom(recipient, target), radar.Clock);
        int milesError = Math.Abs(TrafficAdvisoryMatcher.MilesFrom(recipient, target) - radar.Miles);
        int directionError = OctantDistance(
            IndexOf(SoloAdvisoryText.Directions, TrafficAdvisoryMatcher.DirectionFrom(target)),
            IndexOf(SoloAdvisoryText.Directions, radar.Direction)
        );
        List<AdvisorySegment> marks =
        [
            Mark("clock", clockError, 1, " h"),
            Mark("miles", milesError, 1, " NM"),
            Mark("direction", directionError, 0, " octant"),
        ];
        if (radar.Altitude is { } altitude)
        {
            marks.Add(Mark("altitude", Math.Abs((Hundreds(target.AltitudeFt) * 100) - altitude), 100, " ft"));
        }

        marks.Add(TypeMark(target, radar.AircraftType));
        return marks;
    }

    private static List<AdvisorySegment> NoseMarks(TrafficAdvisoryCandidate recipient, TrafficAdvisoryCandidate target, TrafficRelativeDetails nose)
    {
        int positionError = OctantDistance(
            TrafficAdvisoryMatcher.RelativeBearingOctant(recipient, target),
            IndexOf(SoloAdvisoryText.Positions, nose.Position)
        );
        int milesError = Math.Abs(TrafficAdvisoryMatcher.MilesFrom(recipient, target) - nose.Miles);
        return [Mark("position", positionError, 0, " octant"), Mark("miles", milesError, 1, " NM"), TypeMark(target, nose.AircraftType)];
    }

    private static List<AdvisorySegment> PatternMarks(TrafficAdvisoryCandidate target, TrafficPatternDetails pattern)
    {
        double distanceNm =
            target.PatternPosition?.DistanceNm
            ?? throw new InvalidOperationException($"{target.Callsign} matched a pattern advisory without a pattern position.");
        int milesError = Math.Abs((int)Math.Round(distanceNm, MidpointRounding.AwayFromZero) - pattern.Miles);
        return [new AdvisorySegment("leg ✓", AdvisoryTone.Good), Mark("miles", milesError, 1, " NM"), TypeMark(target, pattern.AircraftType)];
    }

    /// <summary>
    /// A part's mark: <c>clock ✓</c>, or within the exact band <c>clock ✓ ±1 h</c>, else <c>clock ≈ 3 h off</c>; the bands are the
    /// matcher's own "exact" bands.
    /// </summary>
    private static AdvisorySegment Mark(string label, int error, int exactBand, string unit)
    {
        if (error > exactBand)
        {
            return new AdvisorySegment($"{label} ≈ {Whole(error)}{unit} off", AdvisoryTone.Off);
        }

        return new AdvisorySegment((error == 0) ? $"{label} ✓" : $"{label} ✓ ±{Whole(error)}{unit}", AdvisoryTone.Good);
    }

    private static AdvisorySegment TypeMark(TrafficAdvisoryCandidate target, string requested) =>
        TrafficAdvisoryMatcher.TypeMatches(target, requested)
            ? new AdvisorySegment("type ✓", AdvisoryTone.Good)
            : new AdvisorySegment($"type ≈ is {target.BaseAircraftType}", AdvisoryTone.Off);

    private static IReadOnlyDictionary<AdvisoryPart, string>? PatternTruth(TrafficAdvisoryCandidate target)
    {
        if ((target.PatternPosition is not { } position) || (target.AssignedRunwayId is not { } runway))
        {
            return null;
        }

        var truth = new Dictionary<AdvisoryPart, string>
        {
            [AdvisoryPart.Leg] = SoloAdvisoryText.LegWord(position.Leg),
            [AdvisoryPart.Miles] = Whole(Math.Max(1, (int)Math.Round(position.DistanceNm, MidpointRounding.AwayFromZero))),
            [AdvisoryPart.Runway] = runway,
            [AdvisoryPart.Type] = target.BaseAircraftType,
        };
        if ((position.Leg != PatternEntryLeg.Final) && (position.Side is { } side))
        {
            truth[AdvisoryPart.Side] = (side == PatternDirection.Left) ? "L" : "R";
        }

        return truth;
    }

    /// <summary>
    /// The parser's reason without the quoted verb it opens with: <c>"RTIS" RTIS descriptive form…</c> reads
    /// <c>RTIS descriptive form…</c>.
    /// </summary>
    private static string ReasonText(string? reason)
    {
        string text = reason ?? "";
        int close = text.StartsWith('"') ? text.IndexOf("\" ", 1, StringComparison.Ordinal) : -1;
        return (close < 0) ? text : text[(close + 2)..];
    }

    private static int Hundreds(double feet) => (int)Math.Round(feet / 100.0, MidpointRounding.AwayFromZero);

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Where <paramref name="word"/> stands among <paramref name="words"/>; a word not among them is a bug in the caller.</summary>
    private static int IndexOf(IReadOnlyList<string> words, string word)
    {
        int index = words.ToList().IndexOf(word.Trim().ToUpperInvariant());
        return (index >= 0) ? index : throw new ArgumentException($"'{word}' is not one of {string.Join(' ', words)}.", nameof(word));
    }

    private static int SectorDistance(int a, int b)
    {
        int diff = Math.Abs(a - b) % 12;
        return Math.Min(diff, 12 - diff);
    }

    private static int OctantDistance(int a, int b)
    {
        int diff = Math.Abs(a - b) % 8;
        return Math.Min(diff, 8 - diff);
    }
}
