using System.Reflection;
using LMKit.Licensing;

namespace Yaat.Client.Services;

/// <summary>
/// Solution-wide LM-Kit license initialization. Every executable that loads LM-Kit models
/// (Yaat.Client, Yaat.SpeechSandbox, Yaat.Client.Tests) funnels through
/// <see cref="Initialize"/> so the license key lives in exactly one place per environment: an
/// untracked <c>.env</c> file for dev checkouts, and an <see cref="AssemblyMetadataAttribute"/>
/// baked into Yaat.Client.dll at publish time for installed release builds. No copy-paste
/// across executables, and the key never enters source control.
///
/// Lookup order (first hit wins):
/// <list type="number">
///   <item><description>The <c>LMKIT_LICENSE_KEY</c> process environment variable — lets CI and
///     ad-hoc shells override without editing any file.</description></item>
///   <item><description>The first <c>.env</c> file discovered walking up from
///     <see cref="AppContext.BaseDirectory"/>, parsed for a <c>LMKIT_LICENSE_KEY=&lt;value&gt;</c>
///     entry. Absent in installed builds (the walk terminates at the filesystem root), present in
///     dev checkouts (walk from <c>bin/Debug/net10.0/</c> up to the repo root).</description></item>
///   <item><description><see cref="AssemblyMetadataAttribute"/> named <c>LmKitLicenseKey</c> on
///     the Yaat.Client assembly. Injected at publish time by the release workflow via
///     <c>-p:LmKitLicenseKey=$LMKIT_LICENSE_KEY</c> (see <c>Yaat.Client.csproj</c> and
///     <c>.github/workflows/release.yml</c>). This is how installed builds pick up the key with
///     no .env and no ambient environment variable. Dev builds don't set the MSBuild property,
///     so the attribute is absent and this step is a no-op locally.</description></item>
///   <item><description>Empty string → LM-Kit Community Edition. This is the sample-code signal
///     for community tier and is the documented fallback when no key is configured.</description></item>
/// </list>
/// The baked attribute is plaintext — extractable with ILSpy or dnSpy — because LM-Kit
/// validates the key client-side and anything reaching <see cref="LicenseManager.SetLicenseKey"/>
/// is ultimately extractable regardless of obfuscation. YAAT ships with a Community Edition key
/// so this is not a commercial concern; if a paid-tier key is ever injected, accept that it is
/// retrievable from the shipped binary.
///
/// Exceptions thrown by <see cref="LicenseManager.SetLicenseKey"/> (for example, "already
/// initialized" when a test fixture calls Initialize twice in one process) are captured on
/// <see cref="LmKitLicenseInitResult.Error"/> rather than propagated — every existing caller
/// already swallows them, and capturing the exception preserves that behavior while still
/// surfacing it for logging.
/// </summary>
public static class LmKitLicense
{
    private const string EnvVarName = "LMKIT_LICENSE_KEY";
    private const string EnvFileName = ".env";
    private const string AssemblyMetadataKey = "LmKitLicenseKey";

    /// <summary>
    /// Resolves the LM-Kit license key via the lookup chain described on the class and calls
    /// <see cref="LicenseManager.SetLicenseKey"/>. Safe to call multiple times per process — a
    /// second call that LM-Kit rejects as "already initialized" is reported via
    /// <see cref="LmKitLicenseInitResult.Error"/> rather than thrown.
    /// </summary>
    public static LmKitLicenseInitResult Initialize()
    {
        var (key, source) = ResolveKey();
        var tier = string.IsNullOrEmpty(key) ? LmKitLicenseTier.Community : LmKitLicenseTier.Licensed;
        try
        {
            LicenseManager.SetLicenseKey(key ?? string.Empty);
            return new LmKitLicenseInitResult(tier, source, Error: null);
        }
        catch (Exception ex)
        {
            return new LmKitLicenseInitResult(tier, source, ex);
        }
    }

    private static (string? Key, LmKitLicenseSource Source) ResolveKey()
    {
        var envVar = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            return (envVar.Trim(), LmKitLicenseSource.EnvironmentVariable);
        }

        var dotEnvValue = ReadFromDotEnv(EnvVarName);
        if (!string.IsNullOrWhiteSpace(dotEnvValue))
        {
            return (dotEnvValue, LmKitLicenseSource.EnvFile);
        }

        var embedded = ReadFromAssemblyMetadata();
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            return (embedded, LmKitLicenseSource.AssemblyMetadata);
        }

        return (null, LmKitLicenseSource.Default);
    }

    private static string? ReadFromAssemblyMetadata()
    {
        var assembly = typeof(LmKitLicense).Assembly;
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, AssemblyMetadataKey, StringComparison.Ordinal))
            {
                return attr.Value;
            }
        }
        return null;
    }

    private static string? ReadFromDotEnv(string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, EnvFileName);
            if (File.Exists(candidate))
            {
                return TryExtract(candidate, key);
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static string? TryExtract(string path, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            // .env exists but is unreadable (permissions, locked, transient IO error). The
            // helper is non-fatal by design — fall through to the default (Community Edition).
            return null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var k = line[..eq].Trim();
            if (!string.Equals(k, key, StringComparison.Ordinal))
            {
                continue;
            }
            var v = line[(eq + 1)..].Trim();
            // Strip matching outer quotes so `LMKIT_LICENSE_KEY="abc"` and `LMKIT_LICENSE_KEY=abc`
            // both resolve to `abc`. Matches the dotenv convention used by Node tooling elsewhere
            // in the repo (tools/discord-bot) so a single .env file works for both stacks.
            if (v.Length >= 2)
            {
                if ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))
                {
                    v = v[1..^1];
                }
            }
            return v;
        }
        return null;
    }
}

/// <summary>
/// Licensing tier LM-Kit was initialized into. Community is the empty-key fallback;
/// Licensed means a non-empty key was found and accepted.
/// </summary>
public enum LmKitLicenseTier
{
    Community,
    Licensed,
}

/// <summary>
/// Where <see cref="LmKitLicense.Initialize"/> found the license key (or didn't).
/// </summary>
public enum LmKitLicenseSource
{
    Default,
    EnvironmentVariable,
    EnvFile,
    AssemblyMetadata,
}

/// <summary>
/// Outcome of <see cref="LmKitLicense.Initialize"/>. <see cref="Error"/> is non-null only when
/// <see cref="LicenseManager.SetLicenseKey"/> itself threw (most commonly "already initialized"
/// on a second call in the same process).
/// </summary>
public sealed record LmKitLicenseInitResult(LmKitLicenseTier Tier, LmKitLicenseSource Source, Exception? Error);
