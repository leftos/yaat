using Avalonia.Controls;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Lists the aircraft a room export could not restart in the same situation — each with the reason — so the author knows
/// which ones to give preset commands or delete. Copy puts the list on the clipboard as <c>CALLSIGN — reason</c> lines.
/// </summary>
public partial class ScenarioExportReviewWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioExportReviewWindow>();

    private readonly IReadOnlyList<string> _lines;

    /// <summary>Parameterless constructor for the XAML previewer and loader; the app always passes the flags.</summary>
    public ScenarioExportReviewWindow()
        : this([]) { }

    public ScenarioExportReviewWindow(IReadOnlyList<ScenarioExportFlagDto> flags)
    {
        InitializeComponent();
        _lines = FormatLines(flags);

        ListBox? list = this.FindControl<ListBox>("FlagList");
        list?.ItemsSource = _lines;

        Button? copyBtn = this.FindControl<Button>("CopyButton");
        copyBtn?.Click += async (_, _) =>
        {
            try
            {
                IClipboard? clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(string.Join(Environment.NewLine, _lines));
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Copying export review list failed");
            }
        };

        Button? closeBtn = this.FindControl<Button>("CloseButton");
        closeBtn?.Click += (_, _) => Close();
    }

    /// <summary>One <c>CALLSIGN — reason</c> line per flagged aircraft, in the order the export listed them.</summary>
    public static IReadOnlyList<string> FormatLines(IReadOnlyList<ScenarioExportFlagDto> flags) =>
        [.. flags.Select(flag => $"{flag.Callsign} — {flag.Reason}")];
}
