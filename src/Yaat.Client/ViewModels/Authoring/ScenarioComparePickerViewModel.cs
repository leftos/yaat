using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Where one side of a Compare comes from.</summary>
public enum ScenarioCompareSideKind
{
    /// <summary>A working copy of the ARTCC.</summary>
    WorkingCopy,

    /// <summary>A server scenario of the ARTCC, as its mirror holds it.</summary>
    Server,

    /// <summary>A scenario file, asked for once Compare is pressed.</summary>
    File,
}

/// <summary>One scenario the Compare picker offers on a side.</summary>
/// <param name="Kind">A working copy, a server scenario or a file.</param>
/// <param name="Id">The scenario's id; empty for the file entry.</param>
/// <param name="Name">The scenario's name; "File…" for the file entry.</param>
/// <param name="OriginId">A working copy's server scenario; null for a local-only copy and for the other kinds.</param>
public sealed record ScenarioCompareSide(ScenarioCompareSideKind Kind, string Id, string Name, string? OriginId)
{
    /// <summary>The file entry on the Theirs side.</summary>
    public static readonly ScenarioCompareSide FileEntry = new(ScenarioCompareSideKind.File, "", "File…", null);

    /// <summary>Gets what the picker's list shows for the entry.</summary>
    public string Label =>
        Kind switch
        {
            ScenarioCompareSideKind.WorkingCopy => $"{Name} (working copy)",
            ScenarioCompareSideKind.Server => $"{Name} (server)",
            _ => Name,
        };

