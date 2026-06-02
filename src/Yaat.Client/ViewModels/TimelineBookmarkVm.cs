using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.ViewModels;

/// <summary>
/// A user-authored timeline bookmark shown as a gold tick above the rewind slider and
/// listed in the Bookmarks dropdown. Clicking the tick or a list row rewinds the simulation
/// to <see cref="TimeSeconds"/>. Unlike <see cref="TimelineMarkerVm"/> (the auto-generated
/// Finding/Command ticks) the <see cref="Name"/> is user-editable, so this is an
/// <see cref="ObservableObject"/> rather than an init-only record.
///
/// Per-item Rename/Delete/Jump live as commands on this VM (delegating to the owning
/// <see cref="MainViewModel"/> via injected callbacks) so the rail context menu and the
/// dropdown rows — both rendered in popups, where <c>$parent</c> ancestor bindings are
/// unreliable — can bind to <c>{Binding RenameCommand}</c> against the item itself.
/// </summary>
public partial class TimelineBookmarkVm : ObservableObject
{
    public required string Id { get; init; }
    public required double TimeSeconds { get; init; }

    public required Action<TimelineBookmarkVm> RenameRequested { private get; init; }
    public required Action<TimelineBookmarkVm> DeleteRequested { private get; init; }
    public required Func<TimelineBookmarkVm, Task> JumpRequested { private get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _name;

    public string TimeText => TimeSpan.FromSeconds(TimeSeconds).ToString(@"h\:mm\:ss");

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Bookmark {TimeText}" : Name!;

    public string ListLabel => string.IsNullOrWhiteSpace(Name) ? TimeText : $"{TimeText}  {Name}";

    public string ToolTipText => $"{TimeText} — {DisplayName}";

    public double MarkerHeight => 8;
    public double MarkerWidth => 3;

    public IBrush FillBrush => BookmarkBrush;

    private static readonly IBrush BookmarkBrush = new SolidColorBrush(Color.FromRgb(255, 200, 40));

    [RelayCommand]
    private void Rename()
    {
        RenameRequested(this);
    }

    [RelayCommand]
    private void Delete()
    {
        DeleteRequested(this);
    }

    [RelayCommand]
    private async Task Jump()
    {
        await JumpRequested(this);
    }
}
