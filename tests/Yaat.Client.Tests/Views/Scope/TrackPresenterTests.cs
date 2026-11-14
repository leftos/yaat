using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>Pins the pure <see cref="TrackPresenter"/>: field texts, tokens and the flash/reservation contract.</summary>
[Collection(RadarBlinkClockCollection.Name)]
public class TrackPresenterTests
{
    private static ScopeField Field(TrackPresentation p, ScopeFieldSlot slot) => Assert.Single(p.Fields, f => f.Slot == slot);

    [Fact]
    public void VfrMark_PresentForVfr_EmptyForIfr()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        Assert.Equal("", Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId)), ScopeFieldSlot.VfrMark).Text);

        ac.FlightRules = "VFR";
        TrackPresentation vfr = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal("*", Field(vfr, ScopeFieldSlot.VfrMark).Text);
        Assert.Equal("UAL238", Field(vfr, ScopeFieldSlot.Callsign).Text);
    }

    [Fact]
    public void Line2Slots_CarryAltitudeGroundSpeedAndCwtType()
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), ScopeTemplates.StarsId));

        Assert.Equal("230", Field(p, ScopeFieldSlot.Altitude).Text);
        Assert.Equal("25", Field(p, ScopeFieldSlot.GroundSpeed).Text);
        Assert.Equal("D/B738", Field(p, ScopeFieldSlot.CwtType).Text);
    }

    [Fact]
    public void CwtType_FallsBackToTypeAlone_WhenNoCwt()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.CwtCode = "";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal("B738", Field(p, ScopeFieldSlot.CwtType).Text);
    }

    [Fact]
    public void Atpa_ShowsInTrailDistanceFromTheLeadModel_TintedByConeState()
    {
        AircraftModel ac = ScopeTestAircraft.CreateAtpaTrailer();

        TrackPresentation p = TrackPresenter.Present(
            ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, atpaLead: ScopeTestAircraft.CreateAtpaLead())
        );

        ScopeField atpa = Field(p, ScopeFieldSlot.Atpa);
        Assert.Equal("3.2", atpa.Text);
        Assert.Equal(ScopeColorToken.AtpaWarning, atpa.Token);
    }

    [Fact]
    public void Atpa_Empty_WhenLeadModelAbsent()
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateAtpaTrailer(), ScopeTemplates.StarsId));

        Assert.Equal("", Field(p, ScopeFieldSlot.Atpa).StableText);
    }

    [Fact]
    public void FlashPhase_TogglesOnlyTheFlashingSlotsText_NotItsReservation()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsIdenting = true;

        TrackPresentation on = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, flashOn: true));
        TrackPresentation off = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, flashOn: false));

        Assert.Equal("ID", Field(on, ScopeFieldSlot.Ident).Text);
        Assert.Equal("", Field(off, ScopeFieldSlot.Ident).Text);
        Assert.Equal("ID", Field(off, ScopeFieldSlot.Ident).StableText);
        Assert.Equal(on.Fields.Where(f => f.Slot != ScopeFieldSlot.Ident), off.Fields.Where(f => f.Slot != ScopeFieldSlot.Ident));
    }

    [Fact]
    public void Presenter_ReadsNothingButItsInputs()
    {
        // Every flashing field is active and the blink clock flips phase on every read, so a hidden read of
        // the blink would make the two calls differ while the passed phase stays fixed.
        AircraftModel ac = ScopeTestAircraft.CreateEverythingFlashing();
        TrackPresenterInput input = ScopeTestAircraft.Input(
            ac,
            ScopeTemplates.StarsId,
            flashOn: false,
            conflictPeer: ScopeTestAircraft.CreateConflictPeer()
        );

        long reads = 0;
        TrackPresentation first;
        TrackPresentation second;
        using (RadarBlinkPin.To(() => reads++ * RadarBlinkPin.OffPhaseMs))
        {
            first = TrackPresenter.Present(input);
            second = TrackPresenter.Present(input);
        }

        Assert.Equal(first.Fields, second.Fields);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public void Presentation_IsFullLevel_WithNoActions_AndSelectionDrivesTheBlockToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();

        TrackPresentation idle = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId));
        TrackPresentation selected = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId) with { IsSelected = true });

        Assert.Equal(ScopeBlockLevel.Full, idle.Level);
        Assert.Equal(ScopeColorToken.Default, idle.Token);
        Assert.Equal(ScopeColorToken.Selected, selected.Token);
        Assert.All(idle.Fields, f => Assert.Equal(ScopeFieldAction.None, f.Action));
    }

    [Fact]
    public void CwtType_FiledTypeMismatch_CarriesTheTypeMismatchToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";
        ac.AircraftType = "B738";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal(ScopeColorToken.TypeMismatch, Field(p, ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void CwtType_SuffixOnlyDifference_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B738/L";
        ac.AircraftType = "B738";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal(ScopeColorToken.Default, Field(p, ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void CwtType_HintOff_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId) with
        {
            Display = ScopeTestAircraft.AllDisplay with { ShowTypeMismatchHints = false },
        };

        Assert.Equal(ScopeColorToken.Default, Field(TrackPresenter.Present(input), ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void EuroScopeTypeCwt_FiledTypeMismatch_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId));

        Assert.Equal(ScopeColorToken.Default, Field(p, ScopeFieldSlot.TypeCwt).Token);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Squawk_Mismatch_DimsTheTailOnlyOffPhase_WithTheSameText(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateSquawkMismatch();

        ScopeField on = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: true)), ScopeFieldSlot.Squawk);
        ScopeField off = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: false)), ScopeFieldSlot.Squawk);

        Assert.Equal("1200 0301", on.Text);
        Assert.Equal(on.Text, off.Text);
        Assert.Equal(on.StableText, off.StableText);
        Assert.Equal(ScopeFieldStyle.None, on.Style);
        Assert.Equal(ScopeFieldStyle.DimTail, off.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Squawk_NoMismatch_HasNoStyle(string templateId)
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), templateId, flashOn: false));

        Assert.Equal(ScopeFieldStyle.None, Field(p, ScopeFieldSlot.Squawk).Style);
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId, true)]
    [InlineData(ScopeTemplates.StarsId, false)]
    [InlineData(ScopeTemplates.EuroScopeId, true)]
    [InlineData(ScopeTemplates.EuroScopeId, false)]
    public void ModeC_Standby_IsStruckThroughInBothPhases(string templateId, bool flashOn)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "Standby";

        ScopeField modeC = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn)), ScopeFieldSlot.ModeC);

        Assert.Equal("ModeC", modeC.Text);
        Assert.Equal(ScopeFieldStyle.Strikethrough, modeC.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void ModeC_TransponderOn_HasNoStyle(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";

        ScopeField modeC = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId)), ScopeFieldSlot.ModeC);

        Assert.Equal(ScopeFieldStyle.None, modeC.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaBelow_CarriesTheMvaBelowToken(string templateId) =>
        Assert.Equal(ScopeColorToken.MvaBelow, AltitudeTokenFor(templateId, MvaRelation.Below));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaAt_CarriesTheMvaAtToken(string templateId) =>
        Assert.Equal(ScopeColorToken.MvaAt, AltitudeTokenFor(templateId, MvaRelation.At));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaAbove_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, MvaRelation.Above));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaNoData_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, MvaRelation.NoData));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_NoMvaRelation_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, null));

    [Fact]
    public void EuroScopeAssignedAltitude_MvaBelow_KeepsTheDefaultToken()
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), ScopeTemplates.EuroScopeId) with
        {
            Mva = MvaRelation.Below,
        };

        Assert.Equal(ScopeColorToken.Default, Field(TrackPresenter.Present(input), ScopeFieldSlot.AssignedAltitude).Token);
    }

    private static ScopeColorToken AltitudeTokenFor(string templateId, MvaRelation? mva)
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), templateId) with { Mva = mva };
        return Field(TrackPresenter.Present(input), ScopeFieldSlot.Altitude).Token;
    }

    public static TheoryData<string> TemplateIds => [ScopeTemplates.StarsId, ScopeTemplates.EuroScopeId];

    [Fact]
    public void UnknownTemplateId_Throws() =>
        Assert.Throws<ArgumentException>(() => TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), "nope")));
}

