namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Where a browser row's scenario lives.</summary>
public enum ScenarioRowKind
{
    /// <summary>The server mirror: a downloaded server scenario, never edited.</summary>
    Server,

    /// <summary>A working copy: the scenario the author edits.</summary>
    WorkingCopy,
}

/// <summary>A marker shown on a browser row as an icon with a tooltip.</summary>
public enum ScenarioBadge
{
    /// <summary>The working copy's content differs from the server scenario it was started from.</summary>
    Modified,

    /// <summary>The server scenario the working copy was started from has changed since.</summary>
    ServerChanged,

    /// <summary>The server no longer lists the scenario (or the working copy's origin).</summary>
    GoneFromServer,

    /// <summary>The file is not a readable scenario; the row's problem says why.</summary>
    Corrupt,

    /// <summary>The working copy has no server scenario behind it.</summary>
    LocalOnly,

    /// <summary>The working copy has changes not yet exported as vNAS JSON.</summary>
    Unexported,
}

/// <summary>Which of the browser's two lists shows.</summary>
public enum ScenarioBrowserTab
{
    /// <summary>The working copies.</summary>
    Local,

    /// <summary>The server mirror.</summary>
    Server,
}

/// <summary>
/// One scenario in the browser: a server mirror entry or a working copy, with its badges. Rows are rebuilt after
/// every store change, so a row never changes; a later row for the same scenario has the same <see cref="Id"/>.
/// </summary>
public sealed class ScenarioBrowserRowViewModel
{
    /// <summary>Gets the scenario id: the server id for a mirror, the file name for a working copy.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the scenario name, or the id when the file cannot be read.</summary>
    public required string Name { get; init; }

    /// <summary>Gets where the scenario lives.</summary>
    public required ScenarioRowKind Kind { get; init; }

    /// <summary>Gets the row's markers, in the order they show.</summary>
    public IReadOnlyList<ScenarioBadge> Badges { get; init; } = [];

    /// <summary>Gets what the store found wrong with the row's files, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>Gets the server scenario a working copy was started from; null for a local-only copy or a server row.</summary>
    public string? OriginId { get; init; }

    /// <summary>Gets the mirror version a working copy was started from; null for a local-only copy or a server row.</summary>
    public string? BaseVersion { get; init; }

    /// <summary>Gets a value indicating whether a server row's scenario already has its working copy.</summary>
    public bool HasWorkingCopy { get; init; }

    /// <summary>Gets a value indicating whether the row's file is not a readable scenario.</summary>
    public bool IsCorrupt => Badges.Contains(ScenarioBadge.Corrupt);

    /// <summary>Gets a value indicating whether a working copy has changes not yet exported as vNAS JSON.</summary>
    public bool HasUnexportedChanges => Badges.Contains(ScenarioBadge.Unexported);

    /// <summary>Gets a value indicating whether the scenario can be opened for editing: every row but a corrupt one.</summary>
    public bool CanOpen => !IsCorrupt;
}

/// <summary>A request to open a working copy in the editor.</summary>
/// <param name="artccId">The ARTCC.</param>
/// <param name="id">The working copy's id.</param>
/// <param name="kind">What is opened: always <see cref="ScenarioRowKind.WorkingCopy"/>, as a server scenario opens through its copy.</param>
public sealed class ScenarioOpenRequestedEventArgs(string artccId, string id, ScenarioRowKind kind) : EventArgs
{
    /// <summary>Gets the ARTCC.</summary>
    public string ArtccId { get; } = artccId;

    /// <summary>Gets the working copy's id.</summary>
    public string Id { get; } = id;

    /// <summary>Gets what is opened.</summary>
    public ScenarioRowKind Kind { get; } = kind;
}
