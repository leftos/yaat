using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

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
///
/// Bindings: every value-typed dropdown binds <c>SelectedItem</c> to a
/// <see cref="TdlsClearanceValueDto"/>? property (not <c>SelectedValue</c> +
/// <c>SelectedValueBinding</c>). The latter is fragile in Avalonia.Browser —
/// the WASM ComboBox displays the matched item correctly but the two-way
/// write-back from SelectedValue to the bound string property doesn't fire,
/// leaving the VM thinking the field is unset while the dropdown shows a value.
/// </summary>
public partial class TdlsFlightPlanEditorViewModel : ObservableObject
{
    private static readonly ILogger Log = SimLog.CreateLogger("TdlsFlightPlanEditorViewModel");

    private readonly TdlsConfigDto _config;
    private readonly string? _opConfigId;
    private bool _suppressDefaults;

    public string Callsign { get; }

    /// <summary>
    /// Read-only filed flight-plan snapshot rendered above the dropdowns. Null when the aircraft has no filed plan yet
    /// (pre-filing window). Observable because an amendment can land while the controller is composing: the owner
    /// pushes the fresh DTO in through <see cref="ApplyAmendedFlightPlan"/> and the header re-renders. That is the
    /// only way in from outside — an amendment whose route changed also owes the dropdowns the route derives.
    /// </summary>
    [ObservableProperty]
    private TdlsFlightPlanInfoDto? _flightPlan;

