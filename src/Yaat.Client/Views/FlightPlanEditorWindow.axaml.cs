using System;
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
        _aircraft = aircraft;

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
        BcnText.Text = aircraft.BeaconCode.ToString("D4");
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
        int? cruiseSpeed = int.TryParse(SpdBox.Text, out var spd) ? spd : null;

        var altText = AltBox.Text?.Trim() ?? "";
        var parsed = AircraftModel.ParseAltitudeField(altText);
        string? flightRules = parsed?.FlightRules;
        int? cruiseAlt = parsed?.CruiseAltitude;

        var typText = TypBox.Text?.Trim().ToUpperInvariant() ?? "";
        var eqText = EqBox.Text?.Trim().ToUpperInvariant() ?? "";
        // Match CRC: when the type is set but the equipment suffix is left blank, default to A.
        if (!string.IsNullOrEmpty(typText) && string.IsNullOrEmpty(eqText))
        {
            eqText = "A";
        }

        // Re-glue any RMK/ prefix that was hidden during editing so the protocol header
        // (+/V/PILOT/, etc.) round-trips intact.
        var rmkText = RmkBox.Text?.Trim() ?? "";
        var rebuiltRemarks = string.IsNullOrEmpty(_strippedRemarksPrefix) ? rmkText : _strippedRemarksPrefix + "RMK/" + rmkText;

        var amendment = new FlightPlanAmendment(
            AircraftType: typText,
            EquipmentSuffix: eqText,
            Departure: DepBox.Text?.Trim().ToUpperInvariant(),
            Destination: DestBox.Text?.Trim().ToUpperInvariant(),
            CruiseSpeed: cruiseSpeed,
            CruiseAltitude: cruiseAlt,
            FlightRules: flightRules,
            Route: RteBox.Text?.Trim().ToUpperInvariant(),
            Remarks: rebuiltRemarks,
            Scratchpad1: null,
            Scratchpad2: null,
            BeaconCode: null
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
