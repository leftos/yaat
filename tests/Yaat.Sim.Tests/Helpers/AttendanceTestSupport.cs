using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Sets CRC attendance on a bare engine the way the live room does: by issuing the derived
/// <see cref="RecordedAttendanceChange"/> the server writes at the head of a live second, so a test's attendance goes
/// through the same body a replay uses instead of a host stub.
/// </summary>
public static class AttendanceTestSupport
{
    /// <summary>
    /// Attends every vNAS position holding one of <paramref name="tcpCodes"/> in the scenario's ARTCC configuration,
    /// as one record replacing whatever was attended before. Codes are resolved through the scenario's TCP table
    /// (<see cref="TrackResolver.FindTcpByCode(SimScenarioState, string)"/>); an unknown code contributes no ids.
    /// </summary>
    public static void Attend(SimulationEngine engine, params string[] tcpCodes)
    {
        var scenario = engine.Scenario ?? throw new InvalidOperationException("Attend requires a loaded scenario");
        var config = scenario.ArtccConfig ?? throw new InvalidOperationException("Attend requires the scenario's ARTCC config");

        var ids = new List<string>();
        foreach (var code in tcpCodes)
        {
            if (TrackResolver.FindTcpByCode(scenario, code) is not { } tcp)
            {
                continue;
            }

            CollectPositionIds(config.Facility, tcp.Id, ids);
        }

        engine.Actions.IssueDerived(new RecordedAttendanceChange(scenario.ElapsedSeconds, ids));
    }

    private static void CollectPositionIds(FacilityConfig facility, string tcpId, List<string> ids)
    {
        foreach (var position in facility.Positions)
        {
            if (position.StarsConfiguration?.TcpId == tcpId)
            {
                ids.Add(position.Id);
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectPositionIds(child, tcpId, ids);
        }
    }
}
