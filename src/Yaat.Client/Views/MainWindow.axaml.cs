using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
