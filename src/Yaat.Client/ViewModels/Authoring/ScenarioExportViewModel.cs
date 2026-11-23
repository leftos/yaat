using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One ARTCC store's exports as vNAS JSON, for the browser's Export JSON, Export clean JSON and Export all unexported:
/// prepares a working copy's export from its saved file, writes it and records it in the copy's sidecar, and words the
/// notices and file names. Holds no UI state and runs off the UI thread; YAAT sends nothing to vNAS, so an export only
/// ever writes the picked file and the copy's sidecar.
/// </summary>
/// <param name="store">The store whose working copies it exports.</param>
/// <param name="clock">The clock an export's record is stamped with.</param>
internal sealed class ScenarioExportViewModel(LocalScenarioStore store, TimeProvider clock)
{
    /// <summary>Reads a working copy's saved file and prepares its export, running the export's checks.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <param name="kind">Full or clean.</param>
    /// <returns>The prepared export: written, refused or corrupt.</returns>
    public ExportResult Prepare(string artccId, string id, ExportKind kind) =>
        WorkingCopyExporter.Prepare(store.ReadWorkingCopyText(artccId, id), kind, WorkingCopyExporter.DefaultChecks);

    /// <summary>Writes a prepared export to the path, then records the export; a failed write records nothing.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <param name="result">A written result of <see cref="Prepare"/>.</param>
    /// <param name="path">The file to write.</param>
    public void Write(string artccId, string id, ExportResult result, string path)
    {
        WorkingCopyExporter.WriteTo(result, path);
        store.RecordExport(artccId, id, clock.GetUtcNow());
    }

    /// <summary>The notice of a refused or corrupt export: the scenario's name and each error.</summary>
    /// <param name="name">The scenario's name.</param>
    /// <param name="result">The refused or corrupt result.</param>
    /// <returns>The notice line.</returns>
    public static string RefusalNotice(string name, ExportResult result) =>
        $"{name}: not exported: {string.Join("; ", result.Issues.Where(i => i.Severity == ExportIssueSeverity.Error).Select(i => i.Message))}";

    /// <summary>
    /// The notices of a written export: the YAAT-only parts it kept or dropped, then each warning of its checks, each
    /// line headed by the scenario's name. Empty when there is nothing to warn about.
    /// </summary>
    /// <param name="name">The scenario's name.</param>
    /// <param name="kind">Full or clean.</param>
    /// <param name="result">The written result.</param>
    /// <returns>The notice lines.</returns>
    public static IReadOnlyList<string> WarningNotices(string name, ExportKind kind, ExportResult result)
    {
        var notices = new List<string>();
        if (WorkingCopyExporter.DescribeParts(kind, result.Parts) is { } parts)
        {
            notices.Add($"{name}: {parts}");
        }

        notices.AddRange(result.Issues.Where(i => i.Severity == ExportIssueSeverity.Warning).Select(i => $"{name}: {i.Message}"));
        return notices;
    }

    /// <summary>The file name a single export offers: the scenario's name made safe for a file name, or its id.</summary>
    /// <param name="name">The scenario's name.</param>
    /// <param name="id">The scenario's id.</param>
    /// <returns>The file name, with <c>.json</c>.</returns>
    public static string FileName(string name, string id) => (SafeName(name) is { Length: > 0 } safe ? safe : id) + ".json";

    /// <summary>
    /// The file name Export all unexported writes: the scenario's name made safe for a file name, a dash and its id, so two
    /// scenarios of one name never clash; the id alone when the name leaves nothing.
    /// </summary>
    /// <param name="name">The scenario's name.</param>
    /// <param name="id">The scenario's id.</param>
    /// <returns>The file name, with <c>.json</c>.</returns>
    public static string BulkFileName(string name, string id) => (SafeName(name) is { Length: > 0 } safe ? $"{safe}-{id}" : id) + ".json";

    // The name with every character a file name cannot hold replaced by an underscore, trimmed.
    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
    }
}
