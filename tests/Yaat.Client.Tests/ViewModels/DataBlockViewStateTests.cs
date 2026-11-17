using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests.ViewModels;

/// <summary>The per-callsign datablock state's lifecycle: a layout or scenario reset clears everything it holds.</summary>
public class DataBlockViewStateTests
{
    [Fact]
    public void DataBlockViewState_Clear_ReleasesTheLocalLocks()
    {
        var state = new ScopeDataBlockViewState();
        state.ToggleDwellLock("UAL238");
        state.ToggleDwellLock("DAL55");

        state.Clear();

        Assert.Empty(state.DwellLockedCallsigns);
    }
}
