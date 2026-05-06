using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Regression tests for the Aircraft List "Type" column. The column must show the
/// physical (actual) aircraft type, not the filed flight-plan type.
/// </summary>
public class AircraftListTypeColumnTests
{
    [AvaloniaFact]
    public void TypeColumn_BindsToActualAircraftType_NotFiled()
    {
        var view = new DataGridView();
        var grid = view.GetDataGrid();
        Assert.NotNull(grid);

        var typeCol = grid!.Columns.OfType<DataGridTextColumn>().Single(c => (c.Header as string) == "Type");

        var binding = Assert.IsType<Binding>(typeCol.Binding);
        Assert.Equal(nameof(AircraftModel.AircraftType), binding.Path);
    }

    [AvaloniaFact]
    public void TypeColumn_Binding_ResolvesActualType_WhenFiledIsBlank()
    {
        // Models the N2BP scenario from the S2-OAK-4 bundle: actual SR22, filed blank
        // after instructor amendment cleared the flight plan type.
        var ac = new AircraftModel
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            FiledAircraftType = "",
        };

        var view = new DataGridView();
        var grid = view.GetDataGrid();
        Assert.NotNull(grid);

        var typeCol = grid!.Columns.OfType<DataGridTextColumn>().Single(c => (c.Header as string) == "Type");
        var binding = Assert.IsType<Binding>(typeCol.Binding);

        // Resolve the binding against the model directly via a lightweight TextBlock
        // probe. Avoids DataGrid virtualization entirely.
        var probe = new TextBlock { DataContext = ac };
        probe.Bind(TextBlock.TextProperty, binding);

        Assert.Equal("SR22", probe.Text);
    }
}
