namespace Yaat.WindowRecorder;

/// <summary>Raised when the process-loopback audio client cannot be activated or initialised for the requested process.</summary>
internal sealed class AudioActivationException : Exception
{
    public AudioActivationException() { }

    public AudioActivationException(string message)
        : base(message) { }

    public AudioActivationException(string message, Exception innerException)
        : base(message, innerException) { }
}
