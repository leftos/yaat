using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// View-model for the nine-field flight-plan editor that opens on selecting a
/// DCL (Pending) item. Wraps a working <see cref="ClearanceDto"/>, exposes
/// per-field dropdown sources from the facility's <see cref="TdlsConfigDto"/>,
/// applies SID + transition defaults on selection, and gates the Send button on
/// mandatory-field completion.
///
/// Owned by <see cref="VTdlsViewModel"/>; created fresh per selected item so the
/// editor state doesn't bleed across selections.
/// </summary>
public partial class TdlsFlightPlanEditorViewModel : ObservableObject
{
    private readonly TdlsConfigDto _config;
    private bool _suppressDefaults;

    public string Callsign { get; }

    /// <summary>Read-only filed flight-plan snapshot rendered above the dropdowns. Null when the aircraft has no filed plan yet (pre-filing window).</summary>
    public TdlsFlightPlanInfoDto? FlightPlan { get; }

    /// <summary>SIDs offered by the facility. Display via <c>Name</c>; the canonical command uses <c>Id</c>.</summary>
    public ObservableCollection<TdlsSidDto> Sids { get; } = [];

    /// <summary>Transitions of <see cref="SelectedSid"/>; refreshed when the SID changes.</summary>
    public ObservableCollection<TdlsSidTransitionDto> Transitions { get; } = [];

