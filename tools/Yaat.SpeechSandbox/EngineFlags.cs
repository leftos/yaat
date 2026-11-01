using System.Globalization;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// The engine flags <c>--eval</c> and <c>--atc-ouroboros</c> share: <c>--stt</c>, <c>--device</c>, <c>--decoding</c>,
/// <c>--num-paths</c> and <c>--accurate-text</c>, parsed one at a time and then checked against each other into one engine
/// choice. The messages are the ones <c>--corpus-wer</c> prints for the same flags.
/// </summary>
public sealed class EngineFlags
{
    private SttChoice? _stt;
    private SttDevice? _device;
    private SherpaDecodingMethod? _decoding;
    private int? _numPaths;
    private AccurateCommandText? _accurateText;

    /// <summary>True for exactly the five engine flags, each of which takes a value.</summary>
    public static bool Handles(string flag) => flag is "--stt" or "--device" or "--decoding" or "--num-paths" or "--accurate-text";

    /// <summary>Which text accurate mode hands the mappers; <see cref="AccurateCommandText.RawHypothesis"/> unless the flags said otherwise.</summary>
    public AccurateCommandText AccurateText => _accurateText ?? AccurateCommandText.RawHypothesis;

    /// <summary>Applies one engine flag's value.</summary>
    /// <param name="flag">One of the flags <see cref="Handles"/> accepts.</param>
    /// <param name="value">The flag's value as given.</param>
    /// <returns>Null when the value applied, else why it did not.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The flag is not an engine flag.</exception>
    public string? TryApply(string flag, string value) =>
        flag switch
        {
            "--stt" => ApplyStt(value),
            "--device" => ApplyDevice(value),
            "--decoding" => ApplyDecoding(value),
            "--num-paths" => ApplyNumPaths(value),
            "--accurate-text" => ApplyAccurateText(value),
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "not an engine flag"),
        };

    /// <summary>
    /// The engine choice the flags make: null with no error when none was given (the run keeps the preferences' Whisper),
    /// null with the error when they contradict each other.
    /// </summary>
    /// <param name="error">Why the flags do not make a choice; null otherwise.</param>
    public SttChoice? Resolve(out string? error)
    {
        if (_stt is null)
        {
            bool anyEngineFlag = (_device is not null) || (_decoding is not null) || (_numPaths is not null) || (_accurateText is not null);
            error = anyEngineFlag ? "--device, --decoding and --num-paths need --stt" : null;
            return null;
        }

        error = DeviceError(_stt) ?? DecodingError(_stt);
        if (error is not null)
        {
            return null;
        }

        SttChoice choice = (_device is SttDevice device) ? _stt with { Device = device } : _stt;
        if (_decoding != SherpaDecodingMethod.Beam)
        {
            return choice;
        }
        var beam = new SherpaDecoding(SherpaDecodingMethod.Beam, _numPaths ?? SherpaDecoding.MaxBeamPaths, SherpaDecoding.DefaultHotwordScore);
        return choice with { Decoding = beam };
    }

    private string? ApplyStt(string value)
    {
        if (!CorpusSttEngines.TryParse(value, out SttChoice? choice, out string? error))
        {
            return error;
        }
        _stt = choice;
        return null;
    }

    private string? ApplyDevice(string value)
    {
        if (value is not ("cpu" or "gpu"))
        {
            return $"--device must be cpu or gpu, got '{value}'";
        }
        _device = (value == "cpu") ? SttDevice.Cpu : SttDevice.Gpu;
        return null;
    }

    private string? ApplyDecoding(string value)
    {
        if (value is not ("greedy" or "beam"))
        {
            return $"--decoding must be greedy or beam, got '{value}'";
        }
        _decoding = (value == "greedy") ? SherpaDecodingMethod.Greedy : SherpaDecodingMethod.Beam;
        return null;
    }

    private string? ApplyNumPaths(string value)
    {
        bool valid = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int paths);
        if (!valid || (paths < 1) || (paths > SherpaDecoding.MaxBeamPaths))
        {
            return $"--num-paths needs an integer from 1 to {SherpaDecoding.MaxBeamPaths}, got '{value}'";
        }
        _numPaths = paths;
        return null;
    }

    private string? ApplyAccurateText(string value)
    {
        if (value is not ("raw" or "rewritten"))
        {
            return $"--accurate-text must be raw or rewritten, got '{value}'";
        }
        _accurateText = (value == "raw") ? AccurateCommandText.RawHypothesis : AccurateCommandText.Rewritten;
        return null;
    }

    /// <summary>The cloud engines have no local device to name.</summary>
    private string? DeviceError(SttChoice stt) =>
        ((_device is not null) && (stt.Kind is SttKind.Gemini or SttKind.OpenAi))
            ? $"--device applies to local engines; {CorpusSttEngines.KindText(stt.Kind)} runs in the cloud"
            : null;

    private string? DecodingError(SttChoice stt)
    {
        if ((_decoding is not null) && (stt.Kind != SttKind.Sherpa))
        {
            return $"--decoding applies to the sherpa engine, got {CorpusSttEngines.KindText(stt.Kind)}";
        }
        if ((_numPaths is not null) && (_decoding != SherpaDecodingMethod.Beam))
        {
            return "--num-paths needs --decoding beam";
        }
        return ((_accurateText is not null) && (_decoding != SherpaDecodingMethod.Beam)) ? "--accurate-text needs --decoding beam" : null;
    }
}
