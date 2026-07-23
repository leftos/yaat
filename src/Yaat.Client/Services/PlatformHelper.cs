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

    /// <summary>
    /// A monospace <see cref="SKFont"/> at <paramref name="size"/> px, matching the radar/ground
    /// rendering convention: subpixel glyph positioning on, antialiasing left at Skia's default
    /// (<see cref="SKFontEdging.Antialias"/>).
    /// <para>
    /// Callers own the returned font and must dispose it. Fonts are mutable, so each caller gets its
    /// own instance rather than a cached one — the datablock font in particular is resized at runtime
    /// from the user's font-size preference.
    /// </para>
    /// </summary>
    public static SKFont MonospaceFont(float size) =>
        new()
        {
            Size = size,
            Subpixel = true,
            Typeface = MonospaceTypeface,
        };

    /// <summary>Bold counterpart of <see cref="MonospaceFont"/>. Caller owns and disposes the result.</summary>
    public static SKFont MonospaceFontBold(float size) =>
        new()
        {
            Size = size,
            Subpixel = true,
            Typeface = MonospaceTypefaceBold,
        };

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
