using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>One airport's row in an active-runways editor: its FAA id, the runway text typed for it and the row's refusal.</summary>
public sealed partial class ActiveRunwaysRow(string airport, string text) : ObservableObject
{
    public string Airport { get; } = airport;

    [ObservableProperty]
    private string _text = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;
}
