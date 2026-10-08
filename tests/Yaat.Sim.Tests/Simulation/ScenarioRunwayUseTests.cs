using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RunwayScenarioEdits;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// How many of a loaded scenario's aircraft already have a runway, by airport: a runway spawn starting on the ground counts
/// as a departure, an airborne one as an arrival, at its runway's airport; delayed spawns count, aircraft with no runway
/// do not; the primary airport also lists the runways its arrival generators feed. Real scenarios and real nav data — the corpus lives in
/// <c>docs/atctrainer-scenario-examples/</c>.
/// </summary>
public class ScenarioRunwayUseTests
{
    private const string S2Oak4 = "01HG3N8Q5PPR7QXZK33ZPC4D5M.json";
    private const string S3Fat7 = "01HM8ARK79GCPJZG7RW6A0EEKW.json";

    public ScenarioRunwayUseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void OnRunwaySpawns_CountAsDepartures_AtTheirRunwaysAirport_DelayedOnesIncluded()
    {
        // S3-FAT-7 lines up departures at seven airports, most of them delayed spawns. Its twelve FAT-bound arrivals
        // expect the scenario's primary approach (I29RY), so they count at FAT; N320L's `AT BLEAR ELB 30` puts it on
        // MAE 30. The airborne spawns with no destination resolve no approach airport and count nothing.
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(Load(S3Fat7));

        Assert.Equal(
            [
                ("D86", 1, 0, ""),
                ("FAT", 15, 12, ""),
                ("FCH", 2, 0, ""),
                ("MAE", 0, 1, ""),
                ("O32", 2, 0, ""),
                ("PTV", 1, 0, ""),
                ("TLR", 1, 0, ""),
                ("VIS", 2, 0, ""),
            ],
            Sorted(counts)
        );
    }

    [Fact]
    public void OnFinalSpawns_CountAsArrivals_AndParkedAircraftCountNothing()
    {
        // S2-OAK-4: six OnFinal arrivals at OAK (two of them delayed), eleven parked departures (deferred with no ground
        // data) and thirteen airborne spawns away from final, none of which has a runway. N436MS starts on the ramp with
        // `TAXI B 28R`, so it counts as a departure.
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(Load(S2Oak4));

        Assert.Equal([("OAK", 1, 6, "")], Sorted(counts));
    }

    [Fact]
    public void ARunwaySpawnOnTheGround_AndOneOnFinal_CountAtTheSameAirport()
    {
        ScenarioLoadResult result = LoadEdited(S2Oak4, root => AddOnRunwaySpawn(root, "OAK", "28R"));

        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(result);

        Assert.Equal([("OAK", 2, 6, "")], Sorted(counts));
    }

    [Theory]
    [InlineData("TAXI B 30")]
    [InlineData("WAIT 120 TAXI B 30")]
    [InlineData("RWY 28R TAXI B")]
    [InlineData("RWY 28R")]
    public void AGroundPresetNamingARunway_CountsTheAircraftAsADeparture(string preset)
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461TX", "OAK", OakRamp(), ""), preset));

