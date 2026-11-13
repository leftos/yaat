using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Sim;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Client.Tdls.ViewModels;

/// <summary>
/// View-model for the nine-field flight-plan editor that opens on selecting a
/// DCL (Pending) item. Wraps a working <see cref="ClearanceDto"/>, exposes
/// per-field dropdown sources from the facility's <see cref="TdlsConfigDto"/>,
/// applies SID + transition defaults on selection, and gates the Send button on
/// mandatory-field completion.
///
/// Every dropdown of an editable editor offers a clear entry ("- - - -") first,
/// which empties the field when picked; on the SID it empties the transition too,
/// and neither touches the other fields. The transition list offers it only when
/// there is nothing else to pick: no SID, or a SID with no transitions.
///
/// Climb via and Maintain are alternatives, not companions (7110.65 §4-3-2): a
/// climb-via clearance is itself the altitude instruction, so picking one clears
/// the Maintain field and picking a Maintain clears the climb-via. Neither field is
/// disabled, and a climb-via satisfies a Maintain the facility makes mandatory.
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

    /// <summary>
    /// Reserved id of the editor's own "clear this field" entries. No Facility Engineer id takes this form, so the
    /// editor's entries never collide with an FE row — including the FE's own "- - - -" placeholder rows.
    /// </summary>
    public const string ClearEntryId = "__yaat_clear__";

    private const string ClearEntryText = "- - - -";

    /// <summary>
    /// The clear entry offered first in every value dropdown of an editable editor. Picking it empties the field: the
    /// selection hooks turn it into null at once, so it never reaches the clearance, the matcher or the mandatory check.
    /// </summary>
    public static TdlsClearanceValueDto ClearValue { get; } = new(ClearEntryId, ClearEntryText);

    /// <summary>The clear entry offered first in the SID dropdown; picking it empties the SID and the transition.</summary>
    public static TdlsSidDto ClearSid { get; } = new(ClearEntryId, ClearEntryText, []);

    /// <summary>The clear entry the transition dropdown offers when the selected SID (or no SID) has no transitions.</summary>
    public static TdlsSidTransitionDto ClearTransition { get; } =
        new(
            ClearEntryId,
            ClearEntryText,
            FirstRoutePoint: null,
            DefaultExpect: null,
            DefaultClimbout: null,
            DefaultClimbvia: null,
            DefaultInitialAlt: null,
            DefaultDepFreq: null,
            DefaultContactInfo: null,
            DefaultLocalInfo: null
        );

    private readonly TdlsConfigDto _config;
    private readonly string? _opConfigId;
    private readonly bool _suppressDefaults;

    /// <summary>The view's Override Dep Freq; null when none is chosen or the editor is read-only.</summary>
    private string? _overrideDepFreq;

    /// <summary>
    /// The Override Dep Freq this editor applies on open and after every SID or transition pick; null when none was
    /// chosen, the editor is read-only, or the view dropped it through <see cref="DropOverrideDepFreq"/>.
    /// </summary>
    public string? OverrideDepFreq => _overrideDepFreq;

    /// <summary>
    /// Stops applying the override: the view's Temp. Dep Freqs no longer holds it, so a later SID or transition pick
    /// takes the transition's default departure frequency again. The current Dep Freq is left as it is.
    /// </summary>
    public void DropOverrideDepFreq() => _overrideDepFreq = null;

    /// <summary>False until the constructor has copied the seed and back-filled the defaults; the field exclusions
    /// that answer a controller's pick stay off until then.</summary>
    private readonly bool _constructed;

    public string Callsign { get; }

    /// <summary>
    /// Read-only filed flight-plan snapshot rendered above the dropdowns. Null when the aircraft has no filed plan yet
    /// (pre-filing window). Observable because an amendment can land while the controller is composing: the owner
    /// pushes the fresh DTO in through <see cref="ApplyAmendedFlightPlan"/> and the header re-renders. That is the
    /// only way in from outside — an amendment whose route changed also owes the dropdowns the route derives.
    /// </summary>
    [ObservableProperty]
    private TdlsFlightPlanInfoDto? _flightPlan;

    /// <summary>
    /// True when the editor is showing an already-sent PDC for review — every dropdown is disabled, Send is hidden, and no
    /// resend is possible. False for the normal compose-a-new-PDC flow.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>Convenience inverse of <see cref="IsReadOnly"/> for binding control IsEnabled/IsVisible.</summary>
    public bool IsEditable => !IsReadOnly;

    /// <summary>
    /// True when a climb-via instruction is actually in force: an entry is selected and it carries an instruction
    /// rather than the FE's "- - - -" placeholder, which is selectable but says nothing.
    /// </summary>
    private bool HasClimbVia => (SelectedClimbvia is not null) && !TdlsPlaceholder.IsPlaceholder(SelectedClimbvia.Value);

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

    /// <summary>
    /// String facade kept for ClearanceDto round-trips and test assertions. Reads SelectedExpect.Value; the setter resolves
    /// the matching item in <see cref="Expects"/> (null if no match).
    /// </summary>
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

    /// <summary>
    /// True when every mandatory field is set and the editor is editable. Bound to the Send button's IsEnabled; also gates
    /// the F12 send shortcut.
    /// </summary>
    public bool IsSendEnabled => CanSend && !IsReadOnly;

    /// <summary>Human-readable list of mandatory fields that are still blank — drives the footer status string.</summary>
    public string MissingMandatoryFieldNames => string.Join(", ", EnumerateMissingMandatoryFields());

    public TdlsFlightPlanEditorViewModel(
        string callsign,
        TdlsConfigDto config,
        ClearanceDto? seed,
        TdlsFlightPlanInfoDto? flightPlan,
        bool isReadOnly,
        string? opConfigId,
        TdlsDepFreqChoices? depFreqChoices
    )
    {
        Callsign = callsign;
        _config = config;
        _opConfigId = opConfigId;
        // A sent PDC under review shows what was issued: the view's temporary frequencies and override stay out of it.
        _overrideDepFreq = isReadOnly ? null : depFreqChoices?.OverrideDepFreq;
        FlightPlan = flightPlan;
        IsReadOnly = isReadOnly;

        // A sent PDC under review offers only the SID it was sent with, by the names stored at send time:
        // its ids may name nothing in the ops config now in force.
        TdlsSidDto? sentSid = isReadOnly ? SentSidFromSeed(seed) : null;

        // Resolved, never config.Sids: a facility that enabled operational configurations ships an
        // empty facility-level SID array and keeps every SID inside the active config.
        // An editable editor offers its clear entries first in every list; a sent PDC under review offers none.
        CopyInto(Sids, OfferClearEntry(ClearSid, sentSid is null ? config.ResolveSids(opConfigId) : [sentSid]));
        CopyInto(Climbouts, OfferClearEntry(ClearValue, config.Climbouts));
        CopyInto(Climbvias, OfferClearEntry(ClearValue, config.Climbvias));
        CopyInto(InitialAlts, OfferClearEntry(ClearValue, config.InitialAlts));
        CopyInto(DepFreqs, OfferClearEntry(ClearValue, OfferedDepFreqs(config, isReadOnly ? null : depFreqChoices)));
        CopyInto(Expects, OfferClearEntry(ClearValue, config.Expects));
        CopyInto(ContactInfos, OfferClearEntry(ClearValue, config.ContactInfos));
        CopyInto(LocalInfos, OfferClearEntry(ClearValue, config.LocalInfos));

        // Phase 1 — populate the dropdown selections WITHOUT firing the
        // default-propagation hooks; otherwise selected-changed callbacks
        // overwrite the seed before we get a chance to copy its values in.
        _suppressDefaults = true;
        try
        {
            (_selectedSid, _selectedTransition) = InitialSidSelection(seed, sentSid, flightPlan?.Route);
            RebuildTransitions(_selectedSid);
            ApplySeedValues(seed);
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
            ApplyOverrideDepFreq();
        }

        _constructed = true;
        RecomputeCanSend();
    }

    /// <summary>
    /// Pushes an amended flight plan into an open editor. The header always follows the amendment, and so do the SID
    /// and transition whenever the route itself changed — re-derived the way construction derives them, so a composed
    /// clearance can never name the SID of a route the aircraft no longer has. Assigning the selections runs the
    /// ordinary change hooks, so a re-seeded SID or transition sets all seven fields from the new transition, as a
    /// controller's own pick does: a field it defines no default for comes out empty.
    ///
    /// <para>An amended route from which no configured SID can be derived leaves every selection alone — there is
    /// nothing to re-derive from — and so does a read-only editor, where the sent PDC under review must keep showing
    /// exactly what was issued.</para>
    /// </summary>
    public void ApplyAmendedFlightPlan(TdlsFlightPlanInfoDto? plan)
    {
        string? previousRoute = FlightPlan?.Route;
        FlightPlan = plan;

        if (IsReadOnly || string.Equals(previousRoute, plan?.Route, StringComparison.Ordinal))
        {
            return;
        }

        (string? filedSidId, string? filedTransitionId) = MatchSidFromFiledRoute(_config, plan?.Route, _opConfigId);
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
        // The clear entry stands for "no SID": swap it for null at once. The null assignment re-enters this hook,
        // which empties the transition along with it.
        if (ReferenceEquals(value, ClearSid))
        {
            SelectedSid = null;
            return;
        }

        RebuildTransitions(value);

        // Auto-select the first transition (or the no-op transition) when the
        // SID changes. Without this the picker shows a blank dropdown after
        // SID change and the user has to click twice. A SID with no transitions
        // leaves the transition empty, never on the clear entry.
        SelectedTransition = Transitions.FirstOrDefault(t => !ReferenceEquals(t, ClearTransition));

        if (!_suppressDefaults)
        {
            ApplyTransitionDefaults(SelectedTransition);
        }

        RecomputeCanSend();
    }

    partial void OnSelectedTransitionChanged(TdlsSidTransitionDto? value)
    {
        if (ReferenceEquals(value, ClearTransition))
        {
            SelectedTransition = null;
            return;
        }

        if (!_suppressDefaults)
        {
            ApplyTransitionDefaults(value);
        }

        RecomputeCanSend();
    }

    partial void OnSelectedExpectChanged(TdlsClearanceValueDto? value)
    {
        if (!ClearedInstead(value, () => SelectedExpect = null))
        {
            RecomputeCanSend();
        }
    }

    partial void OnSelectedClimboutChanged(TdlsClearanceValueDto? value)
    {
        if (!ClearedInstead(value, () => SelectedClimbout = null))
        {
            RecomputeCanSend();
        }
    }

    /// <summary>
    /// A climb-via coming into force drops whatever Maintain held; clearing it back to nothing leaves Maintain as it is,
    /// so an altitude dropped here is never restored — it belonged to a clearance that no longer exists. A read-only editor clears
    /// nothing, the same carve-out the default propagation has: a sent PDC under review shows exactly what was issued.
    /// </summary>
    partial void OnSelectedClimbviaChanged(TdlsClearanceValueDto? value)
    {
        if (ClearedInstead(value, () => SelectedClimbvia = null))
        {
            return;
        }

        if (HasClimbVia && !IsReadOnly)
        {
            SelectedInitialAlt = null;
        }

        RecomputeCanSend();
    }

    /// <summary>
    /// The other half of the exclusion: a real Maintain picked over a climb-via drops the climb-via. Only an altitude
    /// clears it — an emptied field or the FE's "- - - -" row instructs nothing — so the two hooks never feed each
    /// other. Construction and a read-only editor clear nothing: they show a clearance as it was composed or issued.
    /// </summary>
    partial void OnSelectedInitialAltChanged(TdlsClearanceValueDto? value)
    {
        if (ClearedInstead(value, () => SelectedInitialAlt = null))
        {
            return;
        }

        if (_constructed && !IsReadOnly && (value is not null) && !TdlsPlaceholder.IsPlaceholder(value.Value))
        {
            SelectedClimbvia = null;
        }

        RecomputeCanSend();
    }

    partial void OnSelectedContactInfoChanged(TdlsClearanceValueDto? value)
    {
        if (!ClearedInstead(value, () => SelectedContactInfo = null))
        {
            RecomputeCanSend();
        }
    }

    partial void OnSelectedLocalInfoChanged(TdlsClearanceValueDto? value)
    {
        if (!ClearedInstead(value, () => SelectedLocalInfo = null))
        {
            RecomputeCanSend();
        }
    }

    partial void OnSelectedDepFreqChanged(TdlsClearanceValueDto? value)
    {
        if (!ClearedInstead(value, () => SelectedDepFreq = null))
        {
            RecomputeCanSend();
        }
    }

    /// <summary>
    /// Swaps the clear entry for null the moment a value dropdown takes it, through <paramref name="clear"/>, and says
    /// so. The null assignment re-enters the field's hook, which does that hook's ordinary work for an empty field, so
    /// the caller stops here.
    /// </summary>
    private static bool ClearedInstead(TdlsClearanceValueDto? value, Action clear)
    {
        if (!ReferenceEquals(value, ClearValue))
        {
            return false;
        }
        clear();
        return true;
    }

    /// <summary>The list as an editable editor offers it, clear entry first; a read-only editor offers the list alone.</summary>
    private IEnumerable<T> OfferClearEntry<T>(T clearEntry, IEnumerable<T> items) => IsReadOnly ? items : items.Prepend(clearEntry);

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
    /// values and then against transition names, and failing that against the first route
    /// points of the later tokens. A route naming no SID selects the SID and transition whose
    /// first route point it passes through. Returns (null, null) when no match is found —
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
        string[] tokens = route.Split([' ', '.', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return (null, null);
        }

        // Some flight plans encode SID + transition as "SID.TRANS" (e.g. "OAK6.OAK"). The tokenizer splits on '.', so
        // "OAK6 OAK V107 LAX" and "OAK6.OAK V107 LAX" both yield the SID first and the transition hint second.
        string sidToken = tokens[0];
        string? transitionHint = tokens.Length > 1 ? tokens[1] : null;

        IReadOnlyList<TdlsSidDto> sids = config.ResolveSids(opConfigId);
        TdlsSidDto? sid = sids.FirstOrDefault(s => string.Equals(s.Name, sidToken, StringComparison.OrdinalIgnoreCase));
        if (sid is null)
        {
            return FindSidByFirstRoutePoint(sids, tokens);
        }

        return (sid.Id, MatchTransitionOfNamedSid(sid, transitionHint, tokens)?.Id);
    }

    /// <summary>
    /// The transition of a SID the route names: the token after the SID names it when it can; otherwise any later
    /// route fix that starts one of this SID's transitions does.
    /// </summary>
    private static TdlsSidTransitionDto? MatchTransitionOfNamedSid(TdlsSidDto sid, string? transitionHint, string[] tokens) =>
        (transitionHint is null ? null : MatchTransitionToHint(sid, transitionHint)) ?? FindTransitionByFirstRoutePoint(sid, tokens.Skip(1));

    /// <summary>
    /// The route names no SID: upstream then selects the SID and transition whose first route point the route passes
    /// through. Every token is tried in route order — past the first airway too — and the first that starts a
    /// transition wins; a fix starting transitions of several SIDs goes to the first of them in config order.
    /// </summary>
    private static (string? SidId, string? TransitionId) FindSidByFirstRoutePoint(IReadOnlyList<TdlsSidDto> sids, string[] tokens)
    {
        foreach (string token in tokens)
        {
            foreach (TdlsSidDto sid in sids)
            {
                if (FindTransitionByFirstRoutePoint(sid, [token]) is { } transition)
                {
                    return (sid.Id, transition.Id);
                }
            }
        }
        return (null, null);
    }

    /// <summary>
    /// The first of <paramref name="sid"/>'s transitions whose first route point is one of <paramref name="tokens"/>,
    /// trying the tokens in route order. A placeholder first route point names no fix and never matches.
    /// </summary>
    private static TdlsSidTransitionDto? FindTransitionByFirstRoutePoint(TdlsSidDto sid, IEnumerable<string> tokens) =>
        tokens
            .Select(token => sid.Transitions.FirstOrDefault(t => CandidateMatchesHint(t.FirstRoutePoint, token)))
            .FirstOrDefault(transition => transition is not null);

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
        !TdlsPlaceholder.IsPlaceholder(candidate) && string.Equals(candidate, transitionHint, StringComparison.OrdinalIgnoreCase);

    private static void CopyInto<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        foreach (T item in source)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// The SID and transition the editor opens on, in priority order: a read-only editor's SID built from the stored
    /// names (<paramref name="sentSid"/>), then the seed clearance's ids (an existing item's pre-filled or sent
    /// clearance), then the filed route's first SID token (best-effort match by name), then the facility's defaults.
    /// </summary>
    private (TdlsSidDto? Sid, TdlsSidTransitionDto? Transition) InitialSidSelection(ClearanceDto? seed, TdlsSidDto? sentSid, string? route)
    {
        if (sentSid is not null)
        {
            return (sentSid, sentSid.Transitions.FirstOrDefault());
        }
        (string? filedSidId, string? filedTransitionId) = MatchSidFromFiledRoute(_config, route, _opConfigId);
        TdlsSidDto? sid = ResolveSid(seed?.Sid ?? filedSidId ?? _config.ResolveDefaultSidId(_opConfigId));
        string? transitionId = seed?.Transition ?? filedTransitionId ?? _config.ResolveDefaultTransitionId(_opConfigId);
        return (sid, ResolveTransition(sid, transitionId));
    }

    /// <summary>
    /// Copies the seed's free-value fields in. Each setter drops through to the matching SelectedXxxItem via ResolveItem
    /// against the per-field collection.
    /// </summary>
    private void ApplySeedValues(ClearanceDto? seed)
    {
        Expect = seed?.Expect;
        Climbout = seed?.Climbout;
        Climbvia = seed?.Climbvia;
        InitialAlt = seed?.InitialAlt;
        ContactInfo = seed?.ContactInfo;
        LocalInfo = seed?.LocalInfo;
        DepFreq = seed?.DepFreq;
    }

    /// <summary>
    /// The SID a sent payload was issued with, built from the names the server resolved at send time: one SID carrying
    /// one transition, or none when the payload names no transition. Null when the payload carries no SID name (one
    /// stored before names were kept), which leaves the ids to resolve against the given ops config.
    /// </summary>
    private static TdlsSidDto? SentSidFromSeed(ClearanceDto? seed)
    {
        if (seed?.SidName is not { } sidName)
        {
            return null;
        }
        TdlsSidTransitionDto[] transitions = seed.TransitionName is { } transitionName
            ?
            [
                new TdlsSidTransitionDto(
                    Id: seed.Transition ?? "",
                    Name: transitionName,
                    FirstRoutePoint: null,
                    DefaultExpect: null,
                    DefaultClimbout: null,
                    DefaultClimbvia: null,
                    DefaultInitialAlt: null,
                    DefaultDepFreq: null,
                    DefaultContactInfo: null,
                    DefaultLocalInfo: null
                ),
            ]
            : [];
        return new TdlsSidDto(seed.Sid ?? "", sidName, transitions);
    }

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
    /// never reach the numeric pass, so nothing is matched by coincidence. The editor's own clear
    /// entry is never a candidate: a "- - - -" value resolves to the FE's row, or to nothing.
    /// </summary>
    private static TdlsClearanceValueDto? ResolveItem(IEnumerable<TdlsClearanceValueDto> items, string? wantedValue)
    {
        if (string.IsNullOrWhiteSpace(wantedValue))
        {
            return null;
        }

        TdlsClearanceValueDto[] candidates = [.. items.Where(i => !ReferenceEquals(i, ClearValue))];

        TdlsClearanceValueDto? exact = candidates.FirstOrDefault(i => string.Equals(i.Value, wantedValue, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        string wantedTrimmed = wantedValue.Trim();
        TdlsClearanceValueDto? loose = candidates.FirstOrDefault(i =>
            string.Equals(i.Value?.Trim(), wantedTrimmed, StringComparison.OrdinalIgnoreCase)
        );
        if (loose is not null)
        {
            return loose;
        }

        TdlsClearanceValueDto? numeric = decimal.TryParse(wantedTrimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal wantedNumber)
            ? candidates.FirstOrDefault(i =>
                decimal.TryParse(i.Value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal candidate)
                && (candidate == wantedNumber)
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

    /// <summary>
    /// Refills the transition list for <paramref name="sid"/>. As upstream does, the clear entry is offered only when
    /// there is nothing else to pick — no SID, or a SID with no transitions — and never in a read-only editor.
    /// </summary>
    private void RebuildTransitions(TdlsSidDto? sid)
    {
        Transitions.Clear();
        TdlsSidTransitionDto[] transitions = sid?.Transitions ?? [];
        if ((transitions.Length == 0) && !IsReadOnly)
        {
            Transitions.Add(ClearTransition);
            return;
        }
        foreach (TdlsSidTransitionDto t in transitions)
        {
            Transitions.Add(t);
        }
    }

    /// <summary>
    /// Sets all seven fields from a newly selected transition, as upstream does: "selecting a SID and transition pair
    /// also populates the remaining fields with default values defined by the Facility Engineer", so a controller who
    /// switches SID gets that SID's clearance rather than a mix of two. A field the transition defines no default for
    /// comes out empty. Only a real transition sets anything: no transition — the SID or the transition was cleared, the
    /// SID has none, or the view emptied the selection while rebuilding the list — leaves all seven as they are, as
    /// upstream's "- - - -" does. A field another field has ruled out is left alone, and a read-only editor is never
    /// touched, because a sent PDC under review has to keep showing what was issued.
    /// </summary>
    private void ApplyTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (IsReadOnly || (transition is null))
        {
            return;
        }
        foreach (TransitionDefaultField field in TransitionDefaultFields(transition))
        {
            if (!field.Suppressed())
            {
                // A blank default resolves to no entry, which empties the field.
                field.Assign(field.Default);
            }
        }
        ApplyOverrideDepFreq();
    }

    /// <summary>
    /// The Facility Menu's Override Dep Freq replaces the transition's default departure frequency, on open and after
    /// every SID or transition pick, as upstream's <c>populateFromSid</c> ends with <c>n&amp;&amp;(this.depFreq=n)</c>.
    /// </summary>
    private void ApplyOverrideDepFreq()
    {
        if (_overrideDepFreq is not null)
        {
            DepFreq = _overrideDepFreq;
        }
    }

    /// <summary>
    /// The departure frequencies the dropdown offers: the facility's, then the view's temporary ones and its override
    /// where the facility does not list that value already (ordinal), so the override always has an entry to select.
    /// </summary>
    private static IEnumerable<TdlsClearanceValueDto> OfferedDepFreqs(TdlsConfigDto config, TdlsDepFreqChoices? choices)
    {
        if (choices is null)
        {
            return config.DepFreqs;
        }
        List<TdlsClearanceValueDto> offered = [.. config.DepFreqs];
        IEnumerable<string> extra = choices.OverrideDepFreq is { } chosen ? choices.TempDepFreqs.Append(chosen) : choices.TempDepFreqs;
        foreach (string value in extra)
        {
            if (!offered.Exists(entry => string.Equals(entry.Value, value, StringComparison.Ordinal)))
            {
                offered.Add(new TdlsClearanceValueDto(value, value));
            }
        }
        return offered;
    }

    /// <summary>
    /// Fills only the fields that are still empty. This is construction's rule: what the seed brought is a clearance
    /// already composed (or already sent), and it outranks the facility's defaults. A field ruled out by another one
    /// is skipped here too.
    /// </summary>
    private void BackFillTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (transition is null)
        {
            return;
        }
        foreach (TransitionDefaultField field in TransitionDefaultFields(transition))
        {
            if ((field.Current is null) && !field.Suppressed())
            {
                field.Assign(field.Default);
            }
        }
    }

    /// <summary>
    /// The seven fields a transition can carry a default for — what it defines, what the editor holds now, the setter
    /// that resolves a value string to this field's dropdown entry through <see cref="ResolveItem"/>, and whether
    /// another field has ruled it out. The two policies above share it so the field list exists once.
    ///
    /// <para>The suppression predicate reads live editor state on each pass rather than a captured verdict: Climbvia is
    /// assigned earlier in the same loop, and ZOA's OAK NUEVO8 defines a climb-via and an initial altitude on the one
    /// transition. A bool captured when this array was built would still say "no climb-via" and fill both.</para>
    /// </summary>
    private TransitionDefaultField[] TransitionDefaultFields(TdlsSidTransitionDto transition) =>
        [
            new(transition.DefaultExpect, SelectedExpect, value => Expect = value, NeverSuppressed),
            new(transition.DefaultClimbout, SelectedClimbout, value => Climbout = value, NeverSuppressed),
            new(transition.DefaultClimbvia, SelectedClimbvia, value => Climbvia = value, NeverSuppressed),
            new(transition.DefaultInitialAlt, SelectedInitialAlt, value => InitialAlt = value, () => HasClimbVia),
            new(transition.DefaultDepFreq, SelectedDepFreq, value => DepFreq = value, NeverSuppressed),
            new(transition.DefaultContactInfo, SelectedContactInfo, value => ContactInfo = value, NeverSuppressed),
            new(transition.DefaultLocalInfo, SelectedLocalInfo, value => LocalInfo = value, NeverSuppressed),
        ];

    /// <summary>Nothing in a clearance can rule these fields out, so their defaults always apply.</summary>
    private static readonly Func<bool> NeverSuppressed = () => false;

    /// <summary>
    /// One defaultable field: the transition's value for it, the editor's current entry, the assignment that resolves a
    /// value, and a live test of whether another field has ruled this one out.
    /// </summary>
    private readonly record struct TransitionDefaultField(
        string? Default,
        TdlsClearanceValueDto? Current,
        Action<string?> Assign,
        Func<bool> Suppressed
    );

    private void RecomputeCanSend()
    {
        CanSend = EnumerateMissingMandatoryFields().Count == 0;

        // The *set* of missing fields changes more often than CanSend does: with two
        // mandatory fields blank, filling one leaves CanSend false, so ObservableProperty
        // raises nothing and a listener keyed on CanSend keeps naming the field the
        // controller just filled. Raise the derived list on every recompute.
        OnPropertyChanged(nameof(MissingMandatoryFieldNames));
    }

    /// <summary>
    /// The mandatory fields still waiting on a value. A field holding the FE's "- - - -" entry counts as waiting: the
    /// placeholder is selectable but instructs nothing, so unlocking Send on it would issue a PDC reading "MAINT
    /// - - - -". Maintain is not checked at all while a climb-via is in force — the climb-via IS the altitude
    /// instruction (7110.65 §4-3-2), and picking a Maintain would drop it.
    /// </summary>
    private List<string> EnumerateMissingMandatoryFields()
    {
        var missing = new List<string>();
        if (_config.MandatorySid && ((SelectedSid is null) || TdlsPlaceholder.IsPlaceholder(SelectedSid.Name)))
        {
            missing.Add("SID");
        }
        if (_config.MandatoryExpect && IsUnset(SelectedExpect))
        {
            missing.Add("Expect");
        }
        if (_config.MandatoryClimbout && IsUnset(SelectedClimbout))
        {
            missing.Add("Climb out");
        }
        if (_config.MandatoryClimbvia && IsUnset(SelectedClimbvia))
        {
            missing.Add("Climb via");
        }
        if (_config.MandatoryInitialAlt && !HasClimbVia && IsUnset(SelectedInitialAlt))
        {
            missing.Add("Maintain");
        }
        if (_config.MandatoryDepFreq && IsUnset(SelectedDepFreq))
        {
            missing.Add("Departure frequency");
        }
        if (_config.MandatoryContactInfo && IsUnset(SelectedContactInfo))
        {
            missing.Add("Contact info");
        }
        if (_config.MandatoryLocalInfo && IsUnset(SelectedLocalInfo))
        {
            missing.Add("Local info");
        }
        return missing;
    }

    /// <summary>True when a dropdown carries no value: nothing selected, or the FE's "no value" placeholder entry.</summary>
    private static bool IsUnset(TdlsClearanceValueDto? selection) => (selection is null) || TdlsPlaceholder.IsPlaceholder(selection.Value);
}

/// <summary>
/// The vTDLS view's own departure-frequency choices from the Facility Menu, handed to each editor it opens: the
/// temporary frequencies the Dep Freq dropdown offers after the facility's, and the override that replaces the
/// transition's default departure frequency. Both belong to the view and are never saved, as upstream keeps them in
/// sessionStorage.
/// </summary>
public sealed record TdlsDepFreqChoices(IReadOnlyList<string> TempDepFreqs, string? OverrideDepFreq);