    /// <summary>Whether the other entry is the same scenario: the same kind and id; the file entry never is.</summary>
    /// <param name="other">The other side's entry.</param>
    /// <returns>True when both sides name one scenario.</returns>
    public bool IsSameScenario(ScenarioCompareSide other) =>
        (other is not null)
        && (Kind != ScenarioCompareSideKind.File)
        && (other.Kind == Kind)
        && string.Equals(other.Id, Id, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>The two scenarios the Compare picker returns.</summary>
/// <param name="Mine">The working copy Apply writes.</param>
/// <param name="Theirs">The scenario it is compared with.</param>
public sealed record ScenarioComparePick(ScenarioCompareSide Mine, ScenarioCompareSide Theirs)
{
    /// <summary>Gets whether theirs is mine's own server scenario, so the merge is three-way over mine's stored base.</summary>
    public bool SharesOrigin =>
        (Theirs.Kind == ScenarioCompareSideKind.Server)
        && (Mine.OriginId is { } origin)
        && string.Equals(origin, Theirs.Id, StringComparison.Ordinal);
}

/// <summary>
/// The Compare picker: any two scenarios of the shown ARTCC. Mine, the side Apply writes, lists the working copies only (a
/// server scenario with a copy is offered as that copy); Theirs lists the server scenarios, the working copies and File….
/// Compare is enabled when both sides are picked and name different scenarios. A copy compared with its own server
/// scenario is three-way over the base the store keeps for the copy; every other pair is two-way.
/// </summary>
public sealed partial class ScenarioComparePickerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(IsSameScenario))]
    private ScenarioCompareSide? _selectedMine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(IsSameScenario))]
    private ScenarioCompareSide? _selectedTheirs;

    /// <summary>Creates the picker over the browser's rows, with a row preselected on the side it can take.</summary>
    /// <param name="localRows">The working copies' rows; corrupt ones are not offered.</param>
    /// <param name="serverRows">The server scenarios' rows; corrupt ones are not offered.</param>
    /// <param name="preselect">
    /// The row Compare with… was asked over: a working copy (or a server scenario with one) goes on Mine as that copy, a
    /// server-only scenario on Theirs; null for none. Mine is the first copy when nothing preselects it.
    /// </param>
    public ScenarioComparePickerViewModel(
        IEnumerable<ScenarioBrowserRowViewModel> localRows,
        IEnumerable<ScenarioBrowserRowViewModel> serverRows,
        ScenarioBrowserRowViewModel? preselect
    )
    {
        ArgumentNullException.ThrowIfNull(localRows);
        ArgumentNullException.ThrowIfNull(serverRows);
        ScenarioCompareSide[] copies =
        [
            .. localRows
                .Where(r => (r.Kind == ScenarioRowKind.WorkingCopy) && !r.IsCorrupt)
                .Select(r => new ScenarioCompareSide(ScenarioCompareSideKind.WorkingCopy, r.Id, r.Name, r.OriginId)),
        ];
        ScenarioCompareSide[] servers =
        [
            .. serverRows
                .Where(r => (r.Kind == ScenarioRowKind.Server) && !r.IsCorrupt)
                .Select(r => new ScenarioCompareSide(ScenarioCompareSideKind.Server, r.Id, r.Name, null)),
        ];
        MineOptions = copies;
        TheirsOptions = [.. servers, .. copies, ScenarioCompareSide.FileEntry];
        ScenarioCompareSide? preselectedCopy = CopyFor(preselect);
        _selectedMine = preselectedCopy ?? copies.FirstOrDefault();
        _selectedTheirs = (preselectedCopy is null) ? ServerFor(preselect) : null;
    }

    /// <summary>Gets the working copies Mine can be.</summary>
    public IReadOnlyList<ScenarioCompareSide> MineOptions { get; }

    /// <summary>Gets what Theirs can be: the server scenarios, the working copies, then the file entry.</summary>
    public IReadOnlyList<ScenarioCompareSide> TheirsOptions { get; }

    /// <summary>Gets whether both sides are picked and name different scenarios.</summary>
    public bool CanCompare => (SelectedMine is not null) && (SelectedTheirs is not null) && !IsSameScenario;

    /// <summary>Gets whether both sides name the same scenario.</summary>
    public bool IsSameScenario => (SelectedMine is { } mine) && (SelectedTheirs is { } theirs) && mine.IsSameScenario(theirs);

    /// <summary>Gets the picked pair, or null while Compare is disabled.</summary>
    public ScenarioComparePick? Pick =>
        (CanCompare && (SelectedMine is { } mine) && (SelectedTheirs is { } theirs)) ? new ScenarioComparePick(mine, theirs) : null;

    /// <summary>Reads the texts a picked pair compares; run off the UI thread.</summary>
    /// <param name="store">The store holding the ARTCC's scenarios.</param>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="pick">The pair.</param>
    /// <param name="filePath">The scenario file theirs is, when theirs is the file entry; else ignored.</param>
    /// <returns>Mine's text, theirs, and mine's stored base when theirs is mine's own server scenario, else no base.</returns>
    /// <exception cref="ArgumentException">Theirs is the file entry and no file path was given.</exception>
    public static ScenarioMergeTexts ReadTexts(LocalScenarioStore store, string artccId, ScenarioComparePick pick, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pick);
        string mine = store.ReadWorkingCopyText(artccId, pick.Mine.Id);
        string theirs = pick.Theirs.Kind switch
        {
            ScenarioCompareSideKind.Server => store.ReadMirrorText(artccId, pick.Theirs.Id),
            ScenarioCompareSideKind.WorkingCopy => store.ReadWorkingCopyText(artccId, pick.Theirs.Id),
            _ => File.ReadAllText(filePath ?? throw new ArgumentException("A file to compare with is needed.", nameof(filePath))),
        };
        return new ScenarioMergeTexts(pick.SharesOrigin ? store.ReadBaseText(artccId, pick.Mine.Id) : null, mine, theirs);
    }

    // The working copy a row stands for: the copy itself, or a server scenario's copy; null for anything else.
    private ScenarioCompareSide? CopyFor(ScenarioBrowserRowViewModel? row) =>
        row switch
        {
            { Kind: ScenarioRowKind.WorkingCopy } => MineOptions.FirstOrDefault(s => string.Equals(s.Id, row.Id, StringComparison.Ordinal)),
            { Kind: ScenarioRowKind.Server, HasWorkingCopy: true } => MineOptions.FirstOrDefault(s =>
                string.Equals(s.OriginId, row.Id, StringComparison.Ordinal)
            ),
            _ => null,
        };

    private ScenarioCompareSide? ServerFor(ScenarioBrowserRowViewModel? row) =>
        (row is { Kind: ScenarioRowKind.Server })
            ? TheirsOptions.FirstOrDefault(s => (s.Kind == ScenarioCompareSideKind.Server) && string.Equals(s.Id, row.Id, StringComparison.Ordinal))
            : null;
}
