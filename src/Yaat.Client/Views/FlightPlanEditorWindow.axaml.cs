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
    private AircraftModel? _aircraft;
    private readonly Action<string, FlightPlanAmendment> _onAmend;
    private readonly Func<string, Task> _onRequestNewBeacon;

    private string _origTyp = "";
    private string _origEq = "";
    private string _origIcaoEq = "";
    private string _origDep = "";
    private string _origDest = "";
    private string _origSpd = "";
    private string _origAlt = "";
    private string _origRte = "";
    private string _origRmk = "";
    private string _strippedRemarksPrefix = "";

    /// <summary>The callsign of the aircraft the editor shows, or null while it is bound to none.</summary>
    public string? Callsign => _aircraft?.Callsign;

    public FlightPlanEditorWindow()
    {
        InitializeComponent();
        _aircraft = null;
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
        IcaoEqBox.TextChanged += OnFieldChanged;
        DepBox.TextChanged += OnFieldChanged;
        DestBox.TextChanged += OnFieldChanged;
        SpdBox.TextChanged += OnFieldChanged;
        AltBox.TextChanged += OnFieldChanged;
        RteBox.TextChanged += OnFieldChanged;
        RmkBox.TextChanged += OnFieldChanged;

        // Input masks (mirror CRC's FlightPlanEditorContent.xaml.cs PreviewTextInput regexes).
        TypBox.TextInput += MaskAlphaUpper;
        EqBox.TextInput += MaskAlphaUpper;
        IcaoEqBox.TextInput += MaskAlphanumericUpper;
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
        UnsubscribeAircraft();
        _aircraft = aircraft;
        _aircraft.PropertyChanged += OnAircraftPropertyChanged;

        Title = $"{aircraft.Callsign} - Flight Plan";

        _origTyp = aircraft.FiledAircraftType;
        _origEq = aircraft.EquipmentSuffix;
        _origIcaoEq = aircraft.IcaoEquipmentCodes;
        _origDep = aircraft.Departure;
        _origDest = aircraft.Destination;
        _origSpd = aircraft.EditorSpeedText;
        SpdBox.PlaceholderText = aircraft.EditorSpeedPlaceholder;
        _origAlt = aircraft.CruiseAltitudeDisplay;
        _origRte = aircraft.Route;
        _origRmk = SplitRemarks(aircraft.Remarks, out _strippedRemarksPrefix);

        AidText.Text = aircraft.Callsign;
        BcnText.Text = aircraft.AssignedBeaconCode.ToString("D4");
        TypBox.Text = _origTyp;
        EqBox.Text = _origEq;
        IcaoEqBox.Text = _origIcaoEq;
        DepBox.Text = _origDep;
        DestBox.Text = _origDest;
        SpdBox.Text = _origSpd;
        AltBox.Text = _origAlt;
        RteBox.Text = _origRte;
        RmkBox.Text = _origRmk;

        SubmitButton.Content = aircraft.HasFlightPlan ? "Amend" : "Create";
        SubmitButton.IsEnabled = false;
        RecycleBcnButton.IsEnabled = true;
    }

    /// <summary>
    /// Drops the aircraft the editor was showing after the server deletes it (an auto-delete on
    /// parking or departure-distance, or a manual DEL). The window stays open — the instructor keeps
    /// their place — but is bound to no aircraft until another one is opened in it: every field is
    /// cleared and Amend/Create stays disabled.
    /// </summary>
    public void ClearAircraft()
    {
        UnsubscribeAircraft();
        _aircraft = null;

        Title = "Flight Plan";
        AidText.Text = "";
        BcnText.Text = "";
        TypBox.Text = "";
        EqBox.Text = "";
        IcaoEqBox.Text = "";
        DepBox.Text = "";
        DestBox.Text = "";
        SpdBox.Text = "";
        SpdBox.PlaceholderText = "";
        AltBox.Text = "";
        RteBox.Text = "";
        RmkBox.Text = "";

        _origTyp = "";
        _origEq = "";
        _origIcaoEq = "";
        _origDep = "";
        _origDest = "";
        _origSpd = "";
        _origAlt = "";
        _origRte = "";
        _origRmk = "";
        _strippedRemarksPrefix = "";

        SubmitButton.Content = "Amend";
        SubmitButton.IsEnabled = false;
        RecycleBcnButton.IsEnabled = false;
    }

    private void UnsubscribeAircraft()
    {
        if (_aircraft is { } aircraft)
        {
            aircraft.PropertyChanged -= OnAircraftPropertyChanged;
        }
    }

    /// <summary>
    /// Splits a remarks string on the first <c>RMK/</c> separator. The portion before
    /// (e.g. <c>+/V/PILOT</c>) is the protocol-prefix that round-trips silently; the
    /// portion after is the user-editable remark text. Mirrors CRC's
    /// <c>FlightPlanEditorViewModel.StripRemarks</c>.
    /// </summary>
    private static string SplitRemarks(string remarks, out string strippedPrefix)
    {
        string[] parts = remarks.Split("RMK/", 2, StringSplitOptions.None);
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

    // Reflect live aircraft changes while the editor is open. The read-only display (BCN,
    // Create/Amend label) always tracks the live aircraft; editable fields refresh per-field so
    // an amendment from another controller shows up without clobbering the instructor's
    // in-progress edits (see RefreshEditableField).
    private void OnAircraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_aircraft is not { } aircraft)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AircraftModel.AssignedBeaconCode):
                BcnText.Text = aircraft.AssignedBeaconCode.ToString("D4");
                break;
            case nameof(AircraftModel.HasFlightPlan):
                SubmitButton.Content = aircraft.HasFlightPlan ? "Amend" : "Create";
                break;
            case nameof(AircraftModel.FiledAircraftType):
                RefreshEditableField(TypBox, ref _origTyp, aircraft.FiledAircraftType);
                break;
            case nameof(AircraftModel.EquipmentSuffix):
                RefreshEditableField(EqBox, ref _origEq, aircraft.EquipmentSuffix);
                break;
            case nameof(AircraftModel.IcaoEquipmentCodes):
                RefreshEditableField(IcaoEqBox, ref _origIcaoEq, aircraft.IcaoEquipmentCodes);
                break;
            case nameof(AircraftModel.Departure):
                RefreshEditableField(DepBox, ref _origDep, aircraft.Departure);
                break;
            case nameof(AircraftModel.Destination):
                RefreshEditableField(DestBox, ref _origDest, aircraft.Destination);
                break;
            case nameof(AircraftModel.EditorSpeedText):
                RefreshEditableField(SpdBox, ref _origSpd, aircraft.EditorSpeedText);
                break;
            case nameof(AircraftModel.EditorSpeedPlaceholder):
                SpdBox.PlaceholderText = aircraft.EditorSpeedPlaceholder;
                break;
            case nameof(AircraftModel.CruiseAltitudeDisplay):
                RefreshEditableField(AltBox, ref _origAlt, aircraft.CruiseAltitudeDisplay);
                break;
            case nameof(AircraftModel.Route):
                RefreshEditableField(RteBox, ref _origRte, aircraft.Route);
                break;
            case nameof(AircraftModel.Remarks):
                RefreshEditableField(RmkBox, ref _origRmk, SplitRemarks(aircraft.Remarks, out _strippedRemarksPrefix));
                break;
        }
    }

    // A field the instructor hasn't touched (box text still equals its baseline) refreshes to the
    // live value. A field mid-edit keeps the instructor's text; only its change-baseline moves, so
    // HasChanges compares the edit against the live plan rather than the values from open time.
    private void RefreshEditableField(TextBox box, ref string baseline, string newValue)
    {
        bool untouched = (box.Text ?? "") == baseline;
        baseline = newValue;
        if (untouched)
        {
            box.Text = newValue;
        }
        SubmitButton.IsEnabled = HasChanges();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeAircraft();

        base.OnClosed(e);
    }

    // A cleared editor stays inert: typing into its boxes must not re-arm Amend, which would submit
    // the typed text against an aircraft the window is no longer bound to.
    private void OnFieldChanged(object? sender, TextChangedEventArgs e) => SubmitButton.IsEnabled = (_aircraft is not null) && HasChanges();

    private bool HasChanges()
    {
        return (TypBox.Text ?? "") != _origTyp
            || (EqBox.Text ?? "") != _origEq
            || (IcaoEqBox.Text ?? "") != _origIcaoEq
            || (DepBox.Text ?? "") != _origDep
            || (DestBox.Text ?? "") != _origDest
            || (SpdBox.Text ?? "") != _origSpd
            || (AltBox.Text ?? "") != _origAlt
            || (RteBox.Text ?? "") != _origRte
            || (RmkBox.Text ?? "") != _origRmk;
    }

    private void OnAmendClick(object? sender, RoutedEventArgs e)
    {
        if (_aircraft is not { } aircraft)
        {
            return;
        }

        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: TypBox.Text,
            eqText: EqBox.Text,
            icaoEqText: IcaoEqBox.Text,
            depText: DepBox.Text,
            destText: DestBox.Text,
            spdText: SpdBox.Text,
            altText: AltBox.Text,
            rteText: RteBox.Text,
            rmkText: RmkBox.Text,
            strippedRemarksPrefix: _strippedRemarksPrefix,
            originalRemarks: aircraft.Remarks
        );

        _onAmend(aircraft.Callsign, amendment);

        _origTyp = TypBox.Text ?? "";
        _origEq = EqBox.Text ?? "";
        _origIcaoEq = IcaoEqBox.Text ?? "";
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
        if (_aircraft is not { } aircraft)
        {
            return;
        }

        try
        {
            await _onRequestNewBeacon(aircraft.Callsign);
        }
        catch (Exception ex)
        {
            // Surface the error in the title so the instructor sees it without a popup — but only while
            // the editor still shows that aircraft, which may have been re-bound or cleared meanwhile.
            if (ReferenceEquals(_aircraft, aircraft))
            {
                Title = $"{aircraft.Callsign} - Flight Plan (recycle failed: {ex.Message})";
            }
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
