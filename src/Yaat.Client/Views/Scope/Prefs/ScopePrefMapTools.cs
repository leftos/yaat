namespace Yaat.Client.Views.Scope.Prefs;

/// <summary>The Scope's map-tool settings as a pref set stores them; a field missing from a file loads as false.</summary>
/// <param name="Mva">Whether the MVA altitude tint is on.</param>
/// <param name="Lock">Whether pan and zoom are locked.</param>
public sealed record ScopePrefMapTools(bool Mva, bool Lock);
