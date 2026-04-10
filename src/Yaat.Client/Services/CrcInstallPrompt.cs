using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Yaat.Client.Services;

/// <summary>
/// Standalone CRC configuration dialog shown during Velopack's AfterInstall callback.
/// Runs before the main Avalonia app starts — spins up its own dispatcher loop.
/// </summary>
public static class CrcInstallPrompt
{
    public static void Show()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        if (!CrcConfigService.IsCrcInstalled())
        {
            return;
        }

        if (CrcConfigService.AreYaatEntriesPresent())
        {
            return;
        }

        // Build a minimal Avalonia app just for this dialog.
        // The main app hasn't started yet (this runs in the Velopack install callback).
        var appBuilder = AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont();

        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnMainWindowClose };
        appBuilder.SetupWithLifetime(lifetime);

        bool userAccepted = false;

        var yesButton = new Button
        {
            Content = "Yes",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var noButton = new Button
        {
            Content = "No",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var window = new Window
        {
            Title = "YAAT — CRC Configuration",
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "CRC is installed on this computer.\nWould you like to add YAAT server environments to CRC?",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yesButton, noButton },
                    },
                },
            },
        };

        yesButton.Click += (_, _) =>
        {
            userAccepted = true;
            window.Close();
        };
        noButton.Click += (_, _) => window.Close();

        lifetime.MainWindow = window;
        lifetime.Start(Array.Empty<string>());

        if (userAccepted)
        {
            CrcConfigService.Configure();
        }
    }
}
