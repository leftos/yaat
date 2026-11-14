using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views.Scope;

/// <summary>Which alert overlays a block may carry, as the user's preferences set them.</summary>
/// <param name="ShowAtpa">ATPA in-trail distance display.</param>
/// <param name="ShowConflictAlerts">Conflict-alert (CA/MCI) display.</param>
/// <param name="ShowNoLandingClearance">No-landing-clearance warning display.</param>
public sealed record ScopeAlertOptions(bool ShowAtpa, bool ShowConflictAlerts, bool ShowNoLandingClearance);

/// <summary>Every per-frame input <see cref="TrackPresenter.Present"/> reads.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="TemplateId">Id of the template to present for (see <see cref="ScopeTemplates"/>).</param>
/// <param name="AtpaLead">The aircraft ahead in this track's ATPA pairing, or null when it is not on the scope.</param>
/// <param name="ConflictPeer">The other member of this track's conflict pair, or null when it is not on the scope.</param>
/// <param name="FlashOn">The 500 ms flash phase: true on the on-phase.</param>
/// <param name="IsSelected">Whether the track is selected.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow on the block.</param>
public sealed record TrackPresenterInput(
    AircraftModel Aircraft,
    string TemplateId,
    AircraftModel? AtpaLead,
    AircraftModel? ConflictPeer,
    bool FlashOn,
    bool IsSelected,
    ScopeAlertOptions Alerts
);

/// <summary>
/// Decides what one track's datablock says: its level, colour role and the text of every field its template
/// places. Pure: it reads only its input, so the flash phase and every peer aircraft arrive as arguments.
/// </summary>
public static class TrackPresenter
{
    /// <summary>Presents <paramref name="input"/>'s track for its template at the Full block level.</summary>
    /// <exception cref="ArgumentException">The template id is unknown.</exception>
    public static TrackPresentation Present(TrackPresenterInput input)
    {
        List<ScopeField> fields = input.TemplateId switch
        {
            ScopeTemplates.StarsId => StarsFields(input),
            ScopeTemplates.EuroScopeId => EuroScopeFields(input),
            _ => throw new ArgumentException($"Unknown scope template id '{input.TemplateId}'.", nameof(input)),
        };
        fields.AddRange(AlertFields(input));
        ScopeColorToken token = input.IsSelected ? ScopeColorToken.Selected : ScopeColorToken.Default;
        return new TrackPresentation(input.TemplateId, ScopeBlockLevel.Full, token, fields);
    }

    private static List<ScopeField> StarsFields(TrackPresenterInput input)
    {
        AircraftModel ac = input.Aircraft;
        bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        string cwt = string.IsNullOrEmpty(ac.CwtCode) ? "" : ac.CwtCode;
        return
        [
            Steady(ScopeFieldSlot.Callsign, ac.Callsign, ScopeColorToken.Default),
            Steady(ScopeFieldSlot.VfrMark, isVfr ? "*" : "", ScopeColorToken.Default),
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.GroundSpeed, ((int)ac.GroundSpeed / 10).ToString("D2"), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.CwtType, RadarDatablockLayout.FormatCwtType(cwt, ac.DisplayAircraftType), ScopeColorToken.Default),
            Flashing(ScopeFieldSlot.Ident, IdentText(ac), input.FlashOn, ScopeColorToken.Default),
            new(
                ScopeFieldSlot.StatusLine3,
                RadarDatablockLayout.BuildOwnerScratchpadLine(ac, showHandoff: input.FlashOn) ?? "",
                RadarDatablockLayout.BuildOwnerScratchpadLine(ac, showHandoff: true) ?? "",
                ScopeColorToken.Default,
                ScopeFieldAction.None
            ),
            AtpaField(input),
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
            Steady(ScopeFieldSlot.Altitude, AltitudeHundreds(ac), ScopeColorToken.Default),
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
        return
        [
            Steady(ScopeFieldSlot.Squawk, SquawkText(ac), ScopeColorToken.Default),
            Steady(ScopeFieldSlot.ModeC, ac.TransponderMode == "Standby" ? "ModeC" : "", ScopeColorToken.Default),
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

    private static ScopeField Steady(ScopeFieldSlot slot, string text, ScopeColorToken token) => new(slot, text, text, token, ScopeFieldAction.None);

    private static ScopeField Flashing(ScopeFieldSlot slot, string stableText, bool flashOn, ScopeColorToken token) =>
        new(slot, flashOn ? stableText : "", stableText, token, ScopeFieldAction.None);
}
