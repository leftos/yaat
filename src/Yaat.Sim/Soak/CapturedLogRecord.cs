using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Soak;

/// <summary>One log entry captured by <see cref="CapturingSimLogProvider"/>: the formatted message plus the exception text, if any.</summary>
public sealed record CapturedLogRecord(
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? ExceptionText,
    DateTime TimestampUtc
);
