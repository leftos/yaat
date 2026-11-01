using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>Whether Accurate mode can run, read from the stock-native marker beside the sherpa-onnx C API library.</summary>
public sealed class SherpaNbestAvailabilityTests : IDisposable
{
    private const string NbestReason = "Accurate needs the n-best native; running Fast";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-sherpa-nbest-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Marker_Present_Makes_Accurate_Unavailable()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sherpa-native-FALLBACK.txt"), "YAATSHERPA001");

        (bool available, string? reason) = SherpaNativeLoader.NbestAvailability(_dir);

        Assert.False(available);
        Assert.Equal(NbestReason, reason);
    }

    [Fact]
    public void Marker_Absent_Makes_Accurate_Available()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sherpa-native-MANIFEST.txt"), "HEAD: abc");

        (bool available, string? reason) = SherpaNativeLoader.NbestAvailability(_dir);

        Assert.True(available);
        Assert.Null(reason);
    }

    [Fact]
    public void Missing_Directory_Makes_Accurate_Unavailable_With_The_Same_Reason()
    {
        (bool available, string? reason) = SherpaNativeLoader.NbestAvailability(_dir);

        Assert.False(available);
        Assert.Equal(NbestReason, reason);
    }

    [Fact]
    public void The_Live_Check_Reads_The_Output_Folder_That_Holds_The_C_Api_Library()
    {
        string library =
            OperatingSystem.IsWindows() ? "sherpa-onnx-c-api.dll"
            : OperatingSystem.IsMacOS() ? "libsherpa-onnx-c-api.dylib"
            : "libsherpa-onnx-c-api.so";

        string? dir = SherpaNativeLoader.CApiDirectory();

        Assert.NotNull(dir);
        Assert.StartsWith(AppContext.BaseDirectory, dir, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, library)), $"{dir} has no {library}");
    }

    [Fact]
    public void Whisper_Reason_Is_Its_Own_Constant() =>
        Assert.Equal("Whisper gives one hypothesis only; running Fast", SherpaNativeLoader.WhisperAccurateReason);
}
