using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;

namespace Yaat.Client.Views;

public partial class FlightPlanEditorWindow : Window
{
    private AircraftModel _aircraft;
    private readonly Action<string, FlightPlanAmendment> _onAmend;
    private readonly Func<string, Task> _onRequestNewBeacon;

    private string _origTyp = "";
    private string _origEq = "";
    private string _origDep = "";
    private string _origDest = "";
    private string _origSpd = "";
    private string _origAlt = "";
    private string _origRte = "";
    private string _origRmk = "";
    private string _strippedRemarksPrefix = "";

    public FlightPlanEditorWindow()
    {
        InitializeComponent();
        _aircraft = null!;
        _onAmend = null!;
        _onRequestNewBeacon = null!;
    }

    public FlightPlanEditorWindow(AircraftModel aircraft, Action<string, FlightPlanAmendment> onAmend, Func<string, Task> onRequestNewBeacon)
    {
        InitializeComponent();
        _aircraft = aircraft;
        _onAmend = onAmend;
        _onRequestNewBeacon = onRequestNewBeacon;

        LoadAircraft(aircraft);

        SubmitButton.Click += OnAmendClick;
        RecycleBcnButton.Click += OnRecycleBeaconClick;

        TypBox.TextChanged += OnFieldChanged;
        EqBox.TextChanged += OnFieldChanged;
        DepBox.TextChanged += OnFieldChanged;
        DestBox.TextChanged += OnFieldChanged;
        SpdBox.TextChanged += OnFieldChanged;
        AltBox.TextChanged += OnFieldChanged;
        RteBox.TextChanged += OnFieldChanged;
        RmkBox.TextChanged += OnFieldChanged;

        // Input masks (mirror CRC's FlightPlanEditorContent.xaml.cs PreviewTextInput regexes).
        TypBox.TextInput += MaskAlphaUpper;
        EqBox.TextInput += MaskAlphaUpper;
        DepBox.TextInput += MaskAlphanumericUpper;
        DestBox.TextInput += MaskAlphanumericUpper;
        SpdBox.TextInput += MaskDigitsOnly;
        AltBox.TextInput += MaskAltitude;
        RteBox.TextInput += MaskRoute;
        RmkBox.TextInput += MaskRemarks;
    }

    public void LoadAircraft(AircraftModel aircraft)
    {
        // The read-only display (BCN, Create/Amend label) must track the live aircraft — a
        // beacon assigned or recycled after the editor opens arrives via a later AircraftUpdated
        // push. Re-target the subscription without re-running the editable-field copy below, which
        // would clobber in-progress edits.
        if (_aircraft is not null)
        {
            _aircraft.PropertyChanged -= OnAircraftPropertyChanged;
        }
        _aircraft = aircraft;
        _aircraft.PropertyChanged += OnAircraftPropertyChanged;

        Title = $"{aircraft.Callsign} - Flight Plan";

        _origTyp = aircraft.FiledAircraftType;
        _origEq = aircraft.EquipmentSuffix;
        _origDep = aircraft.Departure;
        _origDest = aircraft.Destination;
        _origSpd = aircraft.CruiseSpeed > 0 ? aircraft.CruiseSpeed.ToString() : "";
        _origAlt = aircraft.CruiseAltitudeDisplay;
        _origRte = aircraft.Route;
        _origRmk = SplitRemarks(aircraft.Remarks, out _strippedRemarksPrefix);

        AidText.Text = aircraft.Callsign;
        BcnText.Text = aircraft.AssignedBeaconCode.ToString("D4");
        TypBox.Text = _origTyp;
        EqBox.Text = _origEq;
        DepBox.Text = _origDep;
        DestBox.Text = _origDest;
        SpdBox.Text = _origSpd;
        AltBox.Text = _origAlt;
        RteBox.Text = _origRte;
        RmkBox.Text = _origRmk;

        SubmitButton.Content = aircraft.HasFlightPlan ? "Amend" : "Create";
        SubmitButton.IsEnabled = false;
    }

