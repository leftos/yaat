using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;

namespace Yaat.Client.Views;

public partial class FlightPlanEditorWindow : Window
{
    private readonly AircraftModel _aircraft;
    private readonly Action<string, FlightPlanAmendment> _onAmend;

    private string _origBcn;
    private string _origTyp;
    private string _origEq;
    private string _origDep;
    private string _origDest;
    private string _origSpd;
    private string _origAlt;
    private string _origRte;
    private string _origRmk;

    public FlightPlanEditorWindow()
    {
        InitializeComponent();
        _aircraft = null!;
        _onAmend = null!;
        _origBcn = _origTyp = _origEq = _origDep = _origDest = "";
        _origSpd = _origAlt = _origRte = _origRmk = "";
    }

    public FlightPlanEditorWindow(AircraftModel aircraft, Action<string, FlightPlanAmendment> onAmend)
    {
        InitializeComponent();
        _aircraft = aircraft;
        _onAmend = onAmend;

        Title = $"{aircraft.Callsign} - Flight Plan";

        _origBcn = aircraft.BeaconCode.ToString("D4");
        _origTyp = aircraft.AircraftType;
        _origEq = aircraft.EquipmentSuffix;
        _origDep = aircraft.Departure;
        _origDest = aircraft.Destination;
        _origSpd = aircraft.CruiseSpeed > 0 ? aircraft.CruiseSpeed.ToString() : "";
        _origAlt = aircraft.CruiseAltitudeDisplay;
        _origRte = aircraft.Route;
        _origRmk = aircraft.Remarks;

        AidText.Text = aircraft.Callsign;
        BcnBox.Text = _origBcn;
        TypBox.Text = _origTyp;
        EqBox.Text = _origEq;
        DepBox.Text = _origDep;
        DestBox.Text = _origDest;
        SpdBox.Text = _origSpd;
        AltBox.Text = _origAlt;
        RteBox.Text = _origRte;
        RmkBox.Text = _origRmk;

        AmendButton.IsEnabled = false;
        AmendButton.Click += OnAmendClick;

        BcnBox.TextChanged += OnFieldChanged;
        TypBox.TextChanged += OnFieldChanged;
        EqBox.TextChanged += OnFieldChanged;
        DepBox.TextChanged += OnFieldChanged;
        DestBox.TextChanged += OnFieldChanged;
        SpdBox.TextChanged += OnFieldChanged;
        AltBox.TextChanged += OnFieldChanged;
        RteBox.TextChanged += OnFieldChanged;
        RmkBox.TextChanged += OnFieldChanged;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AmendButton.IsEnabled)
        {
            OnAmendClick(null, null!);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e)
    {
        AmendButton.IsEnabled = HasChanges();
    }

    private bool HasChanges()
    {
        return (BcnBox.Text ?? "") != _origBcn
            || (TypBox.Text ?? "") != _origTyp
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
        uint? beacon = uint.TryParse(BcnBox.Text, out var bcn) ? bcn : null;

        var altText = AltBox.Text?.Trim() ?? "";
        var parsed = AircraftModel.ParseAltitudeField(altText);
        string? flightRules = parsed?.FlightRules;
        int? cruiseAlt = parsed?.CruiseAltitude;

        var amendment = new FlightPlanAmendment(
            AircraftType: TypBox.Text?.Trim().ToUpperInvariant(),
            EquipmentSuffix: EqBox.Text?.Trim().ToUpperInvariant(),
            Departure: DepBox.Text?.Trim().ToUpperInvariant(),
            Destination: DestBox.Text?.Trim().ToUpperInvariant(),
            CruiseSpeed: cruiseSpeed,
            CruiseAltitude: cruiseAlt,
            FlightRules: flightRules,
            Route: RteBox.Text?.Trim().ToUpperInvariant(),
            Remarks: RmkBox.Text?.Trim(),
            Scratchpad1: null,
            Scratchpad2: null,
            BeaconCode: beacon
        );

        _onAmend(_aircraft.Callsign, amendment);

        // Update baselines so HasChanges() returns false until next edit
        _origBcn = BcnBox.Text ?? "";
        _origTyp = TypBox.Text ?? "";
        _origEq = EqBox.Text ?? "";
        _origDep = DepBox.Text ?? "";
        _origDest = DestBox.Text ?? "";
        _origSpd = SpdBox.Text ?? "";
        _origAlt = AltBox.Text ?? "";
        _origRte = RteBox.Text ?? "";
        _origRmk = RmkBox.Text ?? "";
        AmendButton.IsEnabled = false;
    }
}
