namespace Yaat.Client.ViewModels;

/// <summary>The map tools' <c>.scope</c> verbs: <c>.scope mva</c>, <c>.scope lock</c> and <c>.scope center</c>.</summary>
public partial class MainViewModel
{
    private const string ScopeMvaUsage = "Usage: .scope mva <on|off>";

    private const string ScopeLockUsage = "Usage: .scope lock <on|off>";

    private const string ScopeCenterUsage = "Usage: .scope center";

    /// <summary>The answer of <c>.scope center</c> while the Scope's map source has no centre to return to.</summary>
    internal const string ScopeNoHomeCenter = "The Scope has no home centre yet";

    partial void RunMapToolsVerb(string verb, string[] parts, ref string? result)
    {
        if (result is not null)
        {
            return;
        }

        Func<ScopeViewModel, string[], string>? handler = verb switch
        {
            "MVA" => SetScopeMva,
            "LOCK" => SetScopeLock,
            "CENTER" => RecenterScope,
            _ => null,
        };
        if (handler is not null)
        {
            result = OpenScope is { } scope ? handler(scope, parts) : "No YAAT Scope is open";
        }
    }

    /// <summary><c>.scope mva on|off</c>: turns the MVA altitude tint on or off.</summary>
    private static string SetScopeMva(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeMvaUsage;
        }

        scope.ShowMvaHints = on;
        return on ? "Scope MVA ON" : "Scope MVA OFF";
    }

    /// <summary><c>.scope lock on|off</c>: locks or unlocks wheel zoom and right-drag pan.</summary>
    private static string SetScopeLock(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeLockUsage;
        }

        scope.IsPanZoomLocked = on;
        return on ? "Scope LOCK ON" : "Scope LOCK OFF";
    }

    /// <summary><c>.scope center</c>: pans back to the facility's home centre, keeping the range (OFF CNTR).</summary>
    private static string RecenterScope(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 2)
        {
            return ScopeCenterUsage;
        }

        return scope.Recenter() ? "Scope centred" : ScopeNoHomeCenter;
    }
}
