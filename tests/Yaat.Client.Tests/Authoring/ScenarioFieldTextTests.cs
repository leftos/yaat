using System.Collections.ObjectModel;
using Xunit;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioFieldText.UpdateInPlace{T}"/> makes a picker's options hold the rebuilt ones, in order, and says
/// whether any entry changed; options already equal to the rebuilt ones are left alone and raise no change.
/// </summary>
public sealed class ScenarioFieldTextTests
{
    public static TheoryData<string[], string[]> Updates =>
        new()
        {
            { ["A", "B"], ["B", "A"] },
            { ["A", "B"], ["C", "A"] },
            { ["A", "B", "C"], ["A", "B"] },
            { ["A"], ["A", "B", "C"] },
            { ["A", "B"], ["A", "A", "B"] },
            { ["A", "A", "B"], ["B", "A", "A"] },
            { ["A", "B"], ["A", "B"] },
            { [], ["A"] },
            { ["A"], [] },
        };

    [Theory]
    [MemberData(nameof(Updates))]
    public void UpdateInPlace_HoldsTheNextEntries_AndSaysWhetherAnyChanged(string[] current, string[] next)
    {
        var options = new ObservableCollection<string>(current);
        int raised = 0;
        options.CollectionChanged += (_, _) => raised++;
        bool expectChanged = !current.SequenceEqual(next, StringComparer.Ordinal);

        bool changed = ScenarioFieldText.UpdateInPlace(options, next);

        Assert.Equal(next, options);
        Assert.Equal(expectChanged, changed);
        Assert.Equal(expectChanged, raised > 0);
    }
}
