using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class FavoriteSetEditorModelTests
{
    [Fact]
    public void MoveUp_SingleItem_SwapsWithPredecessor()
    {
        var list = Favs("A", "B", "C");

        var moved = FavoriteSetEditorModel.MoveUp(list, [2]);

        Assert.Equal(["A", "C", "B"], list.Select(f => f.Label));
        Assert.Equal([1], moved);
    }

    [Fact]
    public void MoveUp_MultiSelection_PreservesRelativeOrder()
    {
        var list = Favs("A", "B", "C", "D");

        var moved = FavoriteSetEditorModel.MoveUp(list, [1, 3]);

        Assert.Equal(["B", "A", "D", "C"], list.Select(f => f.Label));
        Assert.Equal([0, 2], moved);
    }

    [Fact]
    public void MoveUp_BlockAtTop_StaysPut()
    {
        var list = Favs("A", "B", "C");

        var moved = FavoriteSetEditorModel.MoveUp(list, [0, 1]);

        Assert.Equal(["A", "B", "C"], list.Select(f => f.Label));
        Assert.Equal([0, 1], moved);
    }

    [Fact]
    public void MoveDown_SingleItem_SwapsWithSuccessor()
    {
        var list = Favs("A", "B", "C");

        var moved = FavoriteSetEditorModel.MoveDown(list, [0]);

        Assert.Equal(["B", "A", "C"], list.Select(f => f.Label));
        Assert.Equal([1], moved);
    }

    [Fact]
    public void MoveDown_BlockAtBottom_StaysPut()
    {
        var list = Favs("A", "B", "C");

        var moved = FavoriteSetEditorModel.MoveDown(list, [1, 2]);

        Assert.Equal(["A", "B", "C"], list.Select(f => f.Label));
        Assert.Equal([1, 2], moved);
    }

    [Fact]
    public void MoveDown_MultiSelection_PreservesRelativeOrder()
    {
        var list = Favs("A", "B", "C", "D");

        var moved = FavoriteSetEditorModel.MoveDown(list, [0, 2]);

        Assert.Equal(["B", "A", "D", "C"], list.Select(f => f.Label));
        Assert.Equal([1, 3], moved);
    }

    [Fact]
    public void CopyTo_AppendsClones_SourceUnchanged_AndEditsDoNotBleed()
    {
        var source = Favs("A", "B", "C");
        var target = Favs("X");

        FavoriteSetEditorModel.CopyTo(source, target, [0, 2]);

        Assert.Equal(["A", "B", "C"], source.Select(f => f.Label));
        Assert.Equal(["X", "A", "C"], target.Select(f => f.Label));

        target[1].Label = "Edited";
        Assert.Equal("A", source[0].Label);
        Assert.NotSame(source[0], target[1]);
    }

    [Fact]
    public void MoveTo_TransfersReferences_PreservingRelativeOrder()
    {
        var source = Favs("A", "B", "C", "D");
        var target = Favs("X");
        var movedB = source[1];

        FavoriteSetEditorModel.MoveTo(source, target, [3, 1]);

        Assert.Equal(["A", "C"], source.Select(f => f.Label));
        Assert.Equal(["X", "B", "D"], target.Select(f => f.Label));
        Assert.Same(movedB, target[1]);
    }

    [Fact]
    public void OutOfRangeIndices_AreIgnored()
    {
        var source = Favs("A");
        var target = new List<FavoriteCommand>();

        FavoriteSetEditorModel.CopyTo(source, target, [-1, 5]);
        FavoriteSetEditorModel.MoveTo(source, target, [-1, 5]);
        FavoriteSetEditorModel.MoveUp(source, [-1, 5]);
        FavoriteSetEditorModel.MoveDown(source, [-1, 5]);

        Assert.Equal(["A"], source.Select(f => f.Label));
        Assert.Empty(target);
    }

    private static List<FavoriteCommand> Favs(params string[] labels) =>
        labels.Select(l => new FavoriteCommand { Label = l, CommandText = l }).ToList();
}
