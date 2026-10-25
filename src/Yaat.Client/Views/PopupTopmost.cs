using Avalonia;
using Avalonia.Controls.Primitives;

namespace Yaat.Client.Views;

/// <summary>
/// Marks every <see cref="Popup"/> topmost when it opens.
/// </summary>
/// <remarks>
/// On Windows each popup is hosted in its own native owned window, which is not topmost by default, so a
/// context menu opened from a YAAT window that is not pinned draws underneath a pinned one (Ground View
/// always on top, GitHub #1349). Setting <see cref="Popup.Topmost"/> while the popup is open puts its host
/// in the topmost band, the way a native menu behaves, and it drops back out when the popup closes.
/// </remarks>
public static class PopupTopmost
{
    private static bool _registered;

    /// <summary>
    /// Installs the app-wide class handler. Idempotent, so the headless test host can call it as often as
    /// the app is built.
    /// </summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(
            (popup, e) =>
            {
                if (!e.GetNewValue<bool>())
                {
                    return;
                }

                if (!AutomationGate.SuppressActivation)
                {
                    popup.Topmost = true;
                }
            }
        );
    }
}