    /// <summary>True when the editor is showing an already-sent PDC for review — every dropdown is disabled, Send is hidden, and no resend is possible. False for the normal compose-a-new-PDC flow.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Convenience inverse of <see cref="IsReadOnly"/> for binding control IsEnabled/IsVisible.</summary>
    public bool IsEditable => !IsReadOnly;

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
    [NotifyPropertyChangedFor(nameof(Expect))]
    private TdlsClearanceValueDto? _selectedExpect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Climbout))]
    private TdlsClearanceValueDto? _selectedClimbout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Climbvia))]
    private TdlsClearanceValueDto? _selectedClimbvia;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InitialAlt))]
    private TdlsClearanceValueDto? _selectedInitialAlt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContactInfo))]
    private TdlsClearanceValueDto? _selectedContactInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalInfo))]
    private TdlsClearanceValueDto? _selectedLocalInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DepFreq))]
    private TdlsClearanceValueDto? _selectedDepFreq;

    /// <summary>String facade kept for ClearanceDto round-trips and test assertions. Reads SelectedExpect.Value; the setter resolves the matching item in <see cref="Expects"/> (null if no match).</summary>
    public string? Expect
    {
        get => SelectedExpect?.Value;
        set => SelectedExpect = ResolveItem(Expects, value);
    }

    public string? Climbout
    {
        get => SelectedClimbout?.Value;
        set => SelectedClimbout = ResolveItem(Climbouts, value);
    }

    public string? Climbvia
    {
        get => SelectedClimbvia?.Value;
        set => SelectedClimbvia = ResolveItem(Climbvias, value);
    }

    public string? InitialAlt
    {
        get => SelectedInitialAlt?.Value;
        set => SelectedInitialAlt = ResolveItem(InitialAlts, value);
    }

    public string? ContactInfo
    {
        get => SelectedContactInfo?.Value;
        set => SelectedContactInfo = ResolveItem(ContactInfos, value);
    }

    public string? LocalInfo
    {
        get => SelectedLocalInfo?.Value;
        set => SelectedLocalInfo = ResolveItem(LocalInfos, value);
    }

    public string? DepFreq
    {
        get => SelectedDepFreq?.Value;
        set => SelectedDepFreq = ResolveItem(DepFreqs, value);
    }

    // NotifyCanExecuteChangedFor is load-bearing, not decoration: Avalonia's Button ANDs a
    // cached CanExecute verdict into IsEnabledCore and only refreshes it when the command
    // raises CanExecuteChanged. Without this the Send button latches disabled the moment it
    // binds to an incomplete clearance and never comes back, even though IsSendEnabled and
    // the footer status both report the clearance as valid.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendEnabled))]
    [NotifyPropertyChangedFor(nameof(MissingMandatoryFieldNames))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _canSend;

    /// <summary>True when every mandatory field is set and the editor is editable. Bound to the Send button's IsEnabled; also gates the F12 send shortcut.</summary>
    public bool IsSendEnabled => CanSend && !IsReadOnly;

    /// <summary>Human-readable list of mandatory fields that are still blank — drives the footer status string.</summary>
    public string MissingMandatoryFieldNames => string.Join(", ", EnumerateMissingMandatoryFields());

    public TdlsFlightPlanEditorViewModel(
        string callsign,
        TdlsConfigDto config,
        ClearanceDto? seed,
        TdlsFlightPlanInfoDto? flightPlan,
        bool isReadOnly,
        string? opConfigId
    )
    {
        Callsign = callsign;
        _config = config;
        _opConfigId = opConfigId;
        FlightPlan = flightPlan;
        IsReadOnly = isReadOnly;

        // Resolved, never config.Sids: a facility that enabled operational configurations ships an
        // empty facility-level SID array and keeps every SID inside the active config.
        foreach (var sid in config.ResolveSids(opConfigId))
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
            var (filedSidId, filedTransitionId) = MatchSidFromFiledRoute(config, flightPlan?.Route, opConfigId);

            _selectedSid = ResolveSid(seed?.Sid ?? filedSidId ?? config.ResolveDefaultSidId(opConfigId));
            RebuildTransitions(_selectedSid);
            _selectedTransition = ResolveTransition(
                _selectedSid,
                seed?.Transition ?? filedTransitionId ?? config.ResolveDefaultTransitionId(opConfigId)
            );

            // Field setters drop through to the matching SelectedXxxItem via
            // ResolveItem against the per-field ObservableCollection.
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

        // Phase 2 — back-fill only. Values the seed brought are a clearance already
        // composed and outrank the facility's defaults; the blank fields pick the
        // transition's FE-defined values up. Skipped when read-only: a sent-PDC
        // review must show exactly what was issued, never back-fill defaults into
        // fields that were sent blank.
        if (!isReadOnly)
        {
            BackFillTransitionDefaults(_selectedTransition);
        }

        RecomputeCanSend();
    }

    /// <summary>
    /// Pushes an amended flight plan into an open editor. The header always follows the amendment, and so do the SID
    /// and transition whenever the route itself changed — re-derived the way construction derives them, so a composed
    /// clearance can never name the SID of a route the aircraft no longer has. Assigning the selections runs the
    /// ordinary change hooks, which back-fill the new transition's defaults into the fields still blank.
    ///
    /// <para>Fields the route does not derive keep whatever the controller composed. An amended route naming no
    /// configured SID leaves every selection alone — there is nothing to re-derive from — and so does a read-only
    /// editor, where the sent PDC under review must keep showing exactly what was issued.</para>
    /// </summary>
    public void ApplyAmendedFlightPlan(TdlsFlightPlanInfoDto? plan)
    {
        var previousRoute = FlightPlan?.Route;
        FlightPlan = plan;

        if (IsReadOnly || string.Equals(previousRoute, plan?.Route, StringComparison.Ordinal))
        {
            return;
        }

        var (filedSidId, filedTransitionId) = MatchSidFromFiledRoute(_config, plan?.Route, _opConfigId);
        if (ResolveSid(filedSidId) is not { } sid)
        {
            Log.LogDebug(
                "Amended route '{Route}' for {Callsign} names no configured SID — leaving the selections as composed",
                plan?.Route,
                Callsign
            );
            return;
        }

        // Assigning the SID rebuilds Transitions and selects the first of them; the resolved transition then replaces
        // that pick whenever the route (or the facility default) names one this SID actually offers.
        SelectedSid = sid;
        if (ResolveTransition(sid, filedTransitionId ?? _config.ResolveDefaultTransitionId(_opConfigId)) is { } transition)
        {
            SelectedTransition = transition;
        }
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

    partial void OnSelectedExpectChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedClimboutChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedClimbviaChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedInitialAltChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedContactInfoChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedLocalInfoChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    partial void OnSelectedDepFreqChanged(TdlsClearanceValueDto? value) => RecomputeCanSend();

    /// <summary>Builds the current clearance state as a <see cref="ClearanceDto"/> for the canonical command builder.</summary>
    public ClearanceDto ToClearanceDto() =>
        new(
            Expect: SelectedExpect?.Value,
            Sid: SelectedSid?.Id,
            Transition: SelectedTransition?.Id,
            Climbout: SelectedClimbout?.Value,
            Climbvia: SelectedClimbvia?.Value,
            InitialAlt: SelectedInitialAlt?.Value,
            ContactInfo: SelectedContactInfo?.Value,
            LocalInfo: SelectedLocalInfo?.Value,
            DepFreq: SelectedDepFreq?.Value
        );

    [RelayCommand(CanExecute = nameof(IsSendEnabled))]
    public Task SendAsync() => OnSendRequested?.Invoke(ToClearanceDto()) ?? Task.CompletedTask;

    /// <summary>Invoked when the user presses Send (or F12). VTdlsViewModel hands the clearance off to the canonical builder.</summary>
    public Func<ClearanceDto, Task>? OnSendRequested { get; set; }

    /// <summary>
    /// Best-effort match of the filed route's leading SID token (e.g. "OAK6") against the
    /// facility config's SID names. Returns the matched SID id + optionally a transition id
    /// resolved by matching the route's second token against transition <c>FirstRoutePoint</c>
    /// values and then against transition names. Returns (null, null) when no match is found —
    /// the caller falls through to the facility's <c>DefaultSidId</c>.
    /// </summary>
    internal static (string? SidId, string? TransitionId) MatchSidFromFiledRoute(TdlsConfigDto config, string? route) =>
        MatchSidFromFiledRoute(config, route, opConfigId: null);

    internal static (string? SidId, string? TransitionId) MatchSidFromFiledRoute(TdlsConfigDto config, string? route, string? opConfigId)
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

        var sid = config.ResolveSids(opConfigId).FirstOrDefault(s => string.Equals(s.Name, sidToken, StringComparison.OrdinalIgnoreCase));
        if (sid is null)
        {
            return (null, null);
        }

        var transition = transitionHint is null ? null : MatchTransitionToHint(sid, transitionHint);

        return (sid.Id, transition?.Id);
    }

    /// <summary>
    /// Resolves the filed route's transition token against one SID's transitions. A facility config
    /// only sometimes carries <c>firstRoutePoint</c>: at KSFO the ZOA config fills it in for TRUKN2's
    /// ORRCA transition and omits it for SNTNA2's, though both transitions are named ORRCA, and a
    /// Facility Engineer may point it at the fix *after* the transition fix. So <c>FirstRoutePoint</c>
    /// is matched across every transition first — keeping the FE's explicit entry-fix mapping
    /// authoritative when one transition's name collides with another's first route point — and the
    /// transition names are matched only when that pass finds nothing.
    /// </summary>
    private static TdlsSidTransitionDto? MatchTransitionToHint(TdlsSidDto sid, string transitionHint) =>
        sid.Transitions.FirstOrDefault(t => CandidateMatchesHint(t.FirstRoutePoint, transitionHint))
        ?? sid.Transitions.FirstOrDefault(t => CandidateMatchesHint(t.Name, transitionHint));

    /// <summary>
    /// True when a transition's fix candidate — its <c>FirstRoutePoint</c> or its <c>Name</c> — is the
    /// route's transition token. The FE's "no transition" placeholder name (dashes and spaces, e.g.
    /// "- - - -") names no fix, so it never satisfies a hint.
    /// </summary>
    private static bool CandidateMatchesHint(string? candidate, string transitionHint) =>
        !IsPlaceholderName(candidate) && string.Equals(candidate, transitionHint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Detects blank values and the FE's "no value" placeholder names, which are made of dashes and spaces only.</summary>
    private static bool IsPlaceholderName(string? name) => string.IsNullOrWhiteSpace(name) || name.Replace(" ", "").Replace("-", "").Length == 0;

    private TdlsSidDto? ResolveSid(string? sidId) =>
        sidId is null ? null : _config.ResolveSids(_opConfigId).FirstOrDefault(s => string.Equals(s.Id, sidId, StringComparison.Ordinal));

    private static TdlsSidTransitionDto? ResolveTransition(TdlsSidDto? sid, string? transitionId) =>
        transitionId is null
            ? sid?.Transitions.FirstOrDefault()
            : sid?.Transitions.FirstOrDefault(t => string.Equals(t.Id, transitionId, StringComparison.Ordinal));

    /// <summary>
    /// Resolves an FE-supplied value string to the dropdown entry that offers it. This is the
    /// single funnel for both transition defaults and seed round-trips, so every field gets the
    /// same treatment.
    ///
    /// Matching cannot be exact-only: vNAS facility data routinely stores a transition default
    /// in a different numeric form than the value list it points at. KIAD defaults every
    /// transition's departure frequency to "125.05" against a list holding "125.050", and KRNO
    /// does the same with "119.2" / "119.200". With the field mandatory, an unresolved default
    /// left the dropdown blank and the clearance uncompletable.
    ///
    /// Order: exact ordinal (wins if both forms are listed), then trimmed/case-insensitive,
    /// then numeric equivalence. Non-numeric values like the "- - - -" placeholder or "3000FT"
    /// never reach the numeric pass, so nothing is matched by coincidence.
    /// </summary>
    private static TdlsClearanceValueDto? ResolveItem(IEnumerable<TdlsClearanceValueDto> items, string? wantedValue)
    {
        if (string.IsNullOrWhiteSpace(wantedValue))
        {
            return null;
        }

        var candidates = items as IReadOnlyList<TdlsClearanceValueDto> ?? items.ToList();

        var exact = candidates.FirstOrDefault(i => string.Equals(i.Value, wantedValue, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var wantedTrimmed = wantedValue.Trim();
        var loose = candidates.FirstOrDefault(i => string.Equals(i.Value?.Trim(), wantedTrimmed, StringComparison.OrdinalIgnoreCase));
        if (loose is not null)
        {
            return loose;
        }

        var numeric = decimal.TryParse(wantedTrimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var wantedNumber)
            ? candidates.FirstOrDefault(i =>
                decimal.TryParse(i.Value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var candidate) && (candidate == wantedNumber)
            )
            : null;

        if (numeric is null)
        {
            Log.LogDebug(
                "TDLS value '{Value}' has no match in [{Options}] — leaving the field unset",
                wantedValue,
                string.Join(", ", candidates.Select(i => i.Value))
            );
        }

        return numeric;
    }

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

    /// <summary>
    /// Applies a newly selected transition's defaults, overwriting what the fields hold. Upstream's contract is that
    /// "selecting a SID and transition pair also populates the remaining fields with default values defined by the
    /// Facility Engineer", so a controller who switches SID gets that SID's clearance rather than a mix of two.
    /// A field the transition defines no default for keeps its value — the FE said nothing about it — and a read-only
    /// editor is never touched, because a sent PDC under review has to keep showing what was issued.
    /// </summary>
    private void ApplyTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (IsReadOnly || (transition is null))
        {
            return;
        }
        foreach (var field in TransitionDefaultFields(transition))
        {
            if (!string.IsNullOrWhiteSpace(field.Default))
            {
                field.Assign(field.Default);
            }
        }
    }

    /// <summary>
    /// Fills only the fields that are still empty. This is construction's rule: what the seed brought is a clearance
    /// already composed (or already sent), and it outranks the facility's defaults.
    /// </summary>
    private void BackFillTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (transition is null)
        {
            return;
        }
        foreach (var field in TransitionDefaultFields(transition))
        {
            if (field.Current is null)
            {
                field.Assign(field.Default);
            }
        }
    }

    /// <summary>
    /// The seven fields a transition can carry a default for — what it defines, what the editor holds now, and the
    /// setter that resolves a value string to this field's dropdown entry through <see cref="ResolveItem"/>. The two
    /// policies above share it so the field list exists once.
    /// </summary>
    private TransitionDefaultField[] TransitionDefaultFields(TdlsSidTransitionDto transition) =>
        [
            new(transition.DefaultExpect, SelectedExpect, value => Expect = value),
            new(transition.DefaultClimbout, SelectedClimbout, value => Climbout = value),
            new(transition.DefaultClimbvia, SelectedClimbvia, value => Climbvia = value),
            new(transition.DefaultInitialAlt, SelectedInitialAlt, value => InitialAlt = value),
            new(transition.DefaultDepFreq, SelectedDepFreq, value => DepFreq = value),
            new(transition.DefaultContactInfo, SelectedContactInfo, value => ContactInfo = value),
            new(transition.DefaultLocalInfo, SelectedLocalInfo, value => LocalInfo = value),
        ];

    /// <summary>One defaultable field: the transition's value for it, the editor's current entry, and the assignment that resolves a value.</summary>
    private readonly record struct TransitionDefaultField(string? Default, TdlsClearanceValueDto? Current, Action<string?> Assign);

    private void RecomputeCanSend()
    {
        CanSend = EnumerateMissingMandatoryFields().Count == 0;

        // The *set* of missing fields changes more often than CanSend does: with two
        // mandatory fields blank, filling one leaves CanSend false, so ObservableProperty
        // raises nothing and a listener keyed on CanSend keeps naming the field the
        // controller just filled. Raise the derived list on every recompute.
        OnPropertyChanged(nameof(MissingMandatoryFieldNames));
    }

    private List<string> EnumerateMissingMandatoryFields()
    {
        var missing = new List<string>();
        if (_config.MandatorySid && SelectedSid is null)
        {
            missing.Add("SID");
        }
        if (_config.MandatoryExpect && SelectedExpect is null)
        {
            missing.Add("Expect");
        }
        if (_config.MandatoryClimbout && SelectedClimbout is null)
        {
            missing.Add("Climb out");
        }
        if (_config.MandatoryClimbvia && SelectedClimbvia is null)
        {
            missing.Add("Climb via");
        }
        if (_config.MandatoryInitialAlt && SelectedInitialAlt is null)
        {
            missing.Add("Maintain");
        }
        if (_config.MandatoryDepFreq && SelectedDepFreq is null)
        {
            missing.Add("Departure frequency");
        }
        if (_config.MandatoryContactInfo && SelectedContactInfo is null)
        {
            missing.Add("Contact info");
        }
        if (_config.MandatoryLocalInfo && SelectedLocalInfo is null)
        {
            missing.Add("Local info");
        }
        return missing;
    }
}
