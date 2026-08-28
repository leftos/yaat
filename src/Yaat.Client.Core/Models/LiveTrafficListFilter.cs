namespace Yaat.Client.Models;

/// <summary>How the Aircraft List treats live-traffic shadows; persisted in <c>UserPreferences</c>.</summary>
public enum LiveTrafficListFilter
{
    /// <summary>Shadows listed alongside simulated aircraft.</summary>
    All,

    /// <summary>Only simulated (and assumed) aircraft are listed.</summary>
    HideLive,

    /// <summary>Only live-traffic shadows are listed.</summary>
    OnlyLive,
}
