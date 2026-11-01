namespace Yaat.Client.Models;

/// <summary>How speech recognition trades accuracy against latency.</summary>
public enum SttMode
{
    /// <summary>Favour accuracy over latency. The default.</summary>
    Accurate,

    /// <summary>Favour latency over accuracy.</summary>
    Fast,
}
