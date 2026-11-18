using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Applying another scenario's views offers the Ground view's saved settings only: per-scenario Radar settings are no
/// longer saved, so the dialog shows no Radar group.
/// </summary>
public class ApplyLayoutDialogScenarioRowsTests
{
    [AvaloniaFact]
    public void AnotherScenariosViews_OfferTheGroundRowsOnly()
    {
        var prefs = new UserPreferences();
        prefs.SetGroundSettings("aldsr-source", new SavedGroundSettings { Zoom = 2 });
        var context = new ApplyLayoutContext
        {
            Preferences = prefs,
            CurrentScenarioId = "aldsr-current",
            CurrentScenarioName = "Current",
            CurrentGround = new SavedGroundSettings(),
            CurrentLayout = new SavedLayout { Name = "(current)" },
        };

        var dialog = new ApplyLayoutDialog(context);
        try
        {
            StackPanel rows = dialog.FindControl<StackPanel>("RowsPanel")!;
            List<string?> headers = [.. rows.Children.OfType<TextBlock>().Where(t => t.FontWeight == FontWeight.SemiBold).Select(t => t.Text)];
            List<object?> labels = [.. rows.Children.OfType<Grid>().Select(g => g.Children.OfType<CheckBox>().Single().Content)];

            Assert.Equal(["Ground view"], headers);
            Assert.Equal(ViewSettingsCopyCatalog.GroundGroups.Select(g => (object?)g.Label), labels);
        }
        finally
        {
            dialog.Close();
        }
    }
}
