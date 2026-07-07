namespace Yaat.Client.Models;

/// <summary>
/// User-selectable GPU rendering backend for the desktop client. Only applied on macOS, where the
/// default OpenGL path is emulated on top of Metal (<c>AppleMetalOpenGLRenderer</c>) and burns CPU
/// on Apple Silicon; rendering directly on Metal avoids the translation layer. On Windows and Linux
/// the value is ignored — those platforms keep Avalonia's auto-detected backend.
/// </summary>
public enum RendererMode
{
    /// <summary>Let YAAT pick the best backend for the platform (Metal on macOS). The default.</summary>
    Auto,

    /// <summary>Render on Metal directly, falling back to OpenGL then software if unavailable.</summary>
    Metal,

    /// <summary>Render on OpenGL (emulated over Metal on Apple Silicon). Compatibility fallback.</summary>
    OpenGl,

    /// <summary>CPU software rendering. Last-resort fallback when GPU rendering misbehaves.</summary>
    Software,
}
