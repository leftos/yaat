using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Views;

namespace Yaat.Client.Automation;

/// <summary>
/// Automation mode: the client runs to be driven by an agent rather than a person. It is switched
/// on by the environment variable <c>YAAT_AUTOMATION=1</c>, read once by <c>Program.Main</c>.
/// While on, every window shows never-activated and the client suppresses its own
/// <c>Activate()</c> and <c>Topmost</c> calls (through <see cref="AutomationGate"/>), and the
/// process-wide keyboard hook and Discord rich presence stay off.
/// </summary>
public static class AutomationMode
{
    private static readonly ILogger Log = AppLog.CreateLogger("AutomationMode");

    /// <summary>The environment variable that turns automation mode on when it equals <c>"1"</c>.</summary>
    public const string EnvironmentVariable = "YAAT_AUTOMATION";

    /// <summary>
    /// Whether automation mode is on. Set once at startup from <see cref="ReadFromEnvironment"/>;
    /// tests set it directly and restore it afterwards. Writes through to
    /// <see cref="AutomationGate.SuppressActivation"/>, so the Core window code and this flag
    /// can never disagree.
    /// </summary>
    public static bool IsEnabled
    {
        get => AutomationGate.SuppressActivation;
        set
        {
            if (value && !AutomationGate.SuppressActivation)
            {
                Log.LogInformation(
                    "Automation mode on ({Variable}=1): windows show never-activated; key hook and Discord presence off",
                    EnvironmentVariable
                );
            }

            AutomationGate.SuppressActivation = value;
        }
    }

    /// <summary>True when <c>YAAT_AUTOMATION</c> is exactly <c>"1"</c>; any other value, or none, is off.</summary>
    public static bool ReadFromEnvironment() => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    /// <summary>
    /// The environment variable that, when it equals <c>"1"</c>, DWM-cloaks every client window before its first show
    /// (<see cref="AutomationGate.CloakWindows"/>). Only valid together with <see cref="EnvironmentVariable"/>.
    /// </summary>
    public const string CloakEnvironmentVariable = "YAAT_CLOAK";

    /// <summary>True when <c>YAAT_CLOAK</c> is exactly <c>"1"</c>; any other value, or none, is off.</summary>
    public static bool ReadCloakFromEnvironment() => Environment.GetEnvironmentVariable(CloakEnvironmentVariable) == "1";
}
