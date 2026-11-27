using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What the merge window opens over and how it writes the merged copy.</summary>
/// <param name="ArtccId">The ARTCC the scenario belongs to.</param>
/// <param name="ScenarioId">The scenario's id.</param>
/// <param name="Texts">The base, mine and theirs texts.</param>
/// <param name="Captions">The window's title and the captions over each side.</param>
/// <param name="Write">Writes the merged text when the author applies.</param>
public sealed record ScenarioMergeRequest(
    string ArtccId,
    string ScenarioId,
    ScenarioMergeTexts Texts,
    ScenarioMergeCaptions Captions,
    Func<string, Task> Write
);

/// <summary>The scenario texts a merge compares.</summary>
/// <param name="Base">The text both sides started from; null for a two-way merge.</param>
/// <param name="Mine">Mine: the working copy.</param>
/// <param name="Theirs">Theirs: the other version.</param>
public sealed record ScenarioMergeTexts(string? Base, string Mine, string Theirs);

/// <summary>The words the merge window shows for the scenario and each side.</summary>
/// <param name="Title">The window's title.</param>
/// <param name="Mine">The caption over mine.</param>
/// <param name="Theirs">The caption over theirs.</param>
/// <param name="Base">The caption naming the base, shown after "base: ".</param>
/// <param name="TwoWayNote">The note strip shown when there is no base.</param>
public sealed record ScenarioMergeCaptions(string Title, string Mine, string Theirs, string Base, string TwoWayNote);

/// <summary>What an applied merge wrote.</summary>
/// <param name="MergedJson">The merged scenario text.</param>
/// <param name="Warnings">The checks' findings on the merged scenario.</param>
public sealed record ScenarioMergeResult(string MergedJson, IReadOnlyList<ScenarioEditIssue> Warnings);