        Assert.Equal([("OAK", 1, 0, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Theory]
    [InlineData("CAPP I28R")]
    [InlineData("CAPP 28R")]
    [InlineData("CTL I28R")]
    [InlineData("JAPP I28R")]
    [InlineData("WAIT 60 CAPP I28R")]
    [InlineData("EF 28R")]
    [InlineData("TG 30")]
    [InlineData("CVA 28L")]
    [InlineData("RWY 28L")]
    public void AnArrivalPresetNamingARunway_CountsTheAircraftAsAnArrival(string preset)
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461AR", "OAK", Airborne(), "KOAK"), preset));

        Assert.Equal([("OAK", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void AnApproachClearanceAtAnotherAirport_CountsThere()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461SJ", "OAK", Airborne(), "KOAK"), "CAPP 30L SJC"));

        Assert.Equal([("SJC", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void PresetsThatNameARunwayWithoutUsingIt_CountNothing()
    {
        // A hold short, a crossing and an expect-approach instruction name a runway the aircraft is not sent to; a bare CAPP
        // with no expected approach and no destination runway resolves nothing, as the command itself would.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                Add(root, Aircraft("N461HS", "OAK", OakRamp(), ""), "TAXI B HS 28R", "HS 30", "CROSS 28L");
                Add(root, Aircraft("N461EX", "OAK", Airborne(), "KOAK"), "EAPP I28R");
                Add(root, Aircraft("N461BC", "OAK", Airborne(), "KOAK"), "CAPP");
            }
        );

        Assert.Empty(ScenarioRunwayUse.CountAssigned(result));
    }

    [Fact]
    public void ARunwaySpawnWithRunwayPresets_CountsOnce()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461RS", "OAK", OnRunway("28R"), ""), "TAXI B 30", "CTO"));

        Assert.Equal([("OAK", 1, 0, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void AnUnparseablePresetAndAnUnknownRunway_AreSkipped_AndTheNextPresetStillCounts()
    {
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root => Add(root, Aircraft("N461UN", "OAK", Airborne(), "KOAK"), "THIS IS NOT A COMMAND", "EF 99", "CAPP I99", "EF 30")
        );

        Assert.Equal([("OAK", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void ADeferredAircraft_CountsNothing_WhateverItsPresets()
    {
        // Runway 99 does not exist at OAK, so the loader defers the spawn; its presets would name 30 and 28R.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root => Add(root, Aircraft("N461DF", "OAK", OnRunway("99"), "KOAK"), "TAXI B 30", "CAPP I28R")
        );

        Assert.Contains(result.DeferredAircraft, loaded => loaded.State.Callsign == "N461DF");
        Assert.Empty(ScenarioRunwayUse.CountAssigned(result));
    }

    [Fact]
    public void GeneratorRunways_ListAtThePrimaryOnly_WithASecondAirportPresent()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak234, root => AddOnRunwaySpawn(root, "SJC", "30L"));

        Assert.Equal([("OAK", 0, 0, "28R 30"), ("SJC", 1, 0, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void AnExpectedApproach_CountsTheAircraftAsAnArrival_WithNoPreset()
    {
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                JsonObject arrival = Aircraft("N461XA", "OAK", Airborne(), "KOAK");
                arrival["expectedApproach"] = "I28R";
                Add(root, arrival);
            }
        );

        Assert.Equal([("OAK", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void ThePrimaryApproach_CountsOnlyTheAircraftBoundForThePrimary()
    {
        // SFO also has an ILS 28R; the scenario's primary approach is meant for OAK only.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                root["primaryApproach"] = "I28R";
                Add(root, Aircraft("N461PO", "OAK", Airborne(), "KOAK"));
                Add(root, Aircraft("N461PS", "OAK", Airborne(), "KSFO"));
            }
        );

        Assert.Equal([("OAK", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void ABareApproachClearance_TakesTheExpectedApproachsRunway()
    {
        // The SFO-bound aircraft expects ILS 28R there; a bare CAPP resolves to it, as the command would. The read names
        // 28R twice: once for the expected approach, once for the clearance itself.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                JsonObject arrival = Aircraft("N461BC", "OAK", Airborne(), "KSFO");
                arrival["expectedApproach"] = "I28R";
                Add(root, arrival, "CAPP");
            }
        );

        Assert.Equal(["A28R", "A28R"], SignalTokens(result.ImmediateAircraft.Single(loaded => loaded.State.Callsign == "N461BC")));
        Assert.Equal([("SFO", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void AnAirborneRunwayAssignment_IsAnArrival()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461AA", "OAK", Airborne(), "KOAK"), "RWY 28L"));

        Assert.Equal(["A28L"], SignalTokens(Loaded(result, "N461AA")));
    }

    [Fact]
    public void AnExpectApproachThatDoesNotResolve_LeavesTheExpectedApproachInPlace()
    {
        // I99 is no approach at OAK, so the aircraft keeps expecting I28R and the bare CAPP takes it.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                JsonObject arrival = Aircraft("N461E9", "OAK", Airborne(), "KOAK");
                arrival["expectedApproach"] = "I28R";
                Add(root, arrival, "EAPP I99", "CAPP");
            }
        );

        Assert.Equal(["A28R", "A28R"], SignalTokens(Loaded(result, "N461E9")));
    }

    [Fact]
    public void ADestinationChange_FromNoDestination_KeepsTheExpectedApproach()
    {
        // As in live play, APT drops the expected approach only when it changes an existing destination; this aircraft had
        // none, so its expected I28R still names the runway the bare CAPP takes at OAK.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                JsonObject arrival = Aircraft("N461AP", "OAK", Airborne(), "");
                arrival["expectedApproach"] = "I28R";
                Add(root, arrival, "APT OAK", "CAPP");
            }
        );

        Assert.Equal(["A28R"], SignalTokens(Loaded(result, "N461AP")));
    }

    [Fact]
    public void AnExpectApproachPreset_ThenABareApproachClearance_CountsTheExpectedRunway()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak1, root => Add(root, Aircraft("N461EC", "OAK", Airborne(), "KOAK"), "EAPP I28R", "CAPP"));

        Assert.Equal(["A28R"], SignalTokens(result.ImmediateAircraft.Single(loaded => loaded.State.Callsign == "N461EC")));
        Assert.Equal([("OAK", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void APatternEntry_ResolvesItsRunwayAtTheDestination_NotTheSpawnAirport()
    {
        // The pattern rule: assigned runway, then destination, then the spawn's airport. N461HW spawns at OAK bound for HWD;
        // N461MR has no airport of its own (the loader gives it the primary, OAK) and is bound for MRY.
        ScenarioLoadResult result = LoadEdited(
            S1Oak1,
            root =>
            {
                Add(root, Aircraft("N461HW", "OAK", Airborne(), "KHWD"), "EF 28L");
                JsonObject monterey = Aircraft("N461MR", "OAK", Airborne(), "KMRY");
                monterey.Remove("airportId");
                monterey["flightplan"]!["departure"] = "KMMH";
                Add(root, monterey, "EF 28L");
            }
        );

        Assert.Equal([("HWD", 0, 1, ""), ("MRY", 0, 1, "")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    [Fact]
    public void NoAircraftWithARunway_AndNoGenerator_LeavesEveryAirportAbsent() =>
        // S1-OAK-1 is all parked departures and has no arrival generator.
        Assert.Empty(ScenarioRunwayUse.CountAssigned(Load(S1Oak1)));

    [Fact]
    public void ArrivalGenerators_ListTheirRunwaysAtThePrimary_WithNoAircraftCounted()
    {
        // S1-OAK-234: parked departures only, plus arrival generators on 30 and 28R. OAK is present on its generators alone.
        Assert.Equal([("OAK", 0, 0, "28R 30")], Sorted(ScenarioRunwayUse.CountAssigned(Load(S1Oak234))));

        // A generator whose runway does not resolve is skipped; the other one still lists its runway.
        ScenarioLoadResult withUnknown = LoadEdited(S1Oak234, root => root["aircraftGenerators"]!.AsArray()[0]!["runway"] = "99");

        Assert.Equal([("OAK", 0, 0, "28R")], Sorted(ScenarioRunwayUse.CountAssigned(withUnknown)));
    }

    [Fact]
    public void ArrivalGenerators_AddTheirRunways_ToThePrimarysAircraftCounts()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak234, root => AddOnRunwaySpawn(root, "OAK", "28L"));

        Assert.Equal([("OAK", 1, 0, "28R 30")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    private static (string Airport, int Departures, int Arrivals, string GeneratorRunways)[] Sorted(
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts
    ) =>
        [
            .. counts
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value.Departures, entry.Value.Arrivals, string.Join(' ', entry.Value.GeneratorArrivalRunways))),
        ];
}
