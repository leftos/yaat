using Avalonia.Controls;
using Yaat.ViewModels;

namespace Yaat.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
