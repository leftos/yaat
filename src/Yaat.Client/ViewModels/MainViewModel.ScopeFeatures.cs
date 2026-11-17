namespace Yaat.Client.ViewModels;

/// <summary>The <c>.scope</c> verbs the Scope's features add, tried after every built-in verb.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Offers <paramref name="verb"/> (upper-cased) and its <paramref name="parts"/> to each feature's verbs. Returns the status
    /// text of the feature that took it, or null when none did.
    /// </summary>
    private string? RunFeatureVerb(string verb, string[] parts)
    {
        string? result = null;
        RunMapToolsVerb(verb, parts, ref result);
        RunDataBlocksVerb(verb, parts, ref result);
        return result;
    }

    partial void RunMapToolsVerb(string verb, string[] parts, ref string? result);

    partial void RunDataBlocksVerb(string verb, string[] parts, ref string? result);
}
