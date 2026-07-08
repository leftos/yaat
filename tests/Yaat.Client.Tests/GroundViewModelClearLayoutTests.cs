using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

// Unloading a scenario runs ClearScenarioState -> Ground.ClearLayout, which used to
// Dispose() the tower-cab background SKImage synchronously on the UI thread. Avalonia's
// compositor renders on a SEPARATE thread from a RenderSnapshot that still holds that same
// SKImage (GroundCanvas.CreateRenderSnapshot captures BackgroundImage; RenderFromSnapshot ->
// GroundRenderer.DrawBackgroundImage reads image.Image.Width). Disposing mid-render freed the
// native SkImage out from under sk_image_get_width(), crashing the process with an
// ExecutionEngineException. ClearLayout must drop the reference WITHOUT disposing; the image
// is reclaimed by finalization once no in-flight snapshot references it — matching the
// scenario-switch swap path (LoadTowerCabLayersAsync), which overwrites BackgroundImage and
// never disposed either.
public class GroundViewModelClearLayoutTests
{
    [Fact]
    public void ClearLayout_DropsBackgroundImage_WithoutDisposingIt()
    {
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);

        using var bitmap = new SKBitmap(4, 4);
        var image = SKImage.FromBitmap(bitmap);
        Assert.NotEqual(IntPtr.Zero, image.Handle);
        vm.BackgroundImage = new TowerCabImage(image, 0, 0, 1, 1);

        vm.ClearLayout();

        // Reference is dropped so the display stops drawing it...
        Assert.Null(vm.BackgroundImage);
        // ...but the native SkImage must NOT be freed: a render snapshot on the compositor
        // thread may still hold it. A zero handle means Dispose() ran (the crash).
        Assert.NotEqual(IntPtr.Zero, image.Handle);

        image.Dispose();
    }
}