    public ObservableCollection<TdlsClearanceValueDto> Climbouts { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> Climbvias { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> InitialAlts { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> DepFreqs { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> Expects { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> ContactInfos { get; } = [];
    public ObservableCollection<TdlsClearanceValueDto> LocalInfos { get; } = [];

    [ObservableProperty]
    private TdlsSidDto? _selectedSid;

    [ObservableProperty]
    private TdlsSidTransitionDto? _selectedTransition;

    [ObservableProperty]
    private string? _expect;

    [ObservableProperty]
    private string? _climbout;

    [ObservableProperty]
    private string? _climbvia;

    [ObservableProperty]
    private string? _initialAlt;

    [ObservableProperty]
    private string? _contactInfo;

    [ObservableProperty]
    private string? _localInfo;

    [ObservableProperty]
    private string? _depFreq;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendEnabled))]
    [NotifyPropertyChangedFor(nameof(MissingMandatoryFieldNames))]
    private bool _canSend;

    /// <summary>True when every mandatory field is set. Bound to the Send button's IsEnabled.</summary>
    public bool IsSendEnabled => CanSend;

    /// <summary>Human-readable list of mandatory fields that are still blank — drives the footer status string.</summary>
    public string MissingMandatoryFieldNames => string.Join(", ", EnumerateMissingMandatoryFields());

    public TdlsFlightPlanEditorViewModel(string callsign, TdlsConfigDto config, ClearanceDto? seed, TdlsFlightPlanInfoDto? flightPlan)
    {
        Callsign = callsign;
        _config = config;
        FlightPlan = flightPlan;

        foreach (var sid in config.Sids)
        {
            Sids.Add(sid);
        }
        foreach (var v in config.Climbouts)
        {
            Climbouts.Add(v);
        }
        foreach (var v in config.Climbvias)
        {
            Climbvias.Add(v);
        }
        foreach (var v in config.InitialAlts)
        {
            InitialAlts.Add(v);
        }
        foreach (var v in config.DepFreqs)
        {
            DepFreqs.Add(v);
        }
        foreach (var v in config.Expects)
        {
            Expects.Add(v);
        }
        foreach (var v in config.ContactInfos)
        {
            ContactInfos.Add(v);
        }
        foreach (var v in config.LocalInfos)
        {
            LocalInfos.Add(v);
        }

        // Initial SID + transition come from either the existing Pending
        // item's pre-filled clearance (if any), the filed-route's first SID
        // token (best-effort match against config.Sids by name), or the
        // facility's defaults — in that priority order.
        // Phase 1 — populate the dropdown selections WITHOUT firing the
        // default-propagation hooks; otherwise selected-changed callbacks
        // overwrite the seed before we get a chance to copy its values in.
        _suppressDefaults = true;
        try
        {
            var (filedSidId, filedTransitionId) = MatchSidFromFiledRoute(config, flightPlan?.Route);

            _selectedSid = ResolveSid(seed?.Sid ?? filedSidId ?? config.DefaultSidId);
            RebuildTransitions(_selectedSid);
            _selectedTransition = ResolveTransition(_selectedSid, seed?.Transition ?? filedTransitionId ?? config.DefaultTransitionId);

            Expect = seed?.Expect;
            Climbout = seed?.Climbout;
            Climbvia = seed?.Climbvia;
            InitialAlt = seed?.InitialAlt;
            ContactInfo = seed?.ContactInfo;
            LocalInfo = seed?.LocalInfo;
            DepFreq = seed?.DepFreq;
        }
        finally
        {
            _suppressDefaults = false;
        }

        // Phase 2 — apply transition defaults using null-coalescing assignment.
        // Pre-existing seed values are preserved; blank fields pick up the
        // transition's FE-defined defaults. Mirrors upstream behavior where
        // selecting a SID+transition pre-populates the empty editor fields.
        ApplyTransitionDefaults(_selectedTransition);

        RecomputeCanSend();
    }

    partial void OnSelectedSidChanged(TdlsSidDto? value)
    {
        RebuildTransitions(value);

        // Auto-select the first transition (or the no-op transition) when the
        // SID changes. Without this the picker shows a blank dropdown after
        // SID change and the user has to click twice.
        SelectedTransition = Transitions.FirstOrDefault();

        if (!_suppressDefaults)
        {
            ApplyTransitionDefaults(SelectedTransition);
        }

        RecomputeCanSend();
    }

    partial void OnSelectedTransitionChanged(TdlsSidTransitionDto? value)
    {
        if (!_suppressDefaults)
        {
            ApplyTransitionDefaults(value);
        }

        RecomputeCanSend();
    }

    partial void OnExpectChanged(string? value) => RecomputeCanSend();

    partial void OnClimboutChanged(string? value) => RecomputeCanSend();

    partial void OnClimbviaChanged(string? value) => RecomputeCanSend();

    partial void OnInitialAltChanged(string? value) => RecomputeCanSend();

    partial void OnContactInfoChanged(string? value) => RecomputeCanSend();

    partial void OnLocalInfoChanged(string? value) => RecomputeCanSend();

    partial void OnDepFreqChanged(string? value) => RecomputeCanSend();

    /// <summary>Builds the current clearance state as a <see cref="ClearanceDto"/> for the canonical command builder.</summary>
    public ClearanceDto ToClearanceDto() =>
        new(
            Expect: Expect,
            Sid: SelectedSid?.Id,
            Transition: SelectedTransition?.Id,
            Climbout: Climbout,
            Climbvia: Climbvia,
            InitialAlt: InitialAlt,
            ContactInfo: ContactInfo,
            LocalInfo: LocalInfo,
            DepFreq: DepFreq
        );

    [RelayCommand(CanExecute = nameof(IsSendEnabled))]
    public Task SendAsync() => OnSendRequested?.Invoke(ToClearanceDto()) ?? Task.CompletedTask;

    /// <summary>Invoked when the user presses Send (or F12). VTdlsViewModel hands the clearance off to the canonical builder.</summary>
    public Func<ClearanceDto, Task>? OnSendRequested { get; set; }

    /// <summary>
    /// Best-effort match of the filed route's leading SID token (e.g. "OAK6") against the
    /// facility config's SID names. Returns the matched SID id + optionally a transition id
    /// resolved by matching the route's second token against transition <c>FirstRoutePoint</c>
    /// values. Returns (null, null) when no match is found — the caller falls through to the
    /// facility's <c>DefaultSidId</c>.
    /// </summary>
    internal static (string? SidId, string? TransitionId) MatchSidFromFiledRoute(TdlsConfigDto config, string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return (null, null);
        }
        var tokens = route.Split([' ', '.', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return (null, null);
        }

        // Some flight plans encode SID + transition as "SID.TRANS" (e.g. "OAK6.OAK").
        // Walk the first token's '.' components alongside the route tokens so both
        // "OAK6 OAK V107 LAX" and "OAK6.OAK V107 LAX" route in the same path.
        var firstParts = tokens[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sidToken = firstParts[0];
        var transitionHint =
            firstParts.Length > 1 ? firstParts[1]
            : tokens.Length > 1 ? tokens[1]
            : null;

        var sid = config.Sids.FirstOrDefault(s => string.Equals(s.Name, sidToken, StringComparison.OrdinalIgnoreCase));
        if (sid is null)
        {
            return (null, null);
        }

        var transition = transitionHint is null
            ? null
            : sid.Transitions.FirstOrDefault(t => string.Equals(t.FirstRoutePoint, transitionHint, StringComparison.OrdinalIgnoreCase));

        return (sid.Id, transition?.Id);
    }

    private TdlsSidDto? ResolveSid(string? sidId) =>
        sidId is null ? null : _config.Sids.FirstOrDefault(s => string.Equals(s.Id, sidId, StringComparison.Ordinal));

    private static TdlsSidTransitionDto? ResolveTransition(TdlsSidDto? sid, string? transitionId) =>
        transitionId is null
            ? sid?.Transitions.FirstOrDefault()
            : sid?.Transitions.FirstOrDefault(t => string.Equals(t.Id, transitionId, StringComparison.Ordinal));

    private void RebuildTransitions(TdlsSidDto? sid)
    {
        Transitions.Clear();
        if (sid is null)
        {
            return;
        }
        foreach (var t in sid.Transitions)
        {
            Transitions.Add(t);
        }
    }

    private void ApplyTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (transition is null)
        {
            return;
        }
        // Only overwrite when the destination is currently empty — the
        // controller may already have hand-edited a value, and a transition
        // change shouldn't clobber that.
        Expect ??= transition.DefaultExpect;
        Climbout ??= transition.DefaultClimbout;
        Climbvia ??= transition.DefaultClimbvia;
        InitialAlt ??= transition.DefaultInitialAlt;
        DepFreq ??= transition.DefaultDepFreq;
        ContactInfo ??= transition.DefaultContactInfo;
        LocalInfo ??= transition.DefaultLocalInfo;
    }

    private void RecomputeCanSend() => CanSend = EnumerateMissingMandatoryFields().Count == 0;

    private List<string> EnumerateMissingMandatoryFields()
    {
        var missing = new List<string>();
        if (_config.MandatorySid && SelectedSid is null)
        {
            missing.Add("SID");
        }
        if (_config.MandatoryExpect && string.IsNullOrWhiteSpace(Expect))
        {
            missing.Add("Expect");
        }
        if (_config.MandatoryClimbout && string.IsNullOrWhiteSpace(Climbout))
        {
            missing.Add("Climb out");
        }
        if (_config.MandatoryClimbvia && string.IsNullOrWhiteSpace(Climbvia))
        {
            missing.Add("Climb via");
        }
        if (_config.MandatoryInitialAlt && string.IsNullOrWhiteSpace(InitialAlt))
        {
            missing.Add("Maintain");
        }
        if (_config.MandatoryDepFreq && string.IsNullOrWhiteSpace(DepFreq))
        {
            missing.Add("Departure frequency");
        }
        if (_config.MandatoryContactInfo && string.IsNullOrWhiteSpace(ContactInfo))
        {
            missing.Add("Contact info");
        }
        if (_config.MandatoryLocalInfo && string.IsNullOrWhiteSpace(LocalInfo))
        {
            missing.Add("Local info");
        }
        return missing;
    }
}
