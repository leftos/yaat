using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client mirror of the room's ATPA in-trail pairings
    /// (<see cref="ServerConnection.AtpaResultsChanged"/> and the <c>RoomStateDto.AtpaResults</c>
    /// join seed). Server-authoritative and replaced wholesale on every broadcast.
    /// </summary>
    private List<AtpaPairDto> _atpaResults = [];

    private void OnAtpaResultsChanged(AtpaResultsChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyAtpaResults(dto.Pairs));

    /// <summary>
    /// Replace the ATPA pairing set from a server broadcast or the join-time room-state seed, then
    /// project it onto <see cref="Models.AircraftModel.AtpaLeadCallsign"/> and friends for every
    /// aircraft — the radar renderer reads those per-aircraft fields rather than the pair list, so it
    /// needs no room-level state.
    /// <para>
    /// Only the <b>trailing</b> aircraft of a pairing carries it; the leader is left cleared unless it
    /// trails someone else. Every aircraft is visited, not just the paired ones: a pairing that has
    /// cleared has to blank the fields on aircraft no longer in the list.
    /// </para>
    /// </summary>
    public void ApplyAtpaResults(List<AtpaPairDto>? pairs)
    {
        _atpaResults = pairs ?? [];

        var pairByCallsign = new Dictionary<string, AtpaPairDto>(StringComparer.Ordinal);
        foreach (AtpaPairDto p in _atpaResults)
        {
            pairByCallsign[p.Callsign] = p;
        }

        foreach (AircraftModel ac in Aircraft)
        {
            if (pairByCallsign.TryGetValue(ac.Callsign, out AtpaPairDto? pair))
            {
                ac.AtpaLeadCallsign = pair.LeadCallsign;
                ac.AtpaAllowedSeparationNm = pair.AllowedSeparationNm;
                ac.AtpaConeState = pair.ConeState;
            }
            else
            {
                ac.AtpaLeadCallsign = null;
                ac.AtpaAllowedSeparationNm = 0;
                ac.AtpaConeState = AtpaConeState.Monitor;
            }
        }
    }

    /// <summary>
    /// Re-applies the current ATPA pairing set to a single aircraft. Called when an aircraft is first
    /// added to <see cref="Aircraft"/>, since a pairing broadcast that arrived before the aircraft
    /// existed would otherwise never reach it.
    /// </summary>
    private void SeedAtpaResult(Models.AircraftModel ac)
    {
        foreach (AtpaPairDto p in _atpaResults)
        {
            if (string.Equals(p.Callsign, ac.Callsign, StringComparison.Ordinal))
            {
                ac.AtpaLeadCallsign = p.LeadCallsign;
                ac.AtpaAllowedSeparationNm = p.AllowedSeparationNm;
                ac.AtpaConeState = p.ConeState;
                return;
            }
        }
    }
}
