using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// A suppressed STARS pair's CA line (<see cref="ScopeFieldStyle.Dimmed"/>) draws in the conflict red at half the ALERTS
/// brightness; an unsuppressed one at the full ALERTS brightness.
/// </summary>
public class ScopeConflictStateTests
{
    private static FieldRect FieldIn(ScopeFieldStyle style) =>
        new(ScopeFieldSlot.ConflictAlert, new SKRect(0, 0, 10, 10), "CA", ScopeColorToken.ConflictAlert, style, ScopeFieldAction.None);

    [Fact]
    public void ADimmedCaLine_DrawsAtHalfTheAlertsBrightness()
    {
        ScopeBrightness brightness = ScopeBrightness.Default with { Alerts = 80, Fdb = 60 };

        Assert.Equal(40, ScopeRenderer.FieldPercent(FieldIn(ScopeFieldStyle.Dimmed), brightness));
        Assert.Equal(80, ScopeRenderer.FieldPercent(FieldIn(ScopeFieldStyle.None), brightness));
    }
}
