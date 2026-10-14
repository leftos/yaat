namespace Yaat.Sim.Weather;

/// <summary>
/// Thrown by <see cref="Grib2Reader"/> when the bytes are not a GRIB2 file it can decode: a wrong edition, a template
/// outside the supported subset (grid 3.0, product 4.0, data representation 5.0, bitmap 0/255), or a truncated or
/// malformed message. The message names the section and the value found.
/// </summary>
public sealed class Grib2FormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, naming the section and the template or value found.</param>
    public Grib2FormatException(string message)
        : base(message) { }

    /// <summary>Creates the exception around the error that exposed the bad value.</summary>
    /// <param name="message">What is wrong, naming the section and the template or value found.</param>
    /// <param name="innerException">The error raised while interpreting the value.</param>
    public Grib2FormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