/// <summary>Shared aircraft setups and presenter inputs for the YAAT Scope presenter, engine and parity tests.</summary>
internal static class ScopeTestAircraft
{
    public static AircraftModel CreateModel()
    {
        return new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0),
            Altitude = 23000,
            GroundSpeed = 250,
            CwtCode = "D",
        };
    }

    /// <summary>ATPA lead 3.2 nm due east of <see cref="CreateModel"/>, as in <c>RadarDatablockLayoutTests</c>.</summary>
    public static AircraftModel CreateAtpaLead()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (4.00704 / 60.0)),
            Altitude = 23000,
            GroundSpeed = 250,
        };
    }

    /// <summary>Conflict peer 2.0 nm east of <see cref="CreateModel"/> and 800 ft below it.</summary>
    public static AircraftModel CreateConflictPeer()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (2.5044 / 60.0)),
            Altitude = 22200,
            GroundSpeed = 250,
            Owner = "CD",
        };
    }

    public static AircraftModel CreateAtpaTrailer()
    {
        AircraftModel ac = CreateModel();
        ac.AtpaLeadCallsign = "SWA1234";
        ac.AtpaAllowedSeparationNm = 3.0;
        ac.AtpaConeState = AtpaConeState.Warning;
        return ac;
    }

    /// <summary>An aircraft carrying every flashing field at once: handoff, ident, no-landing-clearance and conflict.</summary>
    public static AircraftModel CreateEverythingFlashing()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.HandoffPeerSectorCode = "3E";
        ac.IsIdenting = true;
        ac.NoLandingClearanceWarningActive = true;
        ac.ConflictPeerCallsign = "SWA1234";
        ac.Note = "Watch wake";
        return ac;
    }

    public static TrackPresenterInput Input(
        AircraftModel ac,
        string templateId,
        bool flashOn = true,
        AircraftModel? atpaLead = null,
        AircraftModel? conflictPeer = null
    ) => new(ac, templateId, atpaLead, conflictPeer, flashOn, IsSelected: false, AllAlerts, AllDisplay, Mva: null);

    /// <summary>Every alert overlay on, matching the flags the parity rows pass to today's layouts.</summary>
    public static readonly ScopeAlertOptions AllAlerts = new(ShowAtpa: true, ShowConflictAlerts: true, ShowNoLandingClearance: true);

    /// <summary>Every display hint on, as the preferences default.</summary>
    public static readonly ScopeDisplayOptions AllDisplay = new(MarkStudentLevel: true, ShowTypeMismatchHints: true, ShowMvaAltitudeTint: true);

    /// <summary>A beacon-code mismatch: Mode C reporting 1200 while 0301 is assigned.</summary>
    public static AircraftModel CreateSquawkMismatch()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        return ac;
    }
}

/// <summary>
/// Classes that pin the process-wide <see cref="RadarBlink.Clock"/>. Parallelization is off so no other test class
/// reads the blink while a pin is in place.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RadarBlinkClockCollection
{
    public const string Name = "RadarBlinkClock";
}

/// <summary>Pins <see cref="RadarBlink.Clock"/> for a <c>using</c> scope and restores the previous clock on dispose.</summary>
internal sealed class RadarBlinkPin : IDisposable
{
    public const long OnPhaseMs = 0;
    public const long OffPhaseMs = 500;

    private readonly Func<long> _previous;

    private RadarBlinkPin(Func<long> clock)
    {
        _previous = RadarBlink.Clock;
        RadarBlink.Clock = clock;
    }

    public static RadarBlinkPin To(Func<long> clock) => new(clock);

    public static RadarBlinkPin Phase(bool on) => To(on ? static () => OnPhaseMs : static () => OffPhaseMs);

    public void Dispose() => RadarBlink.Clock = _previous;
}
