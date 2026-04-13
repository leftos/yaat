using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class SpeechDebugWindow : Window
{
    private SpeechRecognitionService? _service;

    public SpeechDebugWindow()
    {
        InitializeComponent();

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }

        var clearBtn = this.FindControl<Button>("ClearButton");
        if (clearBtn is not null)
        {
            clearBtn.Click += (_, _) =>
            {
                _service?.SessionHistory.Clear();
            };
        }
    }

    public SpeechDebugWindow(SpeechRecognitionService service, UserPreferences preferences)
        : this()
    {
        _service = service;
        var items = this.FindControl<ItemsControl>("SessionsItemsControl");
        if (items is not null)
        {
            items.ItemsSource = service.SessionHistory;
        }

        new WindowGeometryHelper(this, preferences, "SpeechDebug", 820, 560).Restore();
    }
}
