using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope as one control: its bar, its view and its views dock, over the <see cref="ViewModels.ScopeViewModel"/>
/// in its DataContext. The main window's Scope tab and the Scope window each build their own host over the same view model,
/// never both at once, since a control has one parent. The bar slot shows the bar <see cref="ScopeViewModel.BarKind"/>
/// names, so the bar follows the room's profile kind.
/// </summary>
public partial class ScopeHost : UserControl
{
    private const string NoBarMessage = "The Scope host has no bar until its DataContext is a ScopeViewModel";

    private ScopeViewModel? _viewModel;
    private ScopeBarKind? _builtFor;

    public ScopeHost()
    {
        InitializeComponent();
    }

    /// <summary>The Scope's view: its canvas and its key handling.</summary>
    internal ScopeView ScopeView => View;

    /// <summary>The Scope's bar: the one shown in its bar slot.</summary>
    internal ScopeToolbar ScopeToolbar => (BarSlot.Content as ScopeToolbar) ?? throw new InvalidOperationException(NoBarMessage);

    /// <summary>The Scope's views dock.</summary>
    internal Dock.ScopeDock ScopeDock => Dock;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ScopeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        if (_viewModel is { } viewModel)
        {
            ShowBar(viewModel.BarKind);
        }
    }

    /// <summary>Shows the bar for the view model's bar kind when that is the property that changed.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(ScopeViewModel.BarKind)) && (_viewModel is { } viewModel))
        {
            ShowBar(viewModel.BarKind);
        }
    }

    /// <summary>
    /// Shows the bar for <paramref name="kind"/> in the slot; a kind whose bar type is already shown keeps that instance.
    /// </summary>
    private void ShowBar(ScopeBarKind kind)
    {
        if ((_builtFor == kind) && (BarSlot.Content is not null))
        {
            return;
        }

        if (BarSlot.Content?.GetType() != BarTypeFor(kind))
        {
            BarSlot.Content = BuildBar(kind);
        }

        _builtFor = kind;
    }

    /// <summary>The type of the bar a Scope of <paramref name="kind"/> shows.</summary>
    private static Type BarTypeFor(ScopeBarKind kind) =>
        kind switch
        {
            ScopeBarKind.Classic => typeof(ScopeToolbar),
            ScopeBarKind.Stars => typeof(ScopeToolbar),
            ScopeBarKind.Eram => typeof(ScopeToolbar),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope bar kind"),
        };

    /// <summary>The bar a Scope of <paramref name="kind"/> shows.</summary>
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "A kind builds whatever bar control it names, and each kind names the same control"
    )]
    private static Control BuildBar(ScopeBarKind kind) =>
        kind switch
        {
            ScopeBarKind.Classic => new ScopeToolbar(),
            ScopeBarKind.Stars => new ScopeToolbar(),
            ScopeBarKind.Eram => new ScopeToolbar(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope bar kind"),
        };
}
