using Yaat.Sim;

namespace Yaat.Client.Services;

internal static class PilotVoicePack
{
    public const string DirectoryName = "vits-piper-en_US-libritts_r-medium";
    public const string ModelFileName = "en_US-libritts_r-medium.onnx";
    public const string TokensFileName = "tokens.txt";
    public const string EspeakDataDirectoryName = "espeak-ng-data";

    public static string InstallRoot => YaatPaths.Combine("voices", DirectoryName);

    public static bool IsComplete(string voiceDir)
    {
        return File.Exists(Path.Combine(voiceDir, ModelFileName))
            && File.Exists(Path.Combine(voiceDir, TokensFileName))
            && Directory.Exists(Path.Combine(voiceDir, EspeakDataDirectoryName));
    }

    public static string? FindInstalledDirectory()
    {
        const string developmentRelative = ".tmp/voices/vits-piper-en_US-libritts_r-medium";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, developmentRelative);
            if (IsComplete(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return IsComplete(InstallRoot) ? InstallRoot : null;
    }
}
