extern alias mcp;

using System.Windows.Automation;

using mcp::Yaat.ClientDriver.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The element registry's id space: UI Automation elements and client pipe nodes draw from one sequence, an id resolves
/// back to the ref it names, and a UI Automation tool refuses an id that belongs to the pipe.
/// </summary>
public sealed class ElementRegistryTests
{
    [Fact]
    public void UiaAndPipeIds_ShareOneSequence()
    {
        ElementRegistry registry = NewRegistry();

        string firstPipeId = registry.Register(4242, 1);
        string uiaId = registry.Register(AutomationElement.RootElement);
        string secondPipeId = registry.Register(4242, 2);

        Assert.Equal("e1", firstPipeId);
        Assert.Equal("e2", uiaId);
        Assert.Equal("e3", secondPipeId);
        Assert.Equal(new PipeNodeRef(4242, 1), registry.Resolve(firstPipeId));
        Assert.IsType<UiaElementRef>(registry.Resolve(uiaId));
    }

    [Fact]
    public void SamePipeNode_ReusesItsId()
    {
        ElementRegistry registry = NewRegistry();

        string first = registry.Register(7, 42);
        string second = registry.Register(7, 42);

        Assert.Equal(first, second);
        Assert.Equal("e1", first);
    }

    [Fact]
    public void DifferentPidSameNode_GetsANewId()
    {
        ElementRegistry registry = NewRegistry();

        string first = registry.Register(7, 42);
        string second = registry.Register(8, 42);

        Assert.NotEqual(first, second);
        Assert.Equal(new PipeNodeRef(7, 42), registry.Resolve(first));
        Assert.Equal(new PipeNodeRef(8, 42), registry.Resolve(second));
    }

    [Fact]
    public void Resolve_UnknownId_Throws()
    {
        ElementRegistry registry = NewRegistry();

        McpException failure = Assert.Throws<McpException>(() => registry.Resolve("e99"));

        Assert.Equal("Unknown element id 'e99' — ids come from find_elements/dump_tree and die with the server process", failure.Message);
    }

    [Fact]
    public void ResolveUia_GivenAPipeId_RefusesWithTheNotYetMessage()
    {
        ElementRegistry registry = NewRegistry();
        string id = registry.Register(4242, 1);

        McpException failure = Assert.Throws<McpException>(() => registry.ResolveUia(id));

        Assert.Equal($"Element '{id}' belongs to a YAAT client driven over its automation pipe; this tool cannot use it yet.", failure.Message);
    }

    private static ElementRegistry NewRegistry() => new(NullLogger<ElementRegistry>.Instance);
}
