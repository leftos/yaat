using Avalonia.Controls;

namespace Yaat.Client.Views;

public partial class DataGridView : UserControl
{
    public DataGridView()
    {
        InitializeComponent();
    }

    public DataGrid? GetDataGrid()
    {
        return this.FindControl<DataGrid>("AircraftGrid");
    }
}
