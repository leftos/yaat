using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-658 on the landing rollout: a <c>TAXI … EXP</c> / <c>TAXIAUTO … EXP</c> is exit and taxi without delay, its exit judged at the
/// expedite braking limit (<c>RolloutBraking.NamedExitBrakingLimit(category, expedite: true)</c>) wherever the rollout judges or lists
/// exits, and a stored route the exit cannot join ends the route expedite with it.
/// </summary>
public sealed class GroundExpediteRolloutTests
{
    private const string Arrival = "ARR1";
    private const int RolloutWindowSeconds = 60;

    private readonly ITestOutputHelper _output;

    public GroundExpediteRolloutTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A <c>TAXIAUTO … EXP</c> on the rollout picks its exit from the exits the expedite limit makes: at a second when that limit makes
    /// exits the firm limit cannot, the exit instructed is one of those.
    /// </summary>
    [Theory]
    [InlineData("SFO", "19L", "A320", "TAXIAUTO $1 EXP")]
    [InlineData("OAK", "28R", "B738", "TAXIAUTO 30 EXP")]
    public void TaxiAutoExp_OnTheRollout_PicksFromTheExitsTheExpediteLimitMakes(string airport, string runway, string type, string command)
    {
        if (Touchdown(airport, runway, type) is not { } spawned)
        {
            return;
        }

        (IReadOnlyList<ResolvedExitInfo> onlyAtExpedite, _) = TickUntilAnExitOnlyTheExpediteLimitMakes(spawned);
        CommandResult result = spawned.Engine.SendCommand(Arrival, command);
        _output.WriteLine($"{command} -> {result.Success} {result.Message}");
        Assert.True(result.Success, result.Message);

        ExitPreference exit = spawned.Aircraft.Phases?.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        Assert.Contains(onlyAtExpedite, e => string.Equals(e.TaxiwayName, exit.Taxiway, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A <c>TAXIAUTO</c> without <c>EXP</c> under a standing <c>EXIT {x} EXP</c> keeps the expedite braking limit: it picks from the
    /// exits that limit makes and flies the exit it picked at it, even when the pick is not <c>x</c>. <c>x</c> is the farthest exit
    /// the firm limit makes as well, so the nearer exit only the expedite limit makes is the pick.
    /// </summary>
    [Theory]
    [InlineData("SFO", "19L", "A320", "TAXIAUTO $1")]
    [InlineData("OAK", "28R", "B738", "TAXIAUTO 30")]
    public void TaxiAutoUnderAStandingExitExp_PicksFromTheExpediteLimitExits_AndFliesItsPickAtThatLimit(
        string airport,
        string runway,
        string type,
        string command
    )
    {
        if (Touchdown(airport, runway, type) is not { } spawned)
        {
            return;
        }

        (IReadOnlyList<ResolvedExitInfo> onlyAtExpedite, IReadOnlyList<ResolvedExitInfo> firm) = TickUntilAnExitOnlyTheExpediteLimitMakes(spawned);
        string standing = ((firm.Count > 0) ? firm[^1] : onlyAtExpedite[^1]).TaxiwayName;
        string exitExp = $"EXIT {standing} EXP";
        CommandResult exitResult = spawned.Engine.SendCommand(Arrival, exitExp);
        _output.WriteLine($"{exitExp} -> {exitResult.Success} {exitResult.Message}");
        Assert.True(exitResult.Success, exitResult.Message);

        CommandResult result = spawned.Engine.SendCommand(Arrival, command);
        _output.WriteLine($"{command} -> {result.Success} {result.Message}");
        Assert.True(result.Success, result.Message);

        ExitPreference exit = spawned.Aircraft.Phases?.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        _output.WriteLine($"standing {standing}, picked {exit.Taxiway}");
        Assert.NotEqual(standing, exit.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(onlyAtExpedite, e => string.Equals(e.TaxiwayName, exit.Taxiway, StringComparison.OrdinalIgnoreCase));
        Assert.True(spawned.Aircraft.Ground.IsExpeditingExit, $"the pick {exit.Taxiway} is flown at the firm braking");
    }

    /// <summary>
    /// A joining <c>TAXI</c> without <c>EXP</c> under a standing <c>EXIT {x} EXP</c> keeps the expedite braking limit as the TAXIAUTO
    /// above does: at OAK 28R <c>TAXI C B W W1 30</c> joins C from an exit listed at the expedite limit, and the exit it picks is
    /// flown at that limit even when it is not <c>x</c>. <c>x</c> is the nearest exit only the expedite limit makes; the join is
    /// C1, the one exit that reaches C straight, which the firm limit makes as well.
    /// </summary>
    [Fact]
    public void JoiningTaxiUnderAStandingExitExp_PicksFromTheExpediteLimitExits_FliesItsPickAtTheExpediteLimit()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        (IReadOnlyList<ResolvedExitInfo> onlyAtExpedite, IReadOnlyList<ResolvedExitInfo> firm) = TickUntilAnExitOnlyTheExpediteLimitMakes(spawned);
        List<ResolvedExitInfo> atExpedite = [.. firm, .. onlyAtExpedite];
        string standing = onlyAtExpedite[0].TaxiwayName;
        string exitExp = $"EXIT {standing} EXP";
        CommandResult exitResult = spawned.Engine.SendCommand(Arrival, exitExp);
        _output.WriteLine($"{exitExp} -> {exitResult.Success} {exitResult.Message}");
        Assert.True(exitResult.Success, exitResult.Message);

        const string command = "TAXI C B W W1 30";
        CommandResult result = spawned.Engine.SendCommand(Arrival, command);
        _output.WriteLine($"{command} -> {result.Success} {result.Message}");
        Assert.True(result.Success, result.Message);

        ExitPreference exit = spawned.Aircraft.Phases?.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        _output.WriteLine($"standing {standing}, picked {exit.Taxiway}");
        Assert.NotEqual(standing, exit.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(atExpedite, e => string.Equals(e.TaxiwayName, exit.Taxiway, StringComparison.OrdinalIgnoreCase));
        Assert.True(spawned.Aircraft.Ground.IsExpeditingExit, $"the pick {exit.Taxiway} is flown at the firm braking");
    }

    /// <summary>
    /// A <c>TAXIAUTO</c> without <c>EXP</c> after a <c>TAXIAUTO … EXP</c> restates the clearance at the firm braking limit: the exit
    /// the earlier TAXI implied is not a standing <c>EXIT … EXP</c>, so the new pick comes from the exits the firm limit makes and
    /// is flown without the exit expedite.
    /// </summary>
    [Fact]
    public void TaxiAutoAfterATaxiAutoExp_PicksFromTheFirmLimitExits_AndDropsTheExitExpedite()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        (_, IReadOnlyList<ResolvedExitInfo> firm) = TickUntilAnExitOnlyTheExpediteLimitMakes(spawned);
        Assert.NotEmpty(firm);
        const string withExp = "TAXIAUTO 30 EXP";
        CommandResult expResult = spawned.Engine.SendCommand(Arrival, withExp);
        _output.WriteLine($"{withExp} -> {expResult.Success} {expResult.Message}");
        Assert.True(expResult.Success, expResult.Message);
        Assert.True(spawned.Aircraft.Ground.IsExpeditingExit, "premise: the TAXIAUTO EXP flies its exit at the expedite limit");

        const string command = "TAXIAUTO 30";
        CommandResult result = spawned.Engine.SendCommand(Arrival, command);
        _output.WriteLine($"{command} -> {result.Success} {result.Message}");
        Assert.True(result.Success, result.Message);

        ExitPreference exit = spawned.Aircraft.Phases?.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        _output.WriteLine($"picked {exit.Taxiway}");
        Assert.Contains(firm, e => string.Equals(e.TaxiwayName, exit.Taxiway, StringComparison.OrdinalIgnoreCase));
        Assert.False(spawned.Aircraft.Ground.IsExpeditingExit, $"the pick {exit.Taxiway} is flown with the exit expedite on");
    }

    [Fact]
    public void StoredRouteTheExitCannotJoin_EndsTheRouteExpedite()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        SendUntilAccepted(spawned, "TAXIAUTO 30 EXP");
        Assert.True(aircraft.Ground.IsExpeditingTaxi);

        bool started = GroundCommandHandler.TryStartStoredTaxiRoute(
            CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, aircraft.Ground.Layout!),
            null
        );

        Assert.False(started);
        Assert.False(aircraft.Ground.IsExpeditingTaxi);
    }

    /// <summary>
    /// Ticks the rollout until the expedite limit makes an exit the firm limit cannot; returns those exits and the exits the firm
    /// limit makes at that second, each nearest first. Fails when the limits never differ while the rollout lists exits.
    /// </summary>
    private (IReadOnlyList<ResolvedExitInfo> Extra, IReadOnlyList<ResolvedExitInfo> Firm) TickUntilAnExitOnlyTheExpediteLimitMakes(
        ShortFinalArrival.Spawned spawned
    )
    {
        AircraftState aircraft = spawned.Aircraft;
        for (int second = 0; second < RolloutWindowSeconds; second++)
        {
            LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
            IReadOnlyList<ResolvedExitInfo> firm = landing.MakeableExitsAhead(aircraft, expedite: false) ?? [];
            IReadOnlyList<ResolvedExitInfo> expedite = landing.MakeableExitsAhead(aircraft, expedite: true) ?? [];
            List<ResolvedExitInfo> extra = [.. expedite.Where(e => !firm.Any(f => SameExit(e, f)))];
            string firmNames = string.Join(", ", firm.Select(e => e.TaxiwayName));
            string expediteNames = string.Join(", ", expedite.Select(e => e.TaxiwayName));
            _output.WriteLine($"t+{second} {aircraft.GroundSpeed:F1} kt firm [{firmNames}] expedite [{expediteNames}]");
            if (extra.Count > 0)
            {
                return (extra, firm);
            }

            spawned.Engine.TickOneSecond();
        }

        throw new Xunit.Sdk.XunitException("the expedite limit never made an exit the firm limit could not");
    }

    private static bool SameExit(ResolvedExitInfo a, ResolvedExitInfo b) =>
        string.Equals(a.TaxiwayName, b.TaxiwayName, StringComparison.OrdinalIgnoreCase) && (a.Side == b.Side);

    private ShortFinalArrival.Spawned? Touchdown(string airport, string runway, string type)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand(airport, runway, type, Arrival, 1.0) is not { } spawned)
        {
            return null;
        }

        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
        return spawned;
    }

    private void SendUntilAccepted(ShortFinalArrival.Spawned spawned, string command)
    {
        for (int second = 0; second < RolloutWindowSeconds; second++)
        {
            CommandResult result = spawned.Engine.SendCommand(Arrival, command);
            _output.WriteLine($"t+{second} {command} -> {result.Success} {result.Message}");
            if (result.Success)
            {
                return;
            }

            spawned.Engine.TickOneSecond();
        }

        throw new Xunit.Sdk.XunitException($"{command} was never accepted on the rollout");
    }
}
