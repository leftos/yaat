namespace Yaat.Client.Models;

/// <summary>
/// Where speech recognition runs. <see cref="Gpu"/> takes effect only when the CUDA native set is installed; without it
/// recognition stays on the CPU.
/// </summary>
public enum SttDevice
{
    /// <summary>Run speech recognition on the CPU. The default.</summary>
    Cpu,

    /// <summary>Run speech recognition on an NVIDIA GPU through the CUDA native set.</summary>
    Gpu,
}
