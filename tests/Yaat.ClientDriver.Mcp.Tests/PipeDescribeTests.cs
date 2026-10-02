extern alias mcp;

using mcp::Yaat.ClientDriver.Mcp.Pipe;
using Xunit;
using McpNodeInfo = mcp::Yaat.Client.Automation.Protocol.NodeInfo;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>The pipe row formatter on the inputs a live host rarely produces.</summary>
public sealed class PipeDescribeTests
{
    [Fact]
    public void Node_WithoutWindowBounds_ReadsOffscreen()
    {
        var node = new McpNodeInfo
        {
            NodeId = 3,
            Type = "Border",
            Name = "Detached",
        };

        string row = PipeDescribe.Node(node, "e5");

        Assert.Equal("e5 | Border | Detached | id=Detached | enabled=True | rect=offscreen", row);
    }
}
