using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

/// <summary>
/// What an imported preference value must be before it is written: the range its Settings control and setter allow, a
/// colour, an enum name, a key name or a model source. A value outside the rule is rejected, never clamped.
/// </summary>
internal sealed partial class BundledPreferenceRule
{
    private readonly Func<object?, bool> _accepts;

    private BundledPreferenceRule(Type valueType, string description, Func<object?, bool> accepts)
    {
        ValueType = valueType;
        Description = description;
        _accepts = accepts;
    }

    /// <summary>The preference type the rule is written for; a test checks it matches the preference's own type.</summary>
    public Type ValueType { get; }

    /// <summary>What the value must be, in words, for the log line of a rejected value.</summary>
    public string Description { get; }

    public static BundledPreferenceRule Flag { get; } = new(typeof(bool), "true or false", v => v is bool);

    public static BundledPreferenceRule Color { get; } =
        new(typeof(string), "a colour #RRGGBB or #AARRGGBB", v => (v is string s) && ColorRegex().IsMatch(s));

    public static BundledPreferenceRule KeyName { get; } =
        new(typeof(string), "a key name such as Ctrl+T, or empty", v => (v is string s) && (s.Length <= 40) && KeyNameRegex().IsMatch(s));

    public static BundledPreferenceRule ModelSource { get; } =
        new(typeof(string), "a curated model id or an https://huggingface.co address", v => (v is string s) && IsImportableModelSource(s));

    public bool Accepts(object? value) => _accepts(value);

    public static BundledPreferenceRule IntRange(int min, int max) =>
        new(typeof(int), $"a whole number from {min} to {max}", v => (v is int i) && (i >= min) && (i <= max));

    public static BundledPreferenceRule DoubleRange(double min, double max) =>
        new(typeof(double), $"a number from {min} to {max}", v => (v is double d) && (d >= min) && (d <= max));

    public static BundledPreferenceRule OptionalDoubleRange(double min, double max) =>
        new(typeof(double?), $"empty, or a number from {min} to {max}", v => (v is null) || ((v is double d) && (d >= min) && (d <= max)));

    public static BundledPreferenceRule OneOf(params string[] values) =>
        new(typeof(string), $"one of '{string.Join("', '", values)}'", v => (v is string s) && values.Contains(s, StringComparer.Ordinal));

    public static BundledPreferenceRule EnumName<TEnum>()
        where TEnum : struct, Enum =>
        new(typeof(string), $"a {typeof(TEnum).Name} name", v => (v is string s) && Enum.GetNames<TEnum>().Contains(s, StringComparer.Ordinal));

    /// <summary>
    /// An imported model source must be a curated id (no slash) or an https address on huggingface.co or one of its
    /// subdomains, without a user part or a query; a local path or any other address could point the client anywhere.
    /// </summary>
    public static bool IsImportableModelSource(string source)
    {
        if (IsCuratedModelId(source))
        {
            return true;
        }

        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps)
            && IsHuggingFaceHost(uri.Host)
            && (uri.UserInfo.Length == 0)
            && (uri.Query.Length == 0);
    }

    /// <summary>An exported model source leaves out a local path and any address with a user part or a query, which may carry a token.</summary>
    public static bool IsExportableModelSource(string source)
    {
        if (IsCuratedModelId(source))
        {
            return true;
        }

        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && ((uri.Scheme == Uri.UriSchemeHttps) || (uri.Scheme == Uri.UriSchemeHttp))
            && (uri.UserInfo.Length == 0)
            && (uri.Query.Length == 0);
    }

    // A curated id has no slash, so it is not a path, and no drive letter: "C:model.gguf" is a drive-relative path the
    // speech engines load as a local file. A colon later in the id ("gemma4:e4b") is part of the id.
    private static bool IsCuratedModelId(string source) =>
        !string.IsNullOrWhiteSpace(source)
        && !source.Contains('/')
        && !source.Contains('\\')
        && !source.Any(char.IsWhiteSpace)
        && !((source.Length >= 2) && (source[1] == ':') && char.IsAsciiLetter(source[0]));

    private static bool IsHuggingFaceHost(string host) =>
        string.Equals(host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"^([A-Za-z0-9]+(\+[A-Za-z0-9]+){0,3})?$")]
    private static partial Regex KeyNameRegex();
}
