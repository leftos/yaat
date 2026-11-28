namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// What one strip step does to the aircraft's flight strip or half-strip. Each kind is one preset strip command; the
/// editor writes every kind but <see cref="Other"/>.
/// </summary>
public enum StripStepKind
{
    /// <summary><c>STRIP</c>: move the full strip into a bay rack.</summary>
    Move,

    /// <summary><c>AN</c>: write or clear one annotation box.</summary>
    Annotate,

    /// <summary><c>STRIPO</c>: toggle the full strip's offset.</summary>
    Offset,

    /// <summary><c>STRIPD</c>: delete the full strip.</summary>
    Delete,

    /// <summary><c>HSC</c>: create the aircraft's half-strip in a bay rack.</summary>
    HalfCreate,

    /// <summary><c>HSA</c>: replace the half-strip's lines.</summary>
    HalfAmend,

    /// <summary><c>HSM</c>: move the half-strip into a bay rack.</summary>
    HalfMove,

    /// <summary><c>HSO</c>: toggle the half-strip's offset.</summary>
    HalfOffset,

    /// <summary><c>HSS</c>: slide the half-strip to the other side.</summary>
    HalfSlide,

    /// <summary><c>HSD</c>: delete the half-strip.</summary>
    HalfDelete,

    /// <summary>A read-only strip command the editor shows but never writes: <c>SCAN</c>, separators, <c>RACKSORT</c>, blanks.</summary>
    Other,
}
