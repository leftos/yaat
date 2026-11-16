using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The TOOLS tray's Rings tool: the kind it is armed with, the SIZE it places, and the rings placed per track. Rings are
/// this Scope's own overlay, keyed by callsign: never saved, never sent to the server, and cleared when their aircraft
/// leaves and on a scenario unload, restart or rewind. The per-track PTLs live beside them (<c>ScopeViewModel.Ptl.cs</c>).
/// </summary>
public sealed partial class ScopeViewModel
{
    internal static readonly IReadOnlyDictionary<string, ScopeTrackRings> NoRings = new Dictionary<string, ScopeTrackRings>();

    private readonly Dictionary<string, ScopeTrackRings> _rings = new(StringComparer.Ordinal);

    /// <summary>
    /// The kind a left click on a track places, or <see cref="ScopeRingTool.None"/>: set from the band's rings value when the
    /// TOOLS tray opens, changed by a kind button, and cleared by Esc, a second press of the lit button or the tray closing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRingToolArmed))]
    private ScopeRingTool _armedRingTool = ScopeRingTool.None;

    /// <summary>The J-ring radius and cone length a placement takes, in NM, on CRC's grid (<see cref="ScopeRingGeometry.SizeGrid"/>).</summary>
    [ObservableProperty]
    private double _ringSizeNm = ScopeRingGeometry.DefaultSizeNm;

    /// <summary>The rings placed per callsign, a snapshot replaced on every change; the canvas lays them out from it.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, ScopeTrackRings> _localRings = NoRings;

    /// <summary>Whether the Rings tool is armed, so a left click on a track places a ring instead of selecting it.</summary>
    public bool IsRingToolArmed => ArmedRingTool != ScopeRingTool.None;

    /// <summary>
    /// Whether <paramref name="rings"/> carries the ring <paramref name="tool"/> places: a J-ring or cone of any size, or the
    /// DRI of the tool's radius.
    /// </summary>
    internal static bool Carries(ScopeTrackRings rings, ScopeRingTool tool) =>
        tool switch
        {
            ScopeRingTool.JRing => rings.JRingNm is not null,
            ScopeRingTool.Cone => rings.ConeNm is not null,
            ScopeRingTool.Dri5 => rings.Dri == ScopeDriKind.Standard,
            ScopeRingTool.Dri3 => rings.Dri == ScopeDriKind.Reduced,
            _ => false,
        };

    /// <summary>
    /// <paramref name="rings"/> after placing the ring <paramref name="tool"/> places at <paramref name="sizeNm"/> (a DRI
    /// ignores it): a kind the track already has at the same size is removed, at another size resized; the other kinds stay.
    /// A track carries one DRI, so DRI 5 replaces DRI 3 and the other way round.
    /// </summary>
    internal static ScopeTrackRings Toggled(ScopeTrackRings rings, ScopeRingTool tool, double sizeNm) =>
        tool switch
        {
            ScopeRingTool.JRing => rings with { JRingNm = SameSize(rings.JRingNm, sizeNm) ? null : sizeNm },
            ScopeRingTool.Cone => rings with { ConeNm = SameSize(rings.ConeNm, sizeNm) ? null : sizeNm },
            ScopeRingTool.Dri5 => rings with { Dri = (rings.Dri == ScopeDriKind.Standard) ? null : ScopeDriKind.Standard },
            ScopeRingTool.Dri3 => rings with { Dri = (rings.Dri == ScopeDriKind.Reduced) ? null : ScopeDriKind.Reduced },
            _ => rings,
        };

    /// <summary>The rings placed on the track with <paramref name="callsign"/>; <see cref="ScopeTrackRings.None"/> when none.</summary>
    public ScopeTrackRings RingsOf(string callsign) => _rings.GetValueOrDefault(callsign) ?? ScopeTrackRings.None;

    /// <summary>Replaces the rings on the track with <paramref name="callsign"/>; empty rings remove its entry.</summary>
    public void SetRings(string callsign, ScopeTrackRings rings)
    {
        if (rings.IsEmpty)
        {
            _rings.Remove(callsign);
        }
        else
        {
            _rings[callsign] = rings;
        }

        PublishRings();
    }

    /// <summary>Removes every ring on the track with <paramref name="callsign"/>.</summary>
    public void ClearRings(string callsign)
    {
        if (_rings.Remove(callsign))
        {
            PublishRings();
        }
    }

    /// <summary>
    /// Drops every piece of per-track state the scope holds, its data-block state (dwell locks, block offsets, highlights,
    /// z-order), its rings and its PTLs, for a scenario unload, restart or rewind.
    /// </summary>
    public void ClearTrackState()
    {
        DataBlockState.Clear();
        ClearRings();
        ClearPtl();
    }

    /// <summary>Removes every ring on every track.</summary>
    public void ClearRings()
    {
        if (_rings.Count > 0)
        {
            _rings.Clear();
            PublishRings();
        }
    }

    /// <summary>
    /// A left click on the track with <paramref name="callsign"/> while the tool is armed: places, resizes or removes the armed
    /// kind through <c>.scope rings</c>, leaving the selection alone. Disarmed, it does nothing.
    /// </summary>
    public void PlaceRing(string callsign)
    {
        if (IsRingToolArmed)
        {
            RunCommand(ScopeToolbarButtons.RingsCommand(ArmedRingTool, RingSizeNm, callsign));
        }
    }

    /// <summary>Disarms the Rings tool, as Esc does.</summary>
    /// <returns>False when it was not armed.</returns>
    public bool DisarmRingTool()
    {
        if (!IsRingToolArmed)
        {
            return false;
        }

        ArmedRingTool = ScopeRingTool.None;
        return true;
    }

    /// <summary>The kind the TOOLS tray arms when it opens: DRI 5 when the band's rings value is <c>dri</c>, else J-ring.</summary>
    internal ScopeRingTool BandRingTool() => (EffectiveBand.Rings == ScopeProfile.RingsDri) ? ScopeRingTool.Dri5 : ScopeRingTool.JRing;

    /// <summary>
    /// A press of a TOOLS kind button: arms its kind, or disarms when it is the lit one; the tray stays open. False for any
    /// other button.
    /// </summary>
    private bool TryPressRingTool(ScopeButton button)
    {
        ScopeRingTool tool = ScopeToolbarButtons.RingToolOf(button.Id);
        if (tool == ScopeRingTool.None)
        {
            return false;
        }

        ArmedRingTool = (ArmedRingTool == tool) ? ScopeRingTool.None : tool;
        return true;
    }

    /// <summary>Steps SIZE by <paramref name="steps"/> along CRC's grid; a step past an end stops there.</summary>
    private void StepRingSize(int steps) => RingSizeNm = ScopeRingGeometry.StepSize(RingSizeNm, steps);

    // Opening TOOLS arms the band's kind; closing it, or opening another tray, disarms.
    partial void OnOpenMenuChanged(ScopeButtonId? oldValue, ScopeButtonId? newValue)
    {
        if (newValue == ScopeButtonId.Tools)
        {
            ArmedRingTool = BandRingTool();
        }
        else if (oldValue == ScopeButtonId.Tools)
        {
            ArmedRingTool = ScopeRingTool.None;
        }
    }

    private static bool SameSize(double? current, double sizeNm) => (current is { } nm) && (Math.Abs(nm - sizeNm) < 1e-6);

    private void PublishRings() => LocalRings = new Dictionary<string, ScopeTrackRings>(_rings, StringComparer.Ordinal);
}
