using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The profile section's editor over <see cref="StampProfileStore"/>: one profile's every field as the author edits it,
/// a fresh profile from <see cref="New"/>, and <see cref="Build"/> returning the profile <see cref="Load"/> filled it from.
/// Save, Duplicate and Delete are one write each; a blank name or an id that cannot name a profile file is refused with
/// nothing written, and a write that fails says why in <see cref="Message"/>.
/// </summary>
public sealed partial class StampProfileEditorViewModel : ObservableObject
{
    private const string NewProfileName = "New profile";

    private const string CopySuffix = " (copy)";

    private static readonly ILogger Log = AppLog.CreateLogger<StampProfileEditorViewModel>();

    private readonly StampProfileStore _store;

    /// <summary>Initializes a new instance of the <see cref="StampProfileEditorViewModel"/> class holding a fresh profile.</summary>
    /// <param name="store">The profile store the editor reads, saves, duplicates and deletes in.</param>
    /// <param name="positionIds">The ATC position ids the auto-track position picker offers.</param>
    public StampProfileEditorViewModel(StampProfileStore store, IReadOnlyList<string> positionIds)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        PositionIds = positionIds ?? throw new ArgumentNullException(nameof(positionIds));
        New();
    }

    /// <summary>Raised after the editor wrote a profile to the store: a save or a duplicate.</summary>
    public event Action<StampProfile>? Saved;

    /// <summary>Raised after the editor deleted a profile's file, with the deleted profile's id.</summary>
    public event Action<string>? Deleted;

    /// <summary>Gets the STAR aliases applied to the stamped route, in order.</summary>
    public ObservableCollection<StarAlias> StarAliases { get; } = [];

    /// <summary>Gets the preset commands, each with the seconds after spawn at which it runs.</summary>
    public ObservableCollection<StampPresetCommand> PresetCommands { get; } = [];

    /// <summary>Gets the aircraft types a random type is picked from; an empty pool picks nothing.</summary>
    public ObservableCollection<string> AircraftTypes { get; } = [];

    /// <summary>Gets the callsign prefixes a random callsign is built on; an empty pool picks nothing.</summary>
    public ObservableCollection<string> CallsignPrefixes { get; } = [];

    /// <summary>Gets the runways a random runway is picked from; an empty pool picks nothing.</summary>
    public ObservableCollection<string> Runways { get; } = [];

    /// <summary>Gets the parking spots a random spot is picked from; an empty pool picks nothing.</summary>
    public ObservableCollection<string> ParkingSpots { get; } = [];

    private string _id = "";

    /// <summary>Gets the profile's id, which is also its file name in the store; only <see cref="New"/> and <see cref="Load"/> set it.</summary>
    public string Id
    {
        get => _id;
        private set => SetProperty(ref _id, value);
    }

    /// <summary>Gets or sets the last refusal or write failure to show the author; null when the last write succeeded.</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>Gets or sets the ATC position ids the auto-track position picker offers.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _positionIds = [];

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _heavyOnly;

    [ObservableProperty]
    private string? _primaryAirport;

    [ObservableProperty]
    private string? _expectedApproach;

    [ObservableProperty]
    private bool _startingApply;

    [ObservableProperty]
    private string? _startingType;

    [ObservableProperty]
    private double? _startingAltitude;

    [ObservableProperty]
    private double? _startingSpeed;

    [ObservableProperty]
    private double? _startingHeading;

    [ObservableProperty]
    private bool _startingApplyNavigationPath;

    [ObservableProperty]
    private string? _startingRunway;

    [ObservableProperty]
    private string? _startingParking;

    [ObservableProperty]
    private bool _flightPlanApply;

    [ObservableProperty]
    private string? _flightPlanRules;

    [ObservableProperty]
    private string? _flightPlanDeparture;

    [ObservableProperty]
    private string? _flightPlanDestination;

    [ObservableProperty]
    private bool _flightPlanApplyRoute;

    [ObservableProperty]
    private int? _flightPlanCruiseAltitude;

    [ObservableProperty]
    private int? _flightPlanCruiseSpeed;

    [ObservableProperty]
    private string? _flightPlanRemarks;

    [ObservableProperty]
    private bool _presetsApply;

    [ObservableProperty]
    private bool _presetsReplace;

    [ObservableProperty]
    private bool _autoTrackApply;

    [ObservableProperty]
    private string _autoTrackPositionId = "";

    [ObservableProperty]
    private int? _autoTrackHandoffDelay;

    [ObservableProperty]
    private string? _autoTrackScratchPad;

    [ObservableProperty]
    private string? _autoTrackInterimAltitude;

    [ObservableProperty]
    private string? _autoTrackClearedAltitude;

    /// <summary>Holds a fresh profile with a new id named "New profile": every apply flag off, every list empty.</summary>
    public void New() => Load(new StampProfile { Id = Guid.NewGuid().ToString("N"), Name = NewProfileName });

    /// <summary>Fills the editor's every field and list from a profile.</summary>
    /// <param name="profile">The profile to edit.</param>
    public void Load(StampProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Message = null;
        Id = profile.Id;
        Name = profile.Name;
        HeavyOnly = profile.HeavyOnly;
        PrimaryAirport = profile.PrimaryAirport;
        ExpectedApproach = profile.ExpectedApproach;
        StampStartingConditions starting = profile.StartingConditions;
        StartingApply = starting.Apply;
        StartingType = starting.Type;
        StartingAltitude = starting.Altitude;
        StartingSpeed = starting.Speed;
        StartingHeading = starting.Heading;
        StartingApplyNavigationPath = starting.ApplyNavigationPath;
        StartingRunway = starting.Runway;
        StartingParking = starting.Parking;
        StampFlightPlan plan = profile.FlightPlan;
        FlightPlanApply = plan.Apply;
        FlightPlanRules = plan.Rules;
        FlightPlanDeparture = plan.Departure;
        FlightPlanDestination = plan.Destination;
        FlightPlanApplyRoute = plan.ApplyRoute;
        FlightPlanCruiseAltitude = plan.CruiseAltitude;
        FlightPlanCruiseSpeed = plan.CruiseSpeed;
        FlightPlanRemarks = plan.Remarks;
        PresetsApply = profile.Presets.Apply;
        PresetsReplace = profile.Presets.Replace;
        StampAutoTrack autoTrack = profile.AutoTrack;
        AutoTrackApply = autoTrack.Apply;
        AutoTrackPositionId = autoTrack.PositionId;
        AutoTrackHandoffDelay = autoTrack.HandoffDelay;
        AutoTrackScratchPad = autoTrack.ScratchPad;
        AutoTrackInterimAltitude = autoTrack.InterimAltitude;
        AutoTrackClearedAltitude = autoTrack.ClearedAltitude;
        Fill(StarAliases, profile.StarAliases);
        Fill(PresetCommands, profile.Presets.Commands);
        Fill(AircraftTypes, profile.Pools.AircraftTypes);
        Fill(CallsignPrefixes, profile.Pools.CallsignPrefixes);
        Fill(Runways, profile.Pools.Runways);
        Fill(ParkingSpots, profile.Pools.ParkingSpots);
    }

    /// <summary>The profile the editor holds: every field as it stands and every list in its order.</summary>
    /// <returns>The profile, ready to save.</returns>
    public StampProfile Build() =>
        new()
        {
            Id = Id,
            Name = Name,
            HeavyOnly = HeavyOnly,
            StarAliases = [.. StarAliases],
            StartingConditions = new StampStartingConditions
            {
                Apply = StartingApply,
                Type = StartingType,
                Altitude = StartingAltitude,
                Speed = StartingSpeed,
                Heading = StartingHeading,
                ApplyNavigationPath = StartingApplyNavigationPath,
                Runway = StartingRunway,
                Parking = StartingParking,
            },
            FlightPlan = new StampFlightPlan
            {
                Apply = FlightPlanApply,
                Rules = FlightPlanRules,
                Departure = FlightPlanDeparture,
                Destination = FlightPlanDestination,
                ApplyRoute = FlightPlanApplyRoute,
                CruiseAltitude = FlightPlanCruiseAltitude,
                CruiseSpeed = FlightPlanCruiseSpeed,
                Remarks = FlightPlanRemarks,
            },
            Presets = new StampPresets
            {
                Apply = PresetsApply,
                Replace = PresetsReplace,
                Commands = [.. PresetCommands],
            },
            AutoTrack = new StampAutoTrack
            {
                Apply = AutoTrackApply,
                PositionId = AutoTrackPositionId,
                HandoffDelay = AutoTrackHandoffDelay,
                ScratchPad = AutoTrackScratchPad,
                InterimAltitude = AutoTrackInterimAltitude,
                ClearedAltitude = AutoTrackClearedAltitude,
            },
            PrimaryAirport = PrimaryAirport,
            ExpectedApproach = ExpectedApproach,
            Pools = new StampPools
            {
                AircraftTypes = [.. AircraftTypes],
                CallsignPrefixes = [.. CallsignPrefixes],
                Runways = [.. Runways],
                ParkingSpots = [.. ParkingSpots],
            },
        };

    /// <summary>
    /// Saves the profile the editor holds, replacing the file with the same id. A blank name or an id that cannot name a
    /// profile file is refused with nothing written; a write that fails says why in <see cref="Message"/> and saves nothing.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Message = "Give the profile a name before saving.";
            return;
        }

        if (!LocalScenarioStore.IsValidId(Id))
        {
            Message = $"The profile id \"{Id}\" can't be used as a file name.";
            return;
        }

        StampProfile profile = Build();
        if (TryWrite("save", Id, () => _store.Save(profile)))
        {
            Saved?.Invoke(profile);
        }
    }

    /// <summary>Writes a copy of the profile the editor holds under a new id, its name followed by " (copy)".</summary>
    [RelayCommand]
    private void Duplicate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Message = "Give the profile a name before saving.";
            return;
        }

        StampProfile copy = Build() with { Id = Guid.NewGuid().ToString("N"), Name = Name.Trim() + CopySuffix };
        if (TryWrite("save", copy.Id, () => _store.Save(copy)))
        {
            Saved?.Invoke(copy);
        }
    }

    /// <summary>Deletes the profile file the editor holds, then holds a fresh profile; nothing when no such file exists.</summary>
    [RelayCommand]
    private void Delete()
    {
        string id = Id;
        if (!LocalScenarioStore.IsValidId(id))
        {
            Message = $"The profile id \"{id}\" can't be used as a file name.";
            return;
        }

        bool deleted = false;
        if (!TryWrite("delete", id, () => deleted = _store.Delete(id)))
        {
            return;
        }

        if (!deleted)
        {
            Message = "This profile hasn't been saved, so there is nothing to delete.";
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Stamp profile {Id} was not deleted: no such file", id);
            }

            return;
        }

        New();
        Deleted?.Invoke(id);
    }

    // A store write that fails (a locked file, a folder it may not write) leaves the editor as it was and says why.
    private bool TryWrite(string verb, string id, Action write)
    {
        try
        {
            write();
            Message = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Stamp profile editor: could not {Verb} profile {Id}", verb, id);
            Message = $"Couldn't {verb} the profile: {ex.Message}";
            return false;
        }
    }

    private static void Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }
}
