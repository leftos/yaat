using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One facility group in the CRC-style controller list: a header (id + name) and its controllers.
/// Collapse state is backed by a shared set so it survives list rebuilds on refresh.
/// </summary>
public sealed partial class ControllerGroupVm : ObservableObject
{
    private readonly HashSet<string> _collapsedFacilities;

    public ControllerGroupVm(
        string facilityId,
        string? facilityName,
        IReadOnlyList<OnlineControllerDto> controllers,
        HashSet<string> collapsedFacilities
    )
    {
        FacilityId = facilityId;
        FacilityName = facilityName;
        Controllers = controllers;
        _collapsedFacilities = collapsedFacilities;
        _isExpanded = !collapsedFacilities.Contains(facilityId);
    }

    public string FacilityId { get; }
    public string? FacilityName { get; }
    public string Header => string.IsNullOrEmpty(FacilityName) ? FacilityId : $"{FacilityId} - {FacilityName}";
    public IReadOnlyList<OnlineControllerDto> Controllers { get; }

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _collapsedFacilities.Remove(FacilityId);
        }
        else
        {
            _collapsedFacilities.Add(FacilityId);
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Online-controller list shown in the Controllers tab: the controllers present in the current
/// room (live CRC clients plus the scenario's auto-connect ATC positions), grouped by facility in
/// CRC style. Refreshed on demand and whenever CRC membership or the loaded scenario changes.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<OnlineControllerDto> OnlineControllers { get; } = [];

    /// <summary>CRC-style facility groups, derived from <see cref="OnlineControllers"/>.</summary>
    public ObservableCollection<ControllerGroupVm> ControllerGroups { get; } = [];

    /// <summary>Facility ids the user has collapsed in the controller list; persists across refreshes.</summary>
    private readonly HashSet<string> _collapsedControllerFacilities = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task RefreshOnlineControllersAsync()
    {
        if (!IsConnected || ActiveRoomId is null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                OnlineControllers.Clear();
                RebuildControllerGroups();
                RebuildActiveTcpOptions();
            });
            return;
        }

        try
        {
            var controllers = await _connection.GetOnlineControllersAsync();
            Dispatcher.UIThread.Post(() =>
            {
                OnlineControllers.Clear();
                foreach (var c in controllers)
                {
                    OnlineControllers.Add(c);
                }

                RebuildControllerGroups();
                RebuildActiveTcpOptions();
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh online controllers");
        }
    }

    private void RebuildControllerGroups()
    {
        ControllerGroups.Clear();
        foreach (var group in OnlineControllers.GroupBy(c => c.FacilityId ?? "").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(c => c.Tcp ?? c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            ControllerGroups.Add(new ControllerGroupVm(group.Key, group.First().FacilityName, ordered, _collapsedControllerFacilities));
        }
    }
}
