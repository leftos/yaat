using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Report field in sight…, in RPO mode and in solo training alike: the field advisory flyout (<see cref="FieldAdvisoryPanel"/>),
/// whose Enter sends <c>RFIS {clock} {miles}</c>. With no destination field the entry is disabled with the reason beside its
/// label and the strip's button is faded and inert.
/// </summary>
public static class FieldAdvisoryFlyout
{
    /// <summary>The flyout's title.</summary>
    public const string Title = "Report field in sight";

    /// <summary>Why the entry offers nothing, after its label.</summary>
    public const string NoFieldReason = "no destination field";

    /// <summary>The verb the field advisory is sent with.</summary>
    public const string Verb = "RFIS";

    /// <summary>The entry labelled <paramref name="label"/>: the field advisory flyout, or, with no destination field, inert.</summary>
    public static MenuItem Build(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        if ((aircraft is null) || (AdvisoryField.Of(aircraft) is not { } field))
        {
            MenuDisabledReasonRow.Apply(entry, label, NoFieldReason);
            QuickCommandStrip.MarkInert(entry, $"{label} · {NoFieldReason}");
            return entry;
        }

        AdvisoryFlyoutContent.Attach(
            entry,
            context,
            host,
            content =>
            {
                var panel = new FieldAdvisoryPanel(aircraft, field, context, host, content);
                return (new FieldAdvisoryFlyoutHost(panel), panel);
            }
        );
        return entry;
    }
}

/// <summary>The field advisory flyout's menu row header, holding its <see cref="FieldAdvisoryPanel"/>.</summary>
public sealed class FieldAdvisoryFlyoutHost : FlyoutPanelHost
{
    internal FieldAdvisoryFlyoutHost(FieldAdvisoryPanel panel)
        : base("report field in sight flyout", "field-advisory-flyout-panel")
    {
        Panel = panel;
        Child = panel;
    }

    /// <summary>The panel the row holds.</summary>
    public FieldAdvisoryPanel Panel { get; }
}
