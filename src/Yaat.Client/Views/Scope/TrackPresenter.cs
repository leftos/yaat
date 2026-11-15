using System.Globalization;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views.Scope;

/// <summary>Which alert overlays a block may carry, as the user's preferences set them.</summary>
/// <param name="ShowAtpa">ATPA in-trail distance display.</param>
/// <param name="ShowConflictAlerts">Conflict-alert (CA/MCI) display.</param>
/// <param name="ShowNoLandingClearance">No-landing-clearance warning display.</param>
public sealed record ScopeAlertOptions(bool ShowAtpa, bool ShowConflictAlerts, bool ShowNoLandingClearance);

/// <summary>Which instructor display hints a block carries, as the user's preferences set them.</summary>
/// <param name="MarkStudentLevel">Mark a track the student's scope shows limited or partial with <c>(LDB)</c>/<c>(PDB)</c>.</param>
/// <param name="ShowTypeMismatchHints">Tint the STARS cwt/type token of an aircraft whose actual type differs from its filed type.</param>
/// <param name="ShowMvaAltitudeTint">Tint the altitude by its relation to the MVA floor at the aircraft's position.</param>
/// <param name="SyncStudentColors">Colour each block as the student's STARS scope colours it.</param>
/// <param name="UserInitials">The user's initials, compared with each track's assignee; null applies neither tint.</param>
/// <param name="AssignmentTint">The assignment tint is enabled and its colour parsed.</param>
/// <param name="UnassignedTint">The unassigned tint is enabled and its colour parsed.</param>
/// <param name="Viewer">The position the scope is viewed from (<see cref="ScopeViewer"/>).</param>
/// <param name="EramLine4Type">The ERAM line 4 falls back to the aircraft type and equipment suffix when no HSF is set.</param>
/// <param name="EramLine4Destination">The ERAM line 4 falls back to a destination of four characters or fewer after the type.</param>
public sealed record ScopeDisplayOptions(
    bool MarkStudentLevel,
    bool ShowTypeMismatchHints,
    bool ShowMvaAltitudeTint,
    bool SyncStudentColors,
    string? UserInitials,
    bool AssignmentTint,
    bool UnassignedTint,
    ScopeViewer Viewer,
    bool EramLine4Type,
    bool EramLine4Destination
);

/// <summary>Every per-frame input <see cref="TrackPresenter.Present"/> reads.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="TemplateId">Id of the template to present for (see <see cref="ScopeTemplates"/>).</param>
/// <param name="AtpaLead">The aircraft ahead in this track's ATPA pairing, or null when it is not on the scope.</param>
/// <param name="ConflictPeer">The other member of this track's conflict pair, or null when it is not on the scope.</param>
/// <param name="FlashOn">The 500 ms flash phase: true on the on-phase.</param>
/// <param name="IsSelected">Whether the track is selected; the renderer draws it, so it does not change the presentation.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow on the block.</param>
/// <param name="Display">Instructor display hints the user's preferences allow on the block.</param>
/// <param name="Mva">
/// The altitude's relation to the MVA floor at the track's position, or null when the tint does not apply; computed by
/// <see cref="ScopeFrame.Build"/>.
/// </param>
/// <param name="IsHighlighted">Whether the track's block is highlighted (middle-click).</param>
/// <param name="Level">The block level to present, chosen per track by <see cref="ScopeFrame.Build"/>.</param>
public sealed record TrackPresenterInput(
    AircraftModel Aircraft,
    string TemplateId,
    AircraftModel? AtpaLead,
    AircraftModel? ConflictPeer,
    bool FlashOn,
    bool IsSelected,
    ScopeAlertOptions Alerts,
    ScopeDisplayOptions Display,
    MvaRelation? Mva,
    bool IsHighlighted,
    ScopeBlockLevel Level = ScopeBlockLevel.Full
);

/// <summary>
/// What CRC's ERAM data block reads to format FDB line 2 (Field B, its character 4, and Field C), as the YAAT Scope has it.
/// Altitudes are in hundreds of feet unless named in feet.
/// </summary>
public sealed record EramAltitudeInput
{
    /// <summary>Interim altitude (<c>T</c>), or null.</summary>
    public int? InterimAltitude { get; init; }

    /// <summary>Local interim altitude (<c>L</c>), or null.</summary>
    public int? LocalInterimAltitude { get; init; }

    /// <summary>Procedure altitude (<c>P</c>), or null.</summary>
    public int? ProcedureAltitude { get; init; }

    /// <summary>Controller-entered reported altitude (CERA), which stands in for the Mode C altitude; or null.</summary>
    public int? ControllerEnteredAltitude { get; init; }

    /// <summary>The vertical-conformance latch: the track has reached its assigned altitude.</summary>
    public bool ReachedAssignedAltitude { get; init; }

