using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The spawn planner's Bands mode: the band rows, each read into a <see cref="SpawnBand"/> or an error.</summary>
public sealed partial class SpawnPlannerViewModel
{
    /// <summary>Gets the band rows, in the order their slots are laid.</summary>
    public ObservableCollection<SpawnBandRowViewModel> Bands { get; } = [];

    /// <summary>
    /// Adds a band after the last one, starting one interval after its end (a band's end is inclusive, so no second is laid
    /// twice) and as long and as spaced; <see cref="SpawnPlanBand.Default"/> when there is no last band or it does not read.
    /// </summary>
    [RelayCommand]
    public void AddBand()
    {
        SpawnBandRowViewModel? last = (Bands.Count > 0) ? Bands[^1] : null;
        if (last?.Band is { } band)
        {
            int start = band.EndSec + band.IntervalSec;
            int end = start + (band.EndSec - band.StartSec);
            Bands.Add(new SpawnBandRowViewModel(Minutes(start), Minutes(end), last.IntervalText, last.PlusText, last.MinusText));
            return;
        }

        Bands.Add(SpawnBandRowViewModel.FromPlan(SpawnPlanBand.Default));
    }

    /// <summary>Removes a band row.</summary>
    /// <param name="row">The row to remove; a row not in the list is ignored.</param>
    [RelayCommand]
    public void RemoveBand(SpawnBandRowViewModel? row)
    {
        if (row is not null)
        {
            _ = Bands.Remove(row);
        }
    }

    private static string Minutes(int seconds) => (seconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture);

    private List<SpawnBand> ValidBands() => [.. Bands.Select(row => row.Band).OfType<SpawnBand>()];

    private void AddBandWarnings()
    {
        if (Mode != SpawnPlannerMode.Bands)
        {
            return;
        }

        for (int i = 0; i < Bands.Count; i++)
        {
            SpawnBandRowViewModel row = Bands[i];
            if (row.Error is { } error)
            {
                Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Band {i + 1} is skipped: {error}"));
            }
            else if (row.Warning is { } warning)
            {
                Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Band {i + 1}: {warning}"));
            }
        }
    }
}

/// <summary>
/// One band row as typed: from and to in minutes, the interval as m:ss (or bare digits, as every editor time field reads
/// them), and the jitter seconds later and earlier. A row that does not read shows <see cref="Error"/> and gives no band.
/// </summary>
public sealed partial class SpawnBandRowViewModel : ObservableObject
{
    /// <summary>The warning a band shows when its minus jitter is at least its interval.</summary>
    public const string ReorderWarningText = "Minus jitter is at least the interval: spawns in this band can change order.";

    /// <summary>The error a row shows when from or to is not minutes, zero or more.</summary>
    public const string MinutesErrorText = "From and To must be minutes, zero or more.";

    /// <summary>The error a row shows when it ends before it starts.</summary>
    public const string OrderErrorText = "The band must end at or after its start.";

    /// <summary>The error a row shows when the interval is not a time above zero.</summary>
    public const string IntervalErrorText = "The interval must be a time above zero, as m:ss.";

    /// <summary>The error a row shows when a jitter is not whole seconds, zero or more.</summary>
    public const string JitterErrorText = "Jitter must be a whole number of seconds, zero or more.";

    private static readonly HashSet<string> TextProperties =
    [
        nameof(FromText),
        nameof(ToText),
        nameof(IntervalText),
        nameof(PlusText),
        nameof(MinusText),
    ];

    [ObservableProperty]
    private string _fromText;

    [ObservableProperty]
    private string _toText;

    [ObservableProperty]
    private string _intervalText;

    [ObservableProperty]
    private string _plusText;

    [ObservableProperty]
    private string _minusText;

    [ObservableProperty]
    private SpawnBand? _band;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _warning;

    /// <summary>Initializes a new instance of the <see cref="SpawnBandRowViewModel"/> class from its texts.</summary>
    /// <param name="from">The start in minutes.</param>
    /// <param name="to">The end in minutes, inclusive.</param>
    /// <param name="interval">The interval as m:ss.</param>
    /// <param name="plus">The most seconds a slot moves later.</param>
    /// <param name="minus">The most seconds a slot moves earlier.</param>
    public SpawnBandRowViewModel(string from, string to, string interval, string plus, string minus)
    {
        _fromText = from ?? "";
        _toText = to ?? "";
        _intervalText = interval ?? "";
        _plusText = plus ?? "";
        _minusText = minus ?? "";
        Read();
    }

    /// <summary>A row with a plan file's band texts.</summary>
    /// <param name="band">The band as a plan file holds it.</param>
    /// <returns>The row.</returns>
    public static SpawnBandRowViewModel FromPlan(SpawnPlanBand band)
    {
        ArgumentNullException.ThrowIfNull(band);
        return new SpawnBandRowViewModel(band.From, band.To, band.Interval, band.Plus, band.Minus);
    }

    /// <summary>Gets the row as a plan file holds it.</summary>
    public SpawnPlanBand ToPlan() => new(FromText, ToText, IntervalText, PlusText, MinusText);

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if ((e.PropertyName is { } name) && (TextProperties.Contains(name)))
        {
            Read();
        }
    }

    private static int? Seconds(string text)
    {
        bool ok = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes);
        return ((ok) && (double.IsFinite(minutes)) && (minutes >= 0) && (minutes <= int.MaxValue / 60.0)) ? (int)Math.Round(minutes * 60) : null;
    }

    private static int? Jitter(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        return (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) ? value : null;
    }

    private void Read()
    {
        (SpawnBand? band, string? error) = Parse();
        Band = band;
        Error = error;
        Warning = ((band is not null) && (band.MinusSec >= band.IntervalSec)) ? ReorderWarningText : null;
    }

    private (SpawnBand? Band, string? Error) Parse()
    {
        if ((Seconds(FromText) is not { } start) || (Seconds(ToText) is not { } end))
        {
            return (null, MinutesErrorText);
        }

        if (end < start)
        {
            return (null, OrderErrorText);
        }

        if ((ScenarioTime.Parse(IntervalText) is not { } interval) || (interval <= 0))
        {
            return (null, IntervalErrorText);
        }

        if ((Jitter(PlusText) is not { } plus) || (Jitter(MinusText) is not { } minus))
        {
            return (null, JitterErrorText);
        }

        return (new SpawnBand(start, end, interval, plus, minus), null);
    }
}
