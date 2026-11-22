namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The ARTCCs whose scenarios the signed-in user may edit: the ones they can load scenarios for.</summary>
public interface IArtccAccessSource
{
    /// <summary>Reads the ARTCC ids the user may edit, in the order the editor lists them.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The ARTCC ids; empty when the user may edit none.</returns>
    Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct);
}
