namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>Every Scope driven by this view model: the open Scope, and none while no Scope is open.</summary>
    public IEnumerable<ScopeViewModel> AllScopeViews => (OpenScope is { } scope) ? [scope] : [];
}
