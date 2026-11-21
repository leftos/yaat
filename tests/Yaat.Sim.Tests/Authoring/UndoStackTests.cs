using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="UndoStack{TState}"/> hands back each earlier snapshot in order: undo returns the state an edit replaced,
/// redo the state undo replaced, a new edit forgets the redo history and the oldest snapshot goes past the depth limit.
/// </summary>
public class UndoStackTests
{
    private sealed record State(string Name);

    [Fact]
    public void UndoRedo_ThroughMixedSequence_ReturnsEachEarlierState()
    {
        var s0 = new State("s0");
        var s1 = new State("s1");
        var s2 = new State("s2");
        var s3 = new State("s3");
        var stack = new UndoStack<State>();
        stack.Record("add stand", s0);
        stack.Record("move vertex", s1);
        stack.Record("edit name", s2);

        Assert.Equal("edit name", stack.NextUndoDescription);
        Assert.Same(s2, stack.Undo(s3));
        Assert.Equal("edit name", stack.NextRedoDescription);
        Assert.Same(s1, stack.Undo(s2));
        Assert.Same(s2, stack.Redo(s1));
        Assert.Same(s1, stack.Undo(s2));
        Assert.Same(s0, stack.Undo(s1));
        Assert.False(stack.CanUndo);
        Assert.Null(stack.NextUndoDescription);
        Assert.Equal("add stand", stack.NextRedoDescription);
        Assert.Same(s1, stack.Redo(s0));
        Assert.Same(s2, stack.Redo(s1));
        Assert.Same(s3, stack.Redo(s2));
        Assert.False(stack.CanRedo);
        Assert.Null(stack.NextRedoDescription);
        Assert.True(stack.CanUndo);
        Assert.Same(s2, stack.Undo(s3));
    }

    [Fact]
    public void Record_ClearsRedo()
    {
        var s0 = new State("s0");
        var s1 = new State("s1");
        var stack = new UndoStack<State>();
        stack.Record("first", s0);
        Assert.Same(s0, stack.Undo(s1));
        Assert.True(stack.CanRedo);

        stack.Record("second", s0);

        Assert.False(stack.CanRedo);
        Assert.Null(stack.Redo(s1));
        Assert.Equal("second", stack.NextUndoDescription);
    }

    [Fact]
    public void MaxDepth_DropsOldest()
    {
        State[] states = [.. Enumerable.Range(0, 6).Select(i => new State($"s{i}"))];
        var stack = new UndoStack<State>(maxDepth: 3);
        for (int i = 0; i < 5; i++)
        {
            stack.Record($"edit {i}", states[i]);
        }

        Assert.Same(states[4], stack.Undo(states[5]));
        Assert.Same(states[3], stack.Undo(states[4]));
        Assert.Same(states[2], stack.Undo(states[3]));
        Assert.False(stack.CanUndo);
        Assert.Null(stack.Undo(states[2]));
    }

    [Fact]
    public void MaxDepthZero_ThrowsArgumentOutOfRange() => Assert.Throws<ArgumentOutOfRangeException>(() => new UndoStack<State>(maxDepth: 0));

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var s0 = new State("s0");
        var s1 = new State("s1");
        var stack = new UndoStack<State>();
        stack.Record("first", s0);
        stack.Record("second", s0);
        Assert.Same(s0, stack.Undo(s1));
        Assert.True(stack.CanUndo);
        Assert.True(stack.CanRedo);

        stack.Clear();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.NextUndoDescription);
        Assert.Null(stack.NextRedoDescription);
    }

    [Fact]
    public void Undo_WhenEmpty_ReturnsNull()
    {
        var current = new State("current");
        var stack = new UndoStack<State>();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.Undo(current));
        Assert.Null(stack.Redo(current));
        Assert.Null(stack.NextUndoDescription);
        Assert.Null(stack.NextRedoDescription);
    }
}
