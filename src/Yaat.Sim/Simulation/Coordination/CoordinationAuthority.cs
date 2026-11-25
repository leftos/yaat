using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Coordination;

/// <summary>
/// The TCPs a position answers for on the STARS coordination lists: its own, plus every TCP whose airspace currently
/// consolidates under it — the owner the room's consolidation lookup (<c>ArtccConfigResolver.GetConsolidationOwner</c>
/// over the engine's <see cref="Simulation.Attendance"/> and manual overrides) resolves that TCP to. CRC shows a list to
/// every position whose consolidated set holds one of the list's configured TCPs, so a list whose receiver has
/// consolidated into a parent is the parent's to acknowledge. The one rule both judges use: the engine's coordination
/// verbs (<see cref="CoordinationCommandHandler"/>) and the server's F13 entry, which infers a verb before dispatching it.
/// </summary>
public sealed class CoordinationAuthority
{
    private readonly List<Tcp> _answered;

    private CoordinationAuthority(Tcp tcp, List<Tcp> answered)
    {
        Tcp = tcp;
        _answered = answered;
    }

    /// <summary>The acting position's own TCP.</summary>
    public Tcp Tcp { get; }

    /// <summary>The authority <paramref name="identity"/> acts with, or null without a scenario or a TCP for the identity.</summary>
    public static CoordinationAuthority? For(SimulationEngine engine, TrackOwner identity)
    {
        if ((engine.Scenario is not { } scenario) || (FindTcpForIdentity(identity, scenario) is not { } tcp))
        {
            return null;
        }

        return new CoordinationAuthority(tcp, AnsweredFor(engine, scenario, tcp, identity.FacilityId));
    }

    /// <summary>Whether <paramref name="tcp"/> is the position's own or consolidated under it.</summary>
    public bool AnswersFor(Tcp tcp) => _answered.Any(t => SameTcp(t, tcp));

    /// <summary>Whether the position sends on <paramref name="channel"/>.</summary>
    public bool IsSender(CoordinationChannel channel) => channel.SendingTcps.Any(AnswersFor);

    /// <summary>Whether the position receives on <paramref name="channel"/>.</summary>
    public bool IsReceiver(CoordinationChannel channel) => channel.Receivers.Any(r => AnswersFor(r.Tcp));

    /// <summary>Two TCPs are the same when their subset and sector match, the way the coordination config names them.</summary>
    private static bool SameTcp(Tcp a, Tcp b) => (a.Subset == b.Subset) && a.SectorId.Equals(b.SectorId, StringComparison.OrdinalIgnoreCase);

    private static Tcp? FindTcpForIdentity(TrackOwner identity, SimScenarioState scenario)
    {
        Tcp? tcp = TrackResolver.FindTcpForOwner(identity, scenario);
        if (tcp is not null)
        {
            return tcp;
        }

        if ((identity.Subset is not null) && (identity.SectorId is not null))
        {
            return new Tcp(identity.Subset.Value, identity.SectorId, "", null);
        }

        return null;
    }

    /// <summary>
    /// The own TCP plus every TCP of its facility that consolidates under it. The facility is the identity's when its TCP
    /// table holds the TCP, else the student's (a tower position can work a TRACON's TCP).
    /// </summary>
    private static List<Tcp> AnsweredFor(SimulationEngine engine, SimScenarioState scenario, Tcp own, string? identityFacilityId)
    {
        List<Tcp> answered = [own];
        if (scenario.ArtccConfig is not { } config)
        {
            return answered;
        }

        string? facilityId = new[] { identityFacilityId, scenario.StudentPosition?.FacilityId }.FirstOrDefault(f =>
            !string.IsNullOrEmpty(f) && config.GetFacilityTcps(f).Any(t => SameTcp(t, own))
        );
        if (facilityId is null)
        {
            return answered;
        }

        foreach (Tcp tcp in config.GetFacilityTcps(facilityId))
        {
            Tcp? owner = SameTcp(tcp, own)
                ? null
                : config.GetConsolidationOwner(facilityId, tcp, engine.Attendance.IsTcpAttended, engine.ConsolidationState);
            if ((owner is not null) && SameTcp(owner, own))
            {
                answered.Add(tcp);
            }
        }

        return answered;
    }
}
