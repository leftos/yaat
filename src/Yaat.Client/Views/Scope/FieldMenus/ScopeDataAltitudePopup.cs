using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The popup a click on an ERAM block's altitude opens below the block: one panel with two columns side by side. SCOPE DATA
/// holds one altitude box (hundreds of feet, 1-999) feeding every row that takes an altitude, then the altitude rows and the
/// clear rows, each showing the ERAM entry it runs (<see cref="ScopeDataEntries"/>); the altitude rows are disabled while the
/// box holds no altitude. PILOT holds the Maintain altitude panel the catalog's entry builds
/// (<see cref="AltitudeFlyout.MaintainPanelFactory"/>), sending through the host. The chrome, Shift, Esc and the light dismiss
/// are <see cref="ScopeDataPopupBase"/>'s.
/// </summary>
internal sealed class ScopeDataAltitudePopup : ScopeDataPopupBase
{
    /// <summary>What the empty altitude box reads.</summary>
    public const string BoxPlaceholder = "altitude, e.g. 180";

    private readonly AircraftModel _aircraft;

    /// <param name="aircraft">The clicked track, whose callsign every entry names last.</param>
    /// <param name="host">The host the PILOT panel sends through and reads the field elevation, the MVA and the clock from.</param>
    /// <param name="recordShift">Told whether Shift is held at each press, release and key press inside the popup.</param>
    /// <param name="pick">Runs a SCOPE DATA row's entry, or stages it while Shift is held.</param>
    public ScopeDataAltitudePopup(AircraftModel aircraft, IMenuHost host, Action<bool> recordShift, Action<string> pick)
        : base(aircraft.Callsign, pick)
    {
        _aircraft = aircraft;
        AltitudeBox = NewBox(BoxPlaceholder);
        AltitudeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Present();
            }
        };

        PilotPanel = AltitudeFlyout.MaintainPanelFactory(aircraft, host)?.Invoke(Chooser(host));
        Control[] pilot = (PilotPanel is null) ? [] : [PilotPanel];
        IFlyoutPanel[] panels = (PilotPanel is IFlyoutPanel panel) ? [panel] : [];
        Attach(Panel(AltitudeColumn(), PilotColumn(pilot), scrollPilot: false), recordShift, host, panels);
    }

    /// <summary>The SCOPE DATA altitude box, in hundreds of feet.</summary>
    public TextBox AltitudeBox { get; }

    /// <summary>The PILOT column's Maintain altitude panel; null when no altitude is listed for the aircraft.</summary>
    public MaintainAltitudePanel? PilotPanel { get; }

    /// <inheritdoc/>
    protected override TextBox FocusBox => AltitudeBox;

    // The PILOT list centres on the selected altitude once the box has the focus.
    protected override void AfterFocus() => PilotPanel?.CentreSelected();

    // The box's text as hundreds of feet, 1-999; null for anything else.
    private int? Hundreds() =>
        (
            (int.TryParse((AltitudeBox.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int hundreds))
            && (hundreds is >= 1 and <= 999)
        )
            ? hundreds
            : null;

    // An altitude row: its entry for the box's altitude, none while the box holds none, when it shows the entry for 001 with
    // that first 001 (the altitude, ahead of the flight ID) elided.
    private void AddAltitudeRow(StackPanel column, string label, Func<int, string, string> entry) => AddAltitudeRow(column, label, entry, () => "");

    // As the other overload, with the note noteNow gives.
    private void AddAltitudeRow(StackPanel column, string label, Func<int, string, string> entry, Func<string> noteNow)
    {
        string sample = entry(1, Flid);
        int at = sample.IndexOf("001", StringComparison.Ordinal);
        string elided = string.Concat(sample.AsSpan(0, at), Elided, sample.AsSpan(at + 3));
        AddRow(column, label, noteNow, () => (Hundreds() is { } h) ? entry(h, Flid) : null, elided);
    }

    // The flight-plan altitude in hundreds of feet, three digits; null while the plan has none.
    private string? FpHundreds() =>
        (_aircraft.CruiseAltitude > 0) ? (_aircraft.CruiseAltitude / 100).ToString("D3", CultureInfo.InvariantCulture) : null;

    // The title line's note: "FP 250 · interim 180", or "· no interim" while none is set.
    private string TitleNote()
    {
        string interim =
            (_aircraft.EramInterimAltitude is { } hundreds) ? $"interim {hundreds.ToString("D3", CultureInfo.InvariantCulture)}" : "no interim";
        return $"FP {FpHundreds() ?? Elided} · {interim}";
    }

    // The FP altitude row's note: the flight-plan altitude now; none while the plan has none.
    private string FpNote() => (FpHundreds() is { } fp) ? $"now {fp}" : "";

    // A clear row: its entry from the flight ID alone, whatever the box holds.
    private void AddClearRow(StackPanel column, string label, Func<string, string> entry)
    {
        string text = entry(Flid);
        AddRow(column, label, "", () => text, text);
    }

    private StackPanel AltitudeColumn()
    {
        StackPanel column = ScopeDataColumn();
        AddCaption(column, "Scope data", TitleNote, FontWeight.Normal);
        column.Children.Add(BoxRow(AltitudeBox));
        column.Children.Add(FlyoutPanelParts.SeparatorLine());
        AddAltitudeRow(column, "FP altitude", ScopeDataEntries.FpAltitude, FpNote);
        AddAltitudeRow(column, "Interim", ScopeDataEntries.Interim);
        AddAltitudeRow(column, "Local interim", ScopeDataEntries.Local);
        AddAltitudeRow(column, "Procedure", ScopeDataEntries.Procedure);
        AddAltitudeRow(column, "Interim + CERA", ScopeDataEntries.InterimWithCera);
        AddAltitudeRow(column, "CERA only", ScopeDataEntries.CeraOnly);
        column.Children.Add(FlyoutPanelParts.SeparatorLine());
        AddClearRow(column, "Clear interim / procedure", ScopeDataEntries.ClearInterimOrProcedure);
        AddClearRow(column, "Clear local", ScopeDataEntries.ClearLocal);
        AddClearRow(column, "Clear CERA", ScopeDataEntries.ClearCera);
        return column;
    }
}
