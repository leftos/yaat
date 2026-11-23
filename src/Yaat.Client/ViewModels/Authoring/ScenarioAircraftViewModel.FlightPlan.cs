using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The detail panel's flight plan block. The Has flight plan checkbox removes the plan, or makes one with IFR rules and
/// the aircraft's type as its filed type, one undo step each; the fields show only while there is a plan. The filed type
/// is its own field from then on: a later edit of the aircraft's type leaves it. Cruise altitude and speed are whole
/// numbers of 0 or more (blank is 0); every other field is text, trimmed, blank written as empty.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    /// <summary>The flight rules the rules picker offers.</summary>
    public static readonly IReadOnlyList<string> FlightRulesChoices = ["IFR", "VFR"];

    // A cruise altitude or speed: a whole number of 0 or more; blank is 0.
    private static readonly ScenarioFieldKind<int> CruiseNumber = new(
        TryCruiseNumber,
        v => v.ToString(CultureInfo.InvariantCulture),
        ScenarioFieldText.NonNegativeWholeNumberErrorText
    );

    // Each flight plan text field by its commit's undo step: how it shows the plan's value, its error cleared.
    private static readonly (string Step, Action<ScenarioAircraftViewModel, ScenarioAircraft> Load)[] FlightPlanFieldLoads =
    [
        ("Edit departure", (vm, a) => vm.Departure = a.FlightPlan?.Departure ?? ""),
        ("Edit destination", (vm, a) => vm.Destination = a.FlightPlan?.Destination ?? ""),
        ("Edit cruise altitude", (vm, a) => (vm.CruiseAltitude, vm.CruiseAltitudeError) = (CruiseText(a.FlightPlan?.CruiseAltitude), null)),
        ("Edit cruise speed", (vm, a) => (vm.CruiseSpeed, vm.CruiseSpeedError) = (CruiseText(a.FlightPlan?.CruiseSpeed), null)),
        ("Edit route", (vm, a) => vm.FlightPlanRoute = a.FlightPlan?.Route ?? ""),
        ("Edit remarks", (vm, a) => vm.Remarks = a.FlightPlan?.Remarks ?? ""),
        ("Edit filed type", (vm, a) => vm.FiledType = a.FlightPlan?.AircraftType ?? ""),
    ];

    // The rules picker's one options collection, updated in place.
    private readonly ObservableCollection<string> _flightRulesOptions = [.. FlightRulesChoices];

    [ObservableProperty]
    private bool _hasFlightPlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightRulesPick))]
    private string _flightRules = "";

    [ObservableProperty]
    private string _departure = "";

    [ObservableProperty]
    private string _destination = "";

    [ObservableProperty]
    private string _cruiseAltitude = "";

    [ObservableProperty]
    private string? _cruiseAltitudeError;

    [ObservableProperty]
    private string _cruiseSpeed = "";

    [ObservableProperty]
    private string? _cruiseSpeedError;

    [ObservableProperty]
    private string _flightPlanRoute = "";

    [ObservableProperty]
    private string _remarks = "";

    [ObservableProperty]
    private string _filedType = "";

    /// <summary>Gets the rules picker's options: IFR and VFR, plus the plan's rules when they are neither.</summary>
    public IReadOnlyList<string> FlightRulesOptions => _flightRulesOptions;

    /// <summary>Gets or sets the rules picker's selection; only the user's pick of other rules is committed.</summary>
    public string? FlightRulesPick
    {
        get => FlightRules;
        set => ScenarioFieldText.Pick(value, FlightRules, v => FlightRules = v, CommitFlightRules);
    }

    partial void OnHasFlightPlanChanged(bool value)
    {
        if (_loadingDetail)
        {
            return;
        }

        if (value)
        {
            ApplyToSelected("Add flight plan", a => a.FlightPlan = new ScenarioFlightPlan { Rules = "IFR", AircraftType = a.AircraftType });
        }
        else
        {
            ApplyToSelected("Remove flight plan", a => a.FlightPlan = null);
        }
    }

    /// <summary>Writes the departure, trimmed.</summary>
    [RelayCommand]
    private void CommitDeparture() => CommitText("Edit departure", Departure, (a, v) => a.FlightPlan?.Departure = v ?? "");

    /// <summary>Writes the destination, trimmed.</summary>
    [RelayCommand]
    private void CommitDestination() => CommitText("Edit destination", Destination, (a, v) => a.FlightPlan?.Destination = v ?? "");

    /// <summary>Writes the cruise altitude in feet, a whole number of 0 or more; blank is 0.</summary>
    [RelayCommand]
    private void CommitCruiseAltitude() =>
        CommitParsed(CruiseNumber, CruiseAltitude, e => CruiseAltitudeError = e, "Edit cruise altitude", (a, v) => a.FlightPlan?.CruiseAltitude = v);

    /// <summary>Writes the cruise speed in knots, a whole number of 0 or more; blank is 0.</summary>
    [RelayCommand]
    private void CommitCruiseSpeed() =>
        CommitParsed(CruiseNumber, CruiseSpeed, e => CruiseSpeedError = e, "Edit cruise speed", (a, v) => a.FlightPlan?.CruiseSpeed = v);

    /// <summary>Writes the route, trimmed.</summary>
    [RelayCommand]
    private void CommitFlightPlanRoute() => CommitText("Edit route", FlightPlanRoute, (a, v) => a.FlightPlan?.Route = v ?? "");

    /// <summary>Writes the remarks, trimmed.</summary>
    [RelayCommand]
    private void CommitRemarks() => CommitText("Edit remarks", Remarks, (a, v) => a.FlightPlan?.Remarks = v ?? "");

    /// <summary>Writes the filed type, trimmed; it is the plan's own and never follows the aircraft's type.</summary>
    [RelayCommand]
    private void CommitFiledType() => CommitText("Edit filed type", FiledType, (a, v) => a.FlightPlan?.AircraftType = v ?? "");

    private void CommitFlightRules()
    {
        string rules = FlightRules;
        ApplyToSelected("Edit flight rules", a => a.FlightPlan?.Rules = rules);
    }

    // The checkbox and the rules picker from the aircraft's plan; its text fields load with the panel's other fields.
    private void LoadFlightPlan(ScenarioAircraft aircraft)
    {
        HasFlightPlan = aircraft.FlightPlan is not null;
        string rules = aircraft.FlightPlan?.Rules ?? "";
        ScenarioFieldText.SetOptions(
            _flightRulesOptions,
            ScenarioFieldText.Picks(FlightRulesChoices, rules),
            () => OnPropertyChanged(nameof(FlightRulesPick))
        );
        FlightRules = rules;
    }

    private static string CruiseText(int? value) => (value is { } number) ? CruiseNumber.Format(number) : "";

    private static bool TryCruiseNumber(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && (value >= 0);
    }
}
