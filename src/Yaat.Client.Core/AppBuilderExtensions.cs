using Avalonia;
using Avalonia.Media.Fonts;

namespace Yaat.Client;

/// <summary>
/// Avalonia bootstrap helpers shared by every entry point that hosts the
/// strip view (desktop client, standalone vStrips, WASM).
/// </summary>
public static class AppBuilderExtensions
{
    /// <summary>
    /// Registers the embedded JetBrains Mono font collection so the
    /// <c>MonoFont</c> resource (and anything that falls through to it)
    /// can resolve <c>fonts:JetBrainsMono#JetBrains Mono</c> at runtime.
    /// Without this the font is shipped as an <c>AvaloniaResource</c> but
    /// never registered with the font manager, so Skia falls all the way
    /// through to whatever else is loaded — Inter on WASM (proportional —
    /// clips strip-cell columns), or the system mono on desktop. Calling
    /// this from every <c>Program.Main</c> guarantees JetBrains Mono is
    /// available regardless of platform.
    /// </summary>
    public static AppBuilder WithJetBrainsMonoFont(this AppBuilder appBuilder) =>
        appBuilder.ConfigureFonts(fontManager =>
        {
            fontManager.AddFontCollection(
                new EmbeddedFontCollection(
                    new Uri("fonts:JetBrainsMono", UriKind.Absolute),
                    new Uri("avares://Yaat.Client.Core/Resources/Fonts", UriKind.Absolute)
                )
            );
        });
}
