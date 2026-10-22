namespace Yaat.Client.ContextMenus;

/// <summary>
/// The solo advisory flyout's menu row header, holding its <see cref="SoloAdvisoryPanel"/>; the flyout around it is an
/// <see cref="AdvisoryFlyoutContent"/>.
/// </summary>
public sealed class SoloAdvisoryFlyoutHost : FlyoutPanelHost
{
    internal SoloAdvisoryFlyoutHost(SoloAdvisoryPanel panel)
        : base("report traffic in sight flyout", "solo-advisory-flyout-panel")
    {
        Panel = panel;
        Child = panel;
    }

    /// <summary>The panel the row holds.</summary>
    public SoloAdvisoryPanel Panel { get; }
}
