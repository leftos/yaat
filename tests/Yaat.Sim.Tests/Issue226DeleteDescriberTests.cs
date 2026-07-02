using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #226: a queued Delete command surfaced as the raw record text
/// "DeleteCommand { }" in the command-line output, because <see cref="DeleteCommand"/>
/// (and its sibling <see cref="CancelAutoDeleteCommand"/>) had no arm in either of
/// <see cref="CommandDescriber"/>'s display-name switches and fell through to
/// <c>command.ToString()</c>. These assert the friendly canonical + natural forms.
/// </summary>
public class Issue226DeleteDescriberTests
{
    [Fact]
    public void Delete_HasFriendlyCanonicalAndNaturalNames()
    {
        var cmd = new DeleteCommand();
        Assert.Equal("DEL", CommandDescriber.DescribeCommand(cmd));
        Assert.Equal("Delete aircraft", CommandDescriber.DescribeNatural(cmd));
    }

    [Fact]
    public void CancelAutoDelete_HasFriendlyCanonicalAndNaturalNames()
    {
        var cmd = new CancelAutoDeleteCommand();
        Assert.Equal("NODEL", CommandDescriber.DescribeCommand(cmd));
        Assert.Equal("Cancel auto-delete", CommandDescriber.DescribeNatural(cmd));
    }
}
