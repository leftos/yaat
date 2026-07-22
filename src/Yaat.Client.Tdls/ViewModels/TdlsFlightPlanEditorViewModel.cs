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
    private bool _suppressDefaults;

    public string Callsign { get; }

    /// <summary>Read-only filed flight-plan snapshot rendered above the dropdowns. Null when the aircraft has no filed plan yet (pre-filing window).</summary>
    public TdlsFlightPlanInfoDto? FlightPlan { get; }

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
        bool isReadOnly
    )
    {
        Callsign = callsign;
        _config = config;
        FlightPlan = flightPlan;
        IsReadOnly = isReadOnly;

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

        // Phase 2 — apply transition defaults using null-coalescing assignment.
        // Pre-existing seed values are preserved; blank fields pick up the
        // transition's FE-defined defaults. Mirrors upstream behavior where
        // selecting a SID+transition pre-populates the empty editor fields.
        // Skipped when read-only: a sent-PDC review must show exactly what was
        // issued, never back-fill defaults into fields that were sent blank.
        if (!isReadOnly)
        {
            ApplyTransitionDefaults(_selectedTransition);
        }

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

    private void ApplyTransitionDefaults(TdlsSidTransitionDto? transition)
    {
        if (transition is null)
        {
            return;
        }
        // Only overwrite when the destination is currently empty — the
        // controller may already have hand-edited a value, and a transition
        // change shouldn't clobber that. Setters resolve string -> SelectedItem.
        if (SelectedExpect is null)
        {
            Expect = transition.DefaultExpect;
        }
        if (SelectedClimbout is null)
        {
            Climbout = transition.DefaultClimbout;
        }
        if (SelectedClimbvia is null)
        {
            Climbvia = transition.DefaultClimbvia;
        }
        if (SelectedInitialAlt is null)
        {
            InitialAlt = transition.DefaultInitialAlt;
        }
        if (SelectedDepFreq is null)
        {
            DepFreq = transition.DefaultDepFreq;
        }
        if (SelectedContactInfo is null)
        {
            ContactInfo = transition.DefaultContactInfo;
        }
        if (SelectedLocalInfo is null)
        {
            LocalInfo = transition.DefaultLocalInfo;
        }
    }

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