    /// <summary>The flight-plan altitude an ERAM viewer is sent, in feet (a block's ceiling); null or 0 when none.</summary>
    public int? PlanAltitudeFeet { get; init; }

    /// <summary>The filed altitude every non-ERAM display is sent, in feet (a block's ceiling); 0 when none.</summary>
    public int FiledAltitudeFeet { get; init; }

    /// <summary>The block floor in feet, or null when the altitude is not a block.</summary>
    public int? BlockFloorFeet { get; init; }

    /// <summary>The altitude is <c>ABV</c>: it has no ceiling.</summary>
    public bool IsAbove { get; init; }

    /// <summary>The altitude is VFR.</summary>
    public bool IsVfr { get; init; }

    /// <summary>The altitude is VFR-on-top (<c>OTP</c>).</summary>
    public bool IsVfrOnTop { get; init; }

    /// <summary>The Mode C altitude, or null when the transponder reports none.</summary>
    public int? ReportedAltitude { get; init; }

    /// <summary>Whether Mode C was ever received (CRC's <c>WasModeCPreviouslyReceived</c>): <c>X</c>/<c>XXX</c> rather than <c>N</c>.</summary>
    public bool ModeCPreviouslyReceived { get; init; }

    /// <summary>
    /// The inputs as <paramref name="ac"/> carries them; Mode C is reported unless the transponder is on standby or the track
    /// coasts.
    /// </summary>
    public static EramAltitudeInput From(AircraftModel ac) =>
        new()
        {
            InterimAltitude = ac.EramInterimAltitude,
            LocalInterimAltitude = ac.EramLocalInterimAltitude,
            ProcedureAltitude = ac.EramProcedureAltitude,
            ControllerEnteredAltitude = ac.EramControllerEnteredAltitude,
            ReachedAssignedAltitude = ac.EramReachedAssignedAltitude,
            PlanAltitudeFeet = ac.EramPlanAltitudeFeet,
            FiledAltitudeFeet = ac.CruiseAltitude,
            BlockFloorFeet = ac.BlockFloorAltitude,
            IsAbove = ac.IsAbove,
            IsVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase),
            IsVfrOnTop = ac.IsVfrOnTop,
            ReportedAltitude = ReportsModeC(ac) ? (int)ac.Altitude / 100 : null,
            ModeCPreviouslyReceived = ac.TransponderHasReportedModeC,
        };

    // CRC GetReportedAltitude: a coasting track (QT coast or lost coverage) reports no Mode C, nor does a standby transponder.
    private static bool ReportsModeC(AircraftModel ac) =>
        !ac.EramIsCoastTrack && !ac.EramIsCoverageCoasting && !string.Equals(ac.TransponderMode, "Standby", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The ERAM FDB line-2 text and the arrow the renderer draws in its <see cref="TrackPresenter.EramArrowCell"/>.</summary>
/// <param name="Text">Field B, its character 4, then Field C.</param>
/// <param name="Style"><see cref="ScopeFieldStyle.ArrowUp"/> or <see cref="ScopeFieldStyle.ArrowDown"/> when climbing or descending.</param>
public readonly record struct EramAltitude(string Text, ScopeFieldStyle Style);

/// <summary>
/// Decides what one track's datablock says: its level, colour role and the text of every field its template
/// places. Pure: it reads only its input, so the flash phase and every peer aircraft arrive as arguments.
/// </summary>
public static class TrackPresenter
{
    /// <summary>
    /// The character the ERAM altitude text holds in Field B character 4 while climbing or descending; the renderer draws the
    /// arrow as a path in that monospace cell, so column alignment never depends on the font's glyph coverage.
    /// </summary>
    public const string EramArrowCell = " ";

    // CRC's ALTITUDE_CONFORMANCE_LIMIT, in hundreds of feet.
    private const int EramConformanceLimit = 2;

    private enum EramConformance
    {
        InConformance,
        Low,
        High,
        Climbing,
        Descending,
        DoesNotExist,
    }

    private readonly record struct EramReported(int Altitude, bool IsCera);

    private readonly record struct EramPlan(int? Altitude, int? BlockLow, bool IsSingle, bool IsBlock, bool IsAbove);

    /// <summary>
    /// ERAM FDB line 2, Field B + character 4 + Field C, as CRC's <c>BaseDataBlockRenderObject</c> formats it
    /// (<c>GetFieldB</c>, <c>GetFieldB4</c>, <c>GetVerticalConformance</c>, <c>GetFieldC</c>, <c>FormatAltitude</c>).
    /// </summary>
    public static EramAltitude EramAltitudeText(EramAltitudeInput input)
    {
        (EramReported? reported, EramPlan plan, EramConformance conformance) = JudgeAltitude(input);
        char b4 = FieldB4(input, reported, plan, conformance);
        ScopeFieldStyle style = b4 switch
        {
            '↑' => ScopeFieldStyle.ArrowUp,
            '↓' => ScopeFieldStyle.ArrowDown,
            _ => ScopeFieldStyle.None,
        };
        string cell = style == ScopeFieldStyle.None ? b4.ToString() : EramArrowCell;
        return new EramAltitude(FieldB(input, plan, conformance) + cell + FieldC(input, reported, plan, conformance), style);
    }

    /// <summary>
    /// ERAM enhanced paired LDB line 2: Field B then Field C, with no character 4 between them (CRC
    /// <c>PairedLdbRenderObject.FormatEnhancedPairedLdb</c>). With no character 4 there is no arrow cell to draw.
    /// </summary>
    public static string EramFieldsBAndC(EramAltitudeInput input)
    {
        (EramReported? reported, EramPlan plan, EramConformance conformance) = JudgeAltitude(input);
        return FieldB(input, plan, conformance) + FieldC(input, reported, plan, conformance);
    }

    /// <summary>
    /// Whether the ERAM plan altitude and the filed (STARS) altitude differ: both set (above 0) and unequal, on a plan that is
    /// neither VFR nor OTP (Field B shows no number there). Block and ABV altitudes compare their ceilings.
    /// </summary>
    public static bool EramAltitudeDiffers(EramAltitudeInput input) =>
        (input.PlanAltitudeFeet is int eram and > 0)
        && (input.FiledAltitudeFeet > 0)
        && (eram != input.FiledAltitudeFeet)
        && !input.IsVfr
        && !input.IsVfrOnTop;

    private static (EramReported? Reported, EramPlan Plan, EramConformance Conformance) JudgeAltitude(EramAltitudeInput input)
    {
        EramReported? reported = ReportedOf(input);
        EramPlan plan = PlanOf(input);
        return (reported, plan, ConformanceOf(reported, input.ReachedAssignedAltitude, plan));
    }

    // CRC GetReportedAltitude: a controller-entered altitude wins over Mode C.
    private static EramReported? ReportedOf(EramAltitudeInput input)
    {
        if (input.ControllerEnteredAltitude is int cera)
        {
            return new EramReported(cera, true);
        }
        return input.ReportedAltitude is int modeC ? new EramReported(modeC, false) : null;
    }

    // CRC ParsedAltitude as the server builds it: IsSingle and IsBlock exclude VFR, OTP and ABV.
    private static EramPlan PlanOf(EramAltitudeInput input)
    {
        int? altitude = input.PlanAltitudeFeet is int feet and > 0 ? feet / 100 : null;
        int? low = input.BlockFloorFeet / 100;
        bool plain = altitude.HasValue && !input.IsVfr && !input.IsVfrOnTop && !input.IsAbove;
        return new EramPlan(altitude, low, plain && !low.HasValue, plain && low.HasValue, input.IsAbove);
    }

    // CRC GetVerticalConformance: a ±2 tolerance unless the altitude is controller-entered; ABV has no ceiling.
    private static EramConformance ConformanceOf(EramReported? reported, bool reached, EramPlan plan)
    {
        if (reported is not { } rep)
        {
            return EramConformance.DoesNotExist;
        }
        if (plan.Altitude is not int assigned)
        {
            return EramConformance.InConformance;
        }

        (int floor, int? ceiling) = ConformanceBand(plan, assigned, rep.IsCera);
        if (rep.Altitude < floor)
        {
            return reached ? EramConformance.Low : EramConformance.Climbing;
        }
        if ((ceiling is int top) && (rep.Altitude > top))
        {
            return reached ? EramConformance.High : EramConformance.Descending;
        }
        return EramConformance.InConformance;
    }

    // The conformance floor (the block floor or the assigned altitude) and ceiling (none for ABV), widened by the tolerance.
    private static (int Floor, int? Ceiling) ConformanceBand(EramPlan plan, int assigned, bool isCera)
    {
        int tolerance = isCera ? 0 : EramConformanceLimit;
        int floor = (plan.BlockLow ?? assigned) - tolerance;
        return (floor, plan.IsAbove ? null : (int?)(assigned + tolerance));
    }

    // The temporary altitude Field B shows ahead of the plan's, with its character 4: local interim, procedure, then interim.
    private static (int Altitude, char Mark)? Temporary(EramAltitudeInput input)
    {
        if (input.LocalInterimAltitude is int local)
        {
            return (local, 'L');
        }
        if (input.ProcedureAltitude is int procedure)
        {
            return (procedure, 'P');
        }
        return input.InterimAltitude is int interim ? (interim, 'T') : null;
    }

    // CRC GetFieldB.
    private static string FieldB(EramAltitudeInput input, EramPlan plan, EramConformance conformance)
    {
        if (Temporary(input) is { } temporary)
        {
            return temporary.Altitude.ToString(CultureInfo.InvariantCulture);
        }
        if ((plan.Altitude is int alt) && (plan.IsSingle || plan.IsAbove || plan.IsBlock))
        {
            return PlanFieldB(plan, alt, conformance).ToString(CultureInfo.InvariantCulture);
        }
        if (input.IsVfrOnTop)
        {
            return "OTP";
        }
        return input.IsVfr ? "VFR" : "";
    }

    // A block shows its floor unless the track is below it (Low or Climbing), when it shows the ceiling.
    private static int PlanFieldB(EramPlan plan, int altitude, EramConformance conformance) =>
        (plan.IsBlock && (plan.BlockLow is int low) && (conformance is not (EramConformance.Low or EramConformance.Climbing))) ? low : altitude;

    // CRC GetFieldB4 (no reported altitude is ever Established in the sim, so that N arm has no input).
    private static char FieldB4(EramAltitudeInput input, EramReported? reported, EramPlan plan, EramConformance conformance)
    {
        if (Temporary(input) is { } temporary)
        {
            return temporary.Mark;
        }
        if (reported is not { } rep)
        {
            return input.ModeCPreviouslyReceived ? 'X' : 'N';
        }
        if (input.IsVfr || input.IsVfrOnTop)
        {
            return '/';
        }
        return plan.Altitude.HasValue ? ConformanceMark(conformance, plan, rep) : 'V';
    }

    private static char ConformanceMark(EramConformance conformance, EramPlan plan, EramReported rep) =>
        conformance switch
        {
            EramConformance.Low => '-',
            EramConformance.High => '+',
            EramConformance.Climbing => '↑',
            EramConformance.Descending => '↓',
            _ when plan.BlockLow.HasValue => 'B',
            _ when rep.IsCera => 'A',
            _ => 'C',
        };

    // CRC GetFieldC. The server always sends a RawValue, so only VFR and OTP take the "no plan altitude" arm.
    private static string FieldC(EramAltitudeInput input, EramReported? reported, EramPlan plan, EramConformance conformance)
    {
        bool hasTemporary = Temporary(input).HasValue;
        if (reported is not { } rep)
        {
            return MissingReportFieldC(input, plan, hasTemporary);
        }
        if (hasTemporary || input.IsVfr || input.IsVfrOnTop)
        {
            return FormatEramAltitude(rep.Altitude, hasTemporary && rep.IsCera);
        }
        return PlanFieldC(rep, plan, conformance);
    }

    // Out of conformance the reported altitude shows; in conformance only a block's ceiling does.
    private static string PlanFieldC(EramReported rep, EramPlan plan, EramConformance conformance)
    {
        if (conformance != EramConformance.InConformance)
        {
            return FormatEramAltitude(rep.Altitude, rep.IsCera);
        }
        return BlockCeiling(plan) is int ceiling ? FormatEramAltitude(ceiling, rep.IsCera) : "";
    }

    private static string MissingReportFieldC(EramAltitudeInput input, EramPlan plan, bool hasTemporary)
    {
        if (input.ModeCPreviouslyReceived)
        {
            return "XXX";
        }
        return (!hasTemporary && (BlockCeiling(plan) is int ceiling)) ? FormatEramAltitude(ceiling, false) : "";
    }

    private static int? BlockCeiling(EramPlan plan) => plan.IsBlock ? plan.Altitude : null;

    // CRC FormatAltitude with pad = false: never zero-padded; a CERA takes '#'.
    private static string FormatEramAltitude(int altitude, bool isCera) => altitude.ToString(CultureInfo.InvariantCulture) + (isCera ? "#" : "");

    /// <summary>
    /// Presents <paramref name="input"/>'s track for its template at its block level. Dwell presents the Full block's fields;
    /// EuroScope presents its full tag at every level.
    /// </summary>
    /// <exception cref="ArgumentException">The template id is unknown.</exception>
    public static TrackPresentation Present(TrackPresenterInput input)
    {
        List<ScopeField> fields = FieldsAt(input);
        ScopeTintRole tint = TintFor(input);
        // CRC's ERAM LDBs draw no leader.
        bool leader = !((input.TemplateId == ScopeTemplates.EramId) && (input.Level is ScopeBlockLevel.Limited or ScopeBlockLevel.Partial));
        return new TrackPresentation(input.TemplateId, input.Level, BlockColorFor(input, tint), tint, fields, leader);
    }

    private static List<ScopeField> FieldsAt(TrackPresenterInput input) =>
        (input.TemplateId, input.Level) switch
        {
            (ScopeTemplates.StarsId, ScopeBlockLevel.Limited) => StarsLimitedFields(input),
            (ScopeTemplates.StarsId, ScopeBlockLevel.Partial) => StarsPartialFields(input),
            (ScopeTemplates.EramId, ScopeBlockLevel.Limited or ScopeBlockLevel.Partial) => EramLdbFields(input),
            _ => FullFields(input),
        };

    private static List<ScopeField> FullFields(TrackPresenterInput input)
    {
        List<ScopeField> fields = input.TemplateId switch
        {
            ScopeTemplates.StarsId => StarsFields(input),
            ScopeTemplates.EramId => EramFields(input),
            ScopeTemplates.EuroScopeId => EuroScopeFields(input),
            _ => throw new ArgumentException($"Unknown scope template id '{input.TemplateId}'.", nameof(input)),
        };
        fields.AddRange(AlertFields(input));
        fields.AddRange(OtherAltitudeFields(input));
        if (input.TemplateId == ScopeTemplates.EramId)
        {
            // The ERAM status text carries the beacon mismatch.
            fields.RemoveAll(f => f.Slot == ScopeFieldSlot.Squawk);
        }
        return fields;
    }

    /// <summary>
    /// The Radar's collapsed LDB (<see cref="RadarDatablockLayout.BuildCollapsedLines"/>): the beacon code in the squawk slot
    /// (empty when none), the altitude and the ident.
    /// </summary>
    private static List<ScopeField> StarsLimitedFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        string beacon = ac.BeaconCode > 0 ? ac.BeaconCode.ToString("0000", CultureInfo.InvariantCulture) : "";
        return
        [
            Steady(ScopeFieldSlot.Squawk, beacon, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), MvaToken(input.Mva)),
            Flashing(ScopeFieldSlot.Ident, IdentText(ac), input.FlashOn, ScopeColorToken.Default),
        ];
    }

    /// <summary>
    /// The Radar's collapsed PDB (<see cref="RadarDatablockLayout.BuildCollapsedLines"/>): the altitude, the handoff peer, the
    /// ground speed and the ident, then scratchpad 1.
    /// </summary>
    private static List<ScopeField> StarsPartialFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        return
        [
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), MvaToken(input.Mva)),
            Steady(ScopeFieldSlot.Handoff, ac.HandoffDisplay, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.GroundSpeed, GroundSpeedTens(ac), ScopeColorToken.Default),
            Flashing(ScopeFieldSlot.Ident, IdentText(ac), input.FlashOn, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Scratchpad1, RadarDatablockLayout.EffectiveScratchpad1(ac) ?? "", ScopeColorToken.Default),
        ];
    }

    /// <summary>
    /// CRC's paired LDB at Limited and enhanced paired LDB at Partial (<c>PairedLdbRenderObject</c>): the aircraft id; the
    /// padded CERA or Mode C altitude, or Field B and Field C (<see cref="EramFieldsBAndC"/>); then the special-purpose-code
    /// label in the status slot. The whole block blinks while a special code shows (CRC <c>BaseLdbRenderObject</c>): every
    /// text blanks on the off phase.
    /// </summary>
    private static List<ScopeField> EramLdbFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        string spc = EramStatusLine.SpcText(ac) ?? "";
        var altitude = EramAltitudeInput.From(ac);
        ScopeField line2 =
            input.Level == ScopeBlockLevel.Limited
                ? Steady(ScopeFieldSlot.Altitude, LdbAltitude(altitude), ScopeColorToken.Default)
                : Steady(ScopeFieldSlot.EramAltitude, EramFieldsBAndC(altitude), ScopeColorToken.Default);
        List<ScopeField> fields = [Steady(ScopeFieldSlot.Callsign, ac.Callsign, ScopeColorToken.Default), line2];
        if (input.Level == ScopeBlockLevel.Partial)
        {
            fields.Add(MismatchField(altitude));
        }
        fields.Add(Steady(ScopeFieldSlot.StatusLine3, spc, ScopeColorToken.Default));
        bool blankPhase = (spc.Length > 0) && !input.FlashOn;
        return blankPhase ? fields.ConvertAll(f => f with { Text = "" }) : fields;
    }

    // CRC FormatAltitude with pad = true: three digits (two below zero), a CERA marked '#'; empty with neither altitude.
    private static string LdbAltitude(EramAltitudeInput input)
    {
        if (input.ControllerEnteredAltitude is int cera)
        {
            return PaddedAltitude(cera) + "#";
        }
        return input.ReportedAltitude is int modeC ? PaddedAltitude(modeC) : "";
    }

    private static string PaddedAltitude(int altitude) => altitude.ToString(altitude < 0 ? "D2" : "D3", CultureInfo.InvariantCulture);

    // The ≠ cell after the ERAM altitude while the two sides differ; empty (no rect, no width) otherwise.
    private static ScopeField MismatchField(EramAltitudeInput altitude)
    {
        bool differs = EramAltitudeDiffers(altitude);
        return Styled(
            ScopeFieldSlot.AltitudeMismatch,
            differs ? EramArrowCell : "",
            differs ? ScopeFieldStyle.AltitudeMismatch : ScopeFieldStyle.None
        );
    }

    /// <summary>
    /// The Dwell block's listing of the other side's altitude under line 2 while the two differ, in unpadded hundreds:
    /// <c>STARS {filed}</c> on the ERAM template, <c>ERAM {plan}</c> on the STARS template; nothing at any other level or template.
    /// </summary>
    private static IEnumerable<ScopeField> OtherAltitudeFields(TrackPresenterInput input)
    {
        if ((input.Level != ScopeBlockLevel.Dwell) || (input.TemplateId is not (ScopeTemplates.EramId or ScopeTemplates.StarsId)))
        {
            return [];
        }

        var altitude = EramAltitudeInput.From(input.Aircraft);
        if (!EramAltitudeDiffers(altitude) || (altitude.PlanAltitudeFeet is not int plan))
        {
            return [];
        }

        string text = input.TemplateId == ScopeTemplates.EramId ? "STARS " + Hundreds(altitude.FiledAltitudeFeet) : "ERAM " + Hundreds(plan);
        return [Steady(ScopeFieldSlot.OtherAltitude, text, ScopeColorToken.Default)];
    }

    private static string Hundreds(int feet) => (feet / 100).ToString(CultureInfo.InvariantCulture);

    private static ScopeField EramAltitudeField(EramAltitudeInput altitude)
    {
        EramAltitude eram = EramAltitudeText(altitude);
        return new ScopeField(ScopeFieldSlot.EramAltitude, eram.Text, eram.Text, ScopeColorToken.Default, ScopeFieldAction.None, eram.Style);
    }

    /// <summary>
    /// The block's colour role with today's radar precedence: the middle-click highlight, then the RPO assignment tint,
    /// then the student's STARS colour when sync is on, then the default. Selection does not change it.
    /// </summary>
    private static ScopeBlockColor BlockColorFor(TrackPresenterInput input, ScopeTintRole tint)
    {
        if (input.IsHighlighted)
        {
            return ScopeBlockColor.Highlighted;
        }

        return tint switch
        {
            ScopeTintRole.AssignedToMe => ScopeBlockColor.AssignedToMe,
            ScopeTintRole.AssignedToOther => ScopeBlockColor.AssignedToOther,
            _ => input.Display.SyncStudentColors ? StudentBlockColor(input.Aircraft.StudentDatablockColor) : ScopeBlockColor.Default,
        };
    }

    /// <summary>
    /// The RPO tint that applies to the track whatever its highlight, as today's radar judges it: with the user's initials
    /// set, an aircraft assigned to them takes the assignment tint and any other the unassigned tint, each when enabled.
    /// </summary>
    private static ScopeTintRole TintFor(TrackPresenterInput input)
    {
        ScopeDisplayOptions display = input.Display;
        if (display.UserInitials is null)
        {
            return ScopeTintRole.None;
        }

        bool isMe = string.Equals(input.Aircraft.AssignedTo, display.UserInitials, StringComparison.OrdinalIgnoreCase);
        if (isMe && display.AssignmentTint)
        {
            return ScopeTintRole.AssignedToMe;
        }
        return (!isMe && display.UnassignedTint) ? ScopeTintRole.AssignedToOther : ScopeTintRole.None;
    }

    private static ScopeBlockColor StudentBlockColor(StarsDatablockColor? student) =>
        student switch
        {
            StarsDatablockColor.Owned => ScopeBlockColor.StudentOwned,
            StarsDatablockColor.Unowned => ScopeBlockColor.StudentUnowned,
            StarsDatablockColor.Pointout => ScopeBlockColor.StudentPointout,
            StarsDatablockColor.Highlighted => ScopeBlockColor.StudentHighlighted,
            _ => ScopeBlockColor.Default,
        };

    private static List<ScopeField> StarsFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        string cwt = string.IsNullOrEmpty(ac.CwtCode) ? "" : ac.CwtCode;
        string levelMark = input.Display.MarkStudentLevel ? RadarDatablockLayout.StudentLevelMarker(ac.StudentDatablockLevel) : "";
        bool typeMismatch = input.Display.ShowTypeMismatchHints && ac.HasFiledTypeMismatch;
        ScopeColorToken typeToken = typeMismatch ? ScopeColorToken.TypeMismatch : ScopeColorToken.Default;
        return
        [
            Steady(ScopeFieldSlot.Callsign, ac.Callsign, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.VfrMark, isVfr ? "*" : "", ScopeColorToken.Default),
            Steady(ScopeFieldSlot.StudentLevelMark, levelMark, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), MvaToken(input.Mva)),
            Steady(ScopeFieldSlot.GroundSpeed, GroundSpeedTens(ac), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.CwtType, RadarDatablockLayout.FormatCwtType(cwt, ac.DisplayAircraftType), typeToken),
            Flashing(ScopeFieldSlot.Ident, IdentText(ac), input.FlashOn, ScopeColorToken.Default),
            new(
                ScopeFieldSlot.StatusLine3,
                RadarDatablockLayout.BuildOwnerScratchpadLine(ac, showHandoff: input.FlashOn) ?? "",
                RadarDatablockLayout.BuildOwnerScratchpadLine(ac, showHandoff: true) ?? "",
                ScopeColorToken.Default,
                ScopeFieldAction.None,
                ScopeFieldStyle.None
            ),
            AtpaField(input),
        ];
    }

    /// <summary>
    /// The ERAM template's fields: the STARS fields with the ERAM altitude line in place of the STARS altitude, and the ERAM
    /// status, HSF indicator and line 4 in place of the STARS status. The MVA tint does not apply: it colours a whole field,
    /// and only Field C is the reported altitude.
    /// </summary>
    private static List<ScopeField> EramFields(TrackPresenterInput input)
    {
        List<ScopeField> fields = StarsFields(input);
        int altitude = fields.FindIndex(f => f.Slot == ScopeFieldSlot.Altitude);
        var eramAltitude = EramAltitudeInput.From(input.Aircraft);
        fields[altitude] = EramAltitudeField(eramAltitude);
        fields.Insert(altitude + 1, MismatchField(eramAltitude));
        fields.RemoveAll(f => f.Slot == ScopeFieldSlot.StatusLine3);
        fields.AddRange(EramLineFields(input));
        fields.AddRange(EramColumn0Fields(input));
        return fields;
    }

    /// <summary>
    /// ERAM column 0: the VCI mark (a placeholder cell the renderer draws in) while the viewer's sector is on frequency, and
    /// <c>R</c> while the track is not the viewer's; each empty otherwise.
    /// </summary>
    private static IEnumerable<ScopeField> EramColumn0Fields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        ScopeViewer viewer = input.Display.Viewer;
        bool vci = EramColumn0.VciLit(ac, viewer);
        return
        [
            Styled(ScopeFieldSlot.Vci, vci ? EramArrowCell : "", vci ? ScopeFieldStyle.Vci : ScopeFieldStyle.None),
            Steady(ScopeFieldSlot.NotYourControl, EramColumn0.ShowsNotYourControl(ac, viewer) ? "R" : "", ScopeColorToken.Default),
        ];
    }

    /// <summary>
    /// ERAM line 3 (the status text, padded to four columns when the HSF indicator follows it) and line 4. A blinking status
    /// is spaces of its length on the off phase, unless the track is in conflict (CRC <c>GetFieldE</c>).
    /// </summary>
    private static IEnumerable<ScopeField> EramLineFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        (string status, bool blinks) = EramStatusLine.Build(ac, input.Display.Viewer);
        bool hsf = EramLine4.HasHsf(ac);
        string stable = hsf ? status.PadRight(4) : status;
        bool blank = blinks && !input.FlashOn && !ConflictActive(input);
        string text = blank ? new string(' ', stable.Length) : stable;
        string line4 = EramLine4.Text(ac, input.Display.EramLine4Type, input.Display.EramLine4Destination);
        return
        [
            new(ScopeFieldSlot.StatusLine3, text, stable, ScopeColorToken.Default, ScopeFieldAction.None, ScopeFieldStyle.None),
            Styled(ScopeFieldSlot.HsfIndicator, hsf ? EramArrowCell : "", hsf ? ScopeFieldStyle.HsfIndicator : ScopeFieldStyle.None),
            Steady(ScopeFieldSlot.Line4Notes, line4, ScopeColorToken.Default),
        ];
    }

    private static List<ScopeField> EuroScopeFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        string? sp1 = RadarDatablockLayout.EffectiveScratchpad1(ac);
        string handoff = string.IsNullOrEmpty(ac.HandoffDisplay) ? "" : $">{ac.HandoffDisplay}";
        return
        [
            Steady(ScopeFieldSlot.Owner, string.IsNullOrEmpty(ac.AssignedTo) ? "--" : ac.AssignedTo, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Callsign, ac.AutoDeletePending ? $"{ac.Callsign}*" : ac.Callsign, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.TypeCwt, EuroScopeTypeCwt(ac), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Destination, ac.Destination ?? "", ScopeColorToken.Default),
            Flashing(ScopeFieldSlot.Ident, IdentText(ac), input.FlashOn, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), MvaToken(input.Mva)),
            Steady(ScopeFieldSlot.AssignedAltitude, ac.AssignedAltitude is double alt ? $"({(int)alt / 100:D3})" : "(---)", ScopeColorToken.Default),
            Steady(ScopeFieldSlot.AssignedSpeed, EuroScopeTagLayout.FormatAssignedSpeed(ac), ScopeColorToken.Default),
            Steady(
                ScopeFieldSlot.AssignedHeading,
                ac.AssignedHeading is MagneticHeading hdg ? $"H{hdg.ToDisplayString()}" : "AHDG",
                ScopeColorToken.Default
            ),
            Steady(ScopeFieldSlot.AssignedRunway, RunwayText(ac), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Scratchpad1, string.IsNullOrEmpty(sp1) ? "" : $".{sp1}", ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Scratchpad2, string.IsNullOrEmpty(ac.Scratchpad2) ? "" : $"+{ac.Scratchpad2}", ScopeColorToken.Default),
            Flashing(ScopeFieldSlot.Handoff, handoff, input.FlashOn, ScopeColorToken.Default),
        ];
    }

    private static IEnumerable<ScopeField> AlertFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        string noLandingClearance = NoLandingClearanceActive(input) ? RadarDatablockLayout.NoLandingClearanceText : "";
        string conflict = ConflictActive(input) ? RadarDatablockLayout.BuildConflictLine(ac, input.ConflictPeer) : "";
        string squawk = SquawkText(ac);
        // The mismatch line keeps its whole text in both phases; only its assigned-code tail dims on the off-phase.
        ScopeFieldStyle squawkStyle = ((squawk.Length > 0) && !input.FlashOn) ? ScopeFieldStyle.DimTail : ScopeFieldStyle.None;
        bool standby = ac.TransponderMode == "Standby";
        return
        [
            Styled(ScopeFieldSlot.Squawk, squawk, squawkStyle),
            Styled(ScopeFieldSlot.ModeC, standby ? "ModeC" : "", standby ? ScopeFieldStyle.Strikethrough : ScopeFieldStyle.None),
            Flashing(ScopeFieldSlot.NoLandingClearance, noLandingClearance, input.FlashOn, ScopeColorToken.NoLandingClearance),
            Flashing(ScopeFieldSlot.ConflictAlert, conflict, input.FlashOn, ScopeColorToken.ConflictAlert),
            Steady(ScopeFieldSlot.InstructorNote, ac.HasNote ? ac.Note : "", ScopeColorToken.Note),
        ];
    }

    private static ScopeField AtpaField(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        bool paired = input.Alerts.ShowAtpa && !string.IsNullOrEmpty(ac.AtpaLeadCallsign);
        string text = paired ? RadarDatablockLayout.BuildAtpaLine(ac, input.AtpaLead) : "";
        ScopeColorToken token = ac.AtpaConeState switch
        {
            AtpaConeState.Warning => ScopeColorToken.AtpaWarning,
            AtpaConeState.Alert => ScopeColorToken.AtpaAlert,
            _ => ScopeColorToken.Default,
        };
        return Steady(ScopeFieldSlot.Atpa, text, token);
    }

    private static bool NoLandingClearanceActive(TrackPresenterInput input) =>
        input.Alerts.ShowNoLandingClearance && input.Aircraft.NoLandingClearanceWarningActive && !input.Aircraft.IsAutoClearedToLand;

    private static bool ConflictActive(TrackPresenterInput input) =>
        input.Alerts.ShowConflictAlerts && !string.IsNullOrEmpty(input.Aircraft.ConflictPeerCallsign);

    private static string SquawkText(AircraftModel ac) =>
        AircraftCommandApplicability.TryGetSquawkMismatch(ac, out string reported, out string assigned) ? $"{reported} {assigned}" : "";

    private static string AltitudeHundreds(AircraftModel ac) => ((int)ac.Altitude / 100).ToString("D3");

    private static string GroundSpeedTens(AircraftModel ac) => ((int)ac.GroundSpeed / 10).ToString("D2", CultureInfo.InvariantCulture);

    private static ScopeColorToken MvaToken(MvaRelation? mva) =>
        mva switch
        {
            MvaRelation.Below => ScopeColorToken.MvaBelow,
            MvaRelation.At => ScopeColorToken.MvaAt,
            _ => ScopeColorToken.Default,
        };

    private static string IdentText(AircraftModel ac) => ac.IsIdenting ? RadarDatablockLayout.IdentText : "";

    private static string RunwayText(AircraftModel ac) =>
        string.IsNullOrEmpty(ac.AssignedRunway) ? "" : $"R{RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}";

    private static string EuroScopeTypeCwt(AircraftModel ac)
    {
        string type = ac.DisplayAircraftType.Trim();
        string cwt = ac.CwtCode.Trim();
        if ((type.Length > 0) && (cwt.Length > 0))
        {
            return $"{type}/{cwt}";
        }
        return type.Length > 0 ? type : cwt;
    }

    private static ScopeField Steady(ScopeFieldSlot slot, string text, ScopeColorToken token) =>
        new(slot, text, text, token, ScopeFieldAction.None, ScopeFieldStyle.None);

    private static ScopeField Styled(ScopeFieldSlot slot, string text, ScopeFieldStyle style) =>
        new(slot, text, text, ScopeColorToken.Default, ScopeFieldAction.None, style);

    private static ScopeField Flashing(ScopeFieldSlot slot, string stableText, bool flashOn, ScopeColorToken token) =>
        new(slot, flashOn ? stableText : "", stableText, token, ScopeFieldAction.None, ScopeFieldStyle.None);
}