    /// <summary>
    /// Splits a remarks string on the first <c>RMK/</c> separator. The portion before
    /// (e.g. <c>+/V/PILOT</c>) is the protocol-prefix that round-trips silently; the
    /// portion after is the user-editable remark text. Mirrors CRC's
    /// <c>FlightPlanEditorViewModel.StripRemarks</c>.
    /// </summary>
    private static string SplitRemarks(string remarks, out string strippedPrefix)
    {
        var parts = remarks.Split("RMK/", 2, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            strippedPrefix = "";
            return remarks;
        }
        strippedPrefix = parts[0];
        return parts[1];
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SubmitButton.IsEnabled)
        {
            OnAmendClick(null, null!);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // Refresh only the read-only display when the live aircraft changes. Never touch the editable
    // boxes — that would clobber the instructor's in-progress edits.
    private void OnAircraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AircraftModel.AssignedBeaconCode):
                BcnText.Text = _aircraft.AssignedBeaconCode.ToString("D4");
                break;
            case nameof(AircraftModel.HasFlightPlan):
                SubmitButton.Content = _aircraft.HasFlightPlan ? "Amend" : "Create";
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_aircraft is not null)
        {
            _aircraft.PropertyChanged -= OnAircraftPropertyChanged;
        }

        base.OnClosed(e);
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e)
    {
        SubmitButton.IsEnabled = HasChanges();
    }

    private bool HasChanges()
    {
        return (TypBox.Text ?? "") != _origTyp
            || (EqBox.Text ?? "") != _origEq
            || (DepBox.Text ?? "") != _origDep
            || (DestBox.Text ?? "") != _origDest
            || (SpdBox.Text ?? "") != _origSpd
            || (AltBox.Text ?? "") != _origAlt
            || (RteBox.Text ?? "") != _origRte
            || (RmkBox.Text ?? "") != _origRmk;
    }

    private void OnAmendClick(object? sender, RoutedEventArgs e)
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: TypBox.Text,
            eqText: EqBox.Text,
            depText: DepBox.Text,
            destText: DestBox.Text,
            spdText: SpdBox.Text,
            altText: AltBox.Text,
            rteText: RteBox.Text,
            rmkText: RmkBox.Text,
            strippedRemarksPrefix: _strippedRemarksPrefix
        );

        _onAmend(_aircraft.Callsign, amendment);

        _origTyp = TypBox.Text ?? "";
        _origEq = EqBox.Text ?? "";
        _origDep = DepBox.Text ?? "";
        _origDest = DestBox.Text ?? "";
        _origSpd = SpdBox.Text ?? "";
        _origAlt = AltBox.Text ?? "";
        _origRte = RteBox.Text ?? "";
        _origRmk = RmkBox.Text ?? "";
        SubmitButton.IsEnabled = false;
    }

    private async void OnRecycleBeaconClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _onRequestNewBeacon(_aircraft.Callsign);
        }
        catch (Exception ex)
        {
            // Surface the error in the title so the instructor sees it without a popup.
            Title = $"{_aircraft.Callsign} - Flight Plan (recycle failed: {ex.Message})";
        }
    }

    // ── Input masks (mirrors CRC's FlightPlanEditorContent.xaml.cs PreviewTextInput) ──

    private static readonly Regex AlphaUpperPattern = new("[^A-Z]+", RegexOptions.Compiled);
    private static readonly Regex AlphanumericUpperPattern = new("[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex DigitsPattern = new("[^0-9]+", RegexOptions.Compiled);
    private static readonly Regex AltitudePattern = new("[^0-9VFROTP/]+", RegexOptions.Compiled);
    private static readonly Regex RoutePattern = new("[^A-Z0-9./+ ]+", RegexOptions.Compiled);

    private static void Mask(TextInputEventArgs e, Regex disallowed)
    {
        if (e.Text is { } text && disallowed.IsMatch(text.ToUpperInvariant()))
        {
            e.Handled = true;
        }
    }

    private static void MaskAlphaUpper(object? sender, TextInputEventArgs e) => Mask(e, AlphaUpperPattern);

    private static void MaskAlphanumericUpper(object? sender, TextInputEventArgs e) => Mask(e, AlphanumericUpperPattern);

    private static void MaskDigitsOnly(object? sender, TextInputEventArgs e) => Mask(e, DigitsPattern);

    private static void MaskAltitude(object? sender, TextInputEventArgs e) => Mask(e, AltitudePattern);

    private static void MaskRoute(object? sender, TextInputEventArgs e) => Mask(e, RoutePattern);

    private static void MaskRemarks(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { } text && text.Contains(':'))
        {
            e.Handled = true;
        }
    }
}
