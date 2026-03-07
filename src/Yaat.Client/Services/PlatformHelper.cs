using System.Runtime.InteropServices;
using Avalonia.Input;
using SkiaSharp;

namespace Yaat.Client.Services;

/// <summary>
/// Cross-platform helpers for macOS/Windows/Linux differences.
/// </summary>
public static class PlatformHelper
{
    public static bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Returns true if the platform's "action" modifier is held (Cmd on macOS, Ctrl elsewhere).
    /// </summary>
    public static bool HasActionModifier(KeyModifiers modifiers)
    {
        return IsMacOS ? modifiers.HasFlag(KeyModifiers.Meta) : modifiers.HasFlag(KeyModifiers.Control);
    }

    /// <summary>
    /// The platform's "action" modifier name for display (⌘ on macOS, Ctrl elsewhere).
    /// </summary>
    public static string ActionModifierName => IsMacOS ? "⌘" : "Ctrl";

    /// <summary>
    /// Cached monospace SKTypeface (normal weight) with cross-platform fallback.
    /// </summary>
    public static SKTypeface MonospaceTypeface { get; } = ResolveTypeface(SKFontStyleWeight.Normal);

    /// <summary>
    /// Cached bold monospace SKTypeface with cross-platform fallback.
    /// </summary>
    public static SKTypeface MonospaceTypefaceBold { get; } = ResolveTypeface(SKFontStyleWeight.Bold);

    private static SKTypeface ResolveTypeface(SKFontStyleWeight weight)
    {
        string[] families = ["Consolas", "Menlo", "DejaVu Sans Mono", "monospace"];
        foreach (var family in families)
        {
            var typeface = SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (typeface.FamilyName != SKTypeface.Default.FamilyName || family == typeface.FamilyName)
            {
                return typeface;
            }
        }

        return SKTypeface.FromFamilyName("monospace", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    }
}
