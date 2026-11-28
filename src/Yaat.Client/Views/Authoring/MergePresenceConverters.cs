using Avalonia.Data.Converters;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>The merge window's converters from a side's <see cref="ScenarioMergePresence"/> to its cell's added and removed tints.</summary>
public static class MergePresenceConverters
{
    /// <summary>Gets a converter that is true for a value the side added.</summary>
    public static IValueConverter IsAdded { get; } = new FuncValueConverter<ScenarioMergePresence, bool>(p => p == ScenarioMergePresence.Added);

    /// <summary>Gets a converter that is true for a value the side removed.</summary>
    public static IValueConverter IsRemoved { get; } = new FuncValueConverter<ScenarioMergePresence, bool>(p => p == ScenarioMergePresence.Removed);
}
