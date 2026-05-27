using System.Runtime.InteropServices.JavaScript;

namespace Yaat.VTdls.Web;

/// <summary>
/// Thin <c>JSImport</c> wrapper around the browser's <c>localStorage</c> so
/// the WASM app can persist user preferences (dark mode, etc.) across
/// reloads without round-tripping through yaat-server. Calls are
/// synchronous — <c>localStorage</c> reads/writes are negligible.
/// </summary>
internal static partial class BrowserStorage
{
    [JSImport("globalThis.localStorage.getItem")]
    internal static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    internal static partial void SetItem(string key, string value);
}
