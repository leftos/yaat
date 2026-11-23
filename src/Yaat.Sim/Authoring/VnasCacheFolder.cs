namespace Yaat.Sim.Authoring;

/// <summary>
/// The vNAS cache folder under the app data root (<c>cache/</c>), which only the downloaders write: the authoring
/// writers (airport map saves, scenario exports) refuse a target inside it.
/// </summary>
internal static class VnasCacheFolder
{
    /// <summary>The full path of the cache folder, or of a folder inside it, without a trailing separator.</summary>
    /// <param name="parts">The folders below <c>cache/</c>; none for the cache folder itself.</param>
    /// <returns>The full path.</returns>
    public static string FullPath(params string[] parts) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(YaatPaths.Combine(["cache", .. parts])));

    /// <summary>Whether a path is the folder or lies inside it, compared without case.</summary>
    /// <param name="folder">A full folder path from <see cref="FullPath"/>.</param>
    /// <param name="path">The path to test; made full first.</param>
    /// <returns>True when the path is inside the folder.</returns>
    public static bool Contains(string folder, string path)
    {
        string fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, folder, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
