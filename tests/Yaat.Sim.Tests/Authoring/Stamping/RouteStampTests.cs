using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Stamping;

/// <summary>
/// <see cref="RouteStamp.Apply(ScenarioAircraft, StampRoute, StampProfile, StampContext)"/> writes a profile onto a copy of an
/// aircraft and reports every change and warning, on real navdata, CIFP and aircraft tables.
/// </summary>
public class RouteStampTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTime ModelDate = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly HashSet<string> NoCallsigns = [];

    public RouteStampTests()
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
        Yaat.Sim.Testing.TestVnasData.EnsureAircraftDataInitialized();
    }

    [Fact]
    public void Apply_InputAircraft_IsUnchanged()
    {
        ScenarioAircraft aircraft = Golden("B738");
        aircraft.PresetCommands.Add(Preset("p1", "CM 50", 10));
        JsonNode? before = JsonSerializer.SerializeToNode(aircraft);
        StampProfile profile = Everything() with
        {
            Pools = new StampPools
            {
                AircraftTypes = ["A320"],
                CallsignPrefixes = ["DAL"],
                Runways = ["28L"],
            },
        };

        StampResult result = RouteStamp.Apply(aircraft, Route("SFO MOD"), profile, Context(1));

        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(aircraft)));
        Assert.NotSame(aircraft, result.Aircraft);
        Assert.NotEmpty(result.Changes);
    }

    [Fact]
    public void Apply_GoldenProfile_ChangeListIsTheDiff()
    {
        StampProfile profile = new()
        {
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                CruiseAltitude = 35000,
                ApplyRoute = true,
            },
            StartingConditions = new StampStartingConditions
            {
                Apply = true,
                Altitude = 7000,
                ApplyNavigationPath = true,
            },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(1));

        StampChange[] expected =
        [
            new("/startingConditions/altitude", DiffKind.Changed, "5000", "7000"),
            new("/startingConditions/navigationPath", DiffKind.Changed, "null", "\"SFO MOD\""),
            new("/flightplan/cruiseAltitude", DiffKind.Changed, "33000", "35000"),
            new("/flightplan/route", DiffKind.Changed, "\"OLD\"", "\"SFO MOD\""),
        ];
        Assert.Equal(expected, result.Changes);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void Apply_ProfileThatChangesNothing_GivesAnEmptyChangeList()
    {
        StampProfile profile = new()
        {
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                CruiseAltitude = 33000,
                Destination = "KLAX",
            },
            StartingConditions = new StampStartingConditions { Apply = true, Altitude = 5000 },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(1));

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Apply_Random42_PoolPicksAreStable()
    {
        StampProfile profile = new()
        {
            Pools = new StampPools { AircraftTypes = ["B738", "A320", "B77W"], CallsignPrefixes = ["UAL", "DAL"] },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(42));

        Assert.Equal(("B77W", "UAL1256"), (result.Aircraft.AircraftType, result.Aircraft.AircraftId));
    }

    [Fact]
    public void Apply_FixedContext_IsByteIdentical()
    {
        StampProfile profile = Everything() with
        {
            Pools = new StampPools
            {
                AircraftTypes = ["B738", "A320"],
                CallsignPrefixes = ["UAL"],
                Runways = ["28L", "28R"],
            },
        };

        StampResult first = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(5));
        StampResult second = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(5));

        static string Text(StampResult r) => JsonSerializer.Serialize(r.Aircraft, ScenarioLoader.JsonOptions) + JsonSerializer.Serialize(r.Changes);
        Assert.Equal(Text(first), Text(second));
        Assert.Equal(ScenarioIdGenerator.Create(FixedNow, new Random(IdSeed)), Assert.Single(first.Aircraft.PresetCommands).Id);
    }

    [Fact]
    public void Apply_NoPool_DrawsNothingRandom()
    {
        var context = new StampContext(FixedNow, new ThrowingRandom(), new Random(IdSeed), NoCallsigns, ModelDate);

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), Everything(), context);

        Assert.Equal("UAL1", result.Aircraft.AircraftId);
        Assert.Equal("B738", result.Aircraft.AircraftType);
    }

    [Theory]
    [InlineData("A320", "A320/L")]
    [InlineData("H/B744", "H/B744/L")]
    [InlineData("B744/", "B744/L")]
    public void Apply_PoolType_KeepsTheFiledEquipmentSuffix(string pick, string stamped)
    {
        StampProfile profile = new() { Pools = new StampPools { AircraftTypes = [pick] } };

        StampResult result = RouteStamp.Apply(Golden("B738/L"), Route("SFO MOD"), profile, Context(1));

        Assert.Equal(stamped, result.Aircraft.AircraftType);
        Assert.Equal(stamped, result.Aircraft.FlightPlan!.AircraftType);
    }

    [Fact]
    public void Apply_BlankPoolEntry_IsSkippedWithAWarning()
    {
        StampProfile profile = new() { Pools = new StampPools { AircraftTypes = [" ", "A320", ""] } };

        foreach (int seed in (int[])[1, 2, 3, 4, 5])
        {
            StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(seed));

            Assert.Equal("A320", result.Aircraft.AircraftType);
            Assert.Contains("Pool aircraft types has a blank entry; skipped", result.Warnings);
        }
    }

    [Fact]
    public void Apply_CallsignsExhausted_KeepsTheCallsignWithAWarning()
    {
        HashSet<string> taken = [.. Enumerable.Range(1, 9999).Select(n => $"DAL{n}")];
        StampProfile profile = new() { Pools = new StampPools { CallsignPrefixes = ["DAL"] } };
        var context = new StampContext(FixedNow, new Random(1), new Random(IdSeed), taken, ModelDate);

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, context);

        Assert.Equal("UAL1", result.Aircraft.AircraftId);
        Assert.Contains(result.Warnings, w => w.StartsWith("No free callsign", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_HeavyOnly_StampsAHeavy()
    {
        StampResult result = RouteStamp.Apply(Golden("B77W"), Route("SFO MOD"), HeavyOnly(), Context(1));

        Assert.Null(result.SkipReason);
        Assert.Equal(36000, result.Aircraft.FlightPlan!.CruiseAltitude);
    }

    [Theory]
    [InlineData("B738", "Large")]
    [InlineData("A388", "Super")]
    public void Apply_HeavyOnly_SkipsAnotherWakeClass(string type, string wakeClass)
    {
        StampResult result = RouteStamp.Apply(Golden(type), Route("SFO MOD"), HeavyOnly(), Context(1));

        Assert.NotNull(result.SkipReason);
        Assert.Contains(wakeClass, result.SkipReason);
        Assert.Empty(result.Changes);
        Assert.Equal(33000, result.Aircraft.FlightPlan!.CruiseAltitude);
    }

    [Fact]
    public void Apply_RouteWarnings_AreCarried()
    {
        var route = new StampRoute("test", "SFO MOD", ["Route test: Point 2: moved"]);

        StampResult result = RouteStamp.Apply(Golden("B738"), route, RouteOnly(), Context(1));

        Assert.Contains("Route test: Point 2: moved", result.Warnings);
    }

    [Fact]
    public void Apply_Alias_ReplacesOnlyWholeStarTokens()
    {
        (string from, string to) = TwoKsfoStars();
        string longer = from + "9";
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to), new StarAlias("BAD STAR", to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{longer} {from}"), profile, Context(1));

        Assert.Equal($"{longer} {to}", result.Aircraft.FlightPlan!.Route);
        Assert.Contains(result.Warnings, w => w.Contains("BAD STAR", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_AliasToStarWithoutTheTransition_CutsBackToTheBareStar()
    {
        (string from, string transition, string to) = StarPairWithoutTransition();
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{from}.{transition}"), profile, Context(1));

        Assert.Equal(to, result.Aircraft.FlightPlan!.Route);
        Assert.Contains(result.Warnings, w => w.Contains(transition, StringComparison.Ordinal) && w.Contains(to, StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_AliasToStarNotInNavdata_IsAnErrorAndKeepsTheOldRoute()
    {
        (string from, string to) = TwoKsfoStars();
        const string Missing = "QQZZX7";
        Assert.Null(NavigationDatabase.Instance.ResolveStarId(Missing));
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to), new StarAlias(to + "X", Missing)] };
        string route = $"LAX {from} {to}X";

        StampResult result = RouteStamp.Apply(Golden("B738"), Route(route), profile, Context(1));

        Assert.Equal("OLD", result.Aircraft.FlightPlan!.Route);
        string warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Error:", warning, StringComparison.Ordinal);
        Assert.Contains(Missing, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UnpublishedFixBeforeTheStar_Warns()
    {
        (string from, string to) = TwoKsfoStars();
        Assert.DoesNotContain("LAX", StarFixes(to));
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"LAX {from}"), profile, Context(1));

        Assert.Contains($"LAX before STAR {to} is not one of its entry or transition fixes", result.Warnings);
    }

    [Fact]
    public void Apply_FixMidStar_WarnsMidProcedure()
    {
        (string from, string to, string mid) = StarWithMidBodyFix();
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{mid} {from}"), profile, Context(1));

        Assert.Contains($"{mid} joins STAR {to} mid-procedure", result.Warnings);
    }

    [Fact]
    public void Apply_FixAtTheStarEntry_DoesNotWarn()
    {
        (string from, string to, _) = StarWithMidBodyFix();
        string entry = NavigationDatabase.Instance.GetStarBody(to)![0].ToUpperInvariant();
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{entry} {from}"), profile, Context(1));

        Assert.DoesNotContain(result.Warnings, w => w.Contains($"STAR {to}", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_FixOffTheKnownTransition_Warns()
    {
        (string from, string to, string transition, string fix) = StarTransitionAndFixOffIt();
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{fix} {from}.{transition}"), profile, Context(1));

        Assert.Equal($"{fix} {to}.{transition}", result.Aircraft.FlightPlan!.Route);
        Assert.Contains($"{fix} before STAR {to} is not on its {transition} transition", result.Warnings);
    }

    [Fact]
    public void Apply_SidOfAnotherAirport_Warns()
    {
        string sid = ProcedureNotAt(NavigationDatabase.Instance.GetSids("KOAK").Select(s => s.ProcedureId), "KSFO", isSid: true);
        StampProfile profile = Cruise(35000) with { FlightPlan = Cruise(35000).FlightPlan with { Departure = "KSFO" } };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{sid} MOD"), profile, Context(1));

        Assert.Contains($"SID {sid} is not a procedure of the departure airport KSFO", result.Warnings);
    }

    [Fact]
    public void Apply_StarOfAnotherAirport_Warns()
    {
        string star = ProcedureNotAt(NavigationDatabase.Instance.GetStars("KLAX").Select(s => s.ProcedureId), "KSFO", isSid: false);
        StampProfile profile = Cruise(35000) with { FlightPlan = Cruise(35000).FlightPlan with { Destination = "KSFO" } };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"MOD {star}"), profile, Context(1));

        Assert.Contains($"STAR {star} is not a procedure of the destination airport KSFO", result.Warnings);
    }

    [Fact]
    public void Apply_FaaDepartureId_FindsItsSid()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string sid = navDb.GetSids("KSFO").Select(s => s.ProcedureId).First(id => navDb.GetSidBody(id) is not null);
        StampProfile profile = Cruise(35000) with { FlightPlan = Cruise(35000).FlightPlan with { Departure = "SFO" } };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{sid} MOD"), profile, Context(1));

        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("SID", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_FrdToken_IsNoProcedure()
    {
        StampProfile profile = Cruise(35000) with { FlightPlan = Cruise(35000).FlightPlan with { Departure = "KSFO", Destination = "KLAX" } };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO090010 OAK270015 MOD"), profile, Context(1));

        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("SID", StringComparison.Ordinal) || w.StartsWith("STAR", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_NavdataNotLoaded_SkipsTheSidAndStarChecksWithAWarning()
    {
        string sid = ProcedureNotAt(NavigationDatabase.Instance.GetSids("KOAK").Select(s => s.ProcedureId), "KSFO", isSid: true);
        StampProfile profile = Cruise(35000) with { FlightPlan = Cruise(35000).FlightPlan with { Departure = "KSFO" } };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"{sid} MOD"), profile, Context(1), navDb: null);

        Assert.Contains("Navdata is not loaded; SID and STAR checks skipped", result.Warnings);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("SID", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_NavdataNotLoadedWithAliases_ShowsOneNavdataLine()
    {
        (string from, string to) = TwoKsfoStars();
        StampProfile profile = RouteOnly() with { StarAliases = [new StarAlias(from, to)] };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route($"SFO {from}"), profile, Context(1), navDb: null);

        string line = Assert.Single(result.Warnings, w => w.StartsWith("Navdata", StringComparison.Ordinal));
        Assert.Equal("Navdata is not loaded; SID and STAR checks skipped", line);
        Assert.Equal($"SFO {to}", result.Aircraft.FlightPlan!.Route);
    }

    [Fact]
    public void Apply_EastboundIfrAtEvenCruise_Warns()
    {
        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), Cruise(34000), Context(1));

        Assert.Contains(result.Warnings, w => w.Contains("(7110.65 §4-5-2; 14 CFR 91.179)", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_WestboundIfrAtFl350_Warns()
    {
        StampResult result = RouteStamp.Apply(Golden("B738"), Route("MOD SFO"), Cruise(35000), Context(1));

        Assert.Contains(result.Warnings, w => w.Contains("91.179", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_WestboundIfrAtFl360_DoesNotWarn()
    {
        StampResult result = RouteStamp.Apply(Golden("B738"), Route("MOD SFO"), Cruise(36000), Context(1));

        Assert.DoesNotContain(result.Warnings, w => w.Contains("91.179", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(45000, false)]
    [InlineData(43000, true)]
    public void Apply_EastboundAboveFl410_FollowsTheFourThousandFootSteps(int cruise, bool warns)
    {
        StampResult result = RouteStamp.Apply(Golden("B77W"), Route("SFO MOD"), Cruise(cruise), Context(1));

        Assert.Equal(warns, result.Warnings.Any(w => w.Contains("91.179", StringComparison.Ordinal)));
    }

    [Fact]
    public void IfrAltitudeFits_CourseThatRoundsTo180_IsWestbound()
    {
        Assert.False(StampChecks.IfrAltitudeFits(35000, 179.6));
        Assert.True(StampChecks.IfrAltitudeFits(36000, 179.6));
        Assert.True(StampChecks.IfrAltitudeFits(35000, 179.4));
    }

    [Theory]
    [InlineData("SFO MOD", 6000, true)]
    [InlineData("SFO MOD", 7500, false)]
    [InlineData("MOD SFO", 4500, false)]
    [InlineData("MOD SFO", 5500, true)]
    [InlineData("SFO MOD", 2500, false)]
    [InlineData("SFO MOD", 18500, false)]
    public void Apply_VfrCruise_FollowsTheHemisphericRule(string route, int cruise, bool warns)
    {
        StampResult result = RouteStamp.Apply(Golden("C172"), Route(route), Vfr(cruise), Context(1));

        Assert.Equal(warns, result.Warnings.Any(w => w.Contains("91.159", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(17500, false)]
    [InlineData(18000, true)]
    [InlineData(18500, true)]
    public void Apply_VfrCruiseInClassA_Warns(int cruise, bool warns)
    {
        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), Vfr(cruise), Context(1));

        Assert.Equal(warns, result.Warnings.Contains($"VFR cruise {cruise} ft is in Class A (14 CFR 91.135)"));
    }

    [Fact]
    public void Apply_CruiseAboveTheCeiling_Warns()
    {
        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), Cruise(45000), Context(1));

        Assert.Contains(result.Warnings, w => w.StartsWith("Cruise 45000 ft is above the B738 ceiling", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StartingConditionKinds.FixOrFrd, true)]
    [InlineData(StartingConditionKinds.Parking, false)]
    public void Apply_StartAboveTheCruise_WarnsWhenAirborne(string kind, bool warns)
    {
        StampProfile profile = Cruise(10000) with
        {
            StartingConditions = new StampStartingConditions
            {
                Apply = true,
                Type = kind,
                Altitude = 12000,
            },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(1));

        Assert.Equal(warns, result.Warnings.Any(w => w.StartsWith("The airborne start at 12000 ft", StringComparison.Ordinal)));
    }

    [Fact]
    public void Apply_ReplacePresets_RemovesEveryPresetAndExpandsTokensFromTheStampedAircraft()
    {
        ScenarioAircraft aircraft = Golden("B738");
        aircraft.PresetCommands.Add(Preset("p1", "CM 50", 10));
        aircraft.PresetCommands.Add(Preset("p2", "SPD 210", 20));

        StampResult result = RouteStamp.Apply(aircraft, Route("MOD SFO"), ReplacePresets(), Context(1));

        PresetCommand preset = Assert.Single(result.Aircraft.PresetCommands);
        Assert.Equal("SAY UAL1 MOD SFO 36000", preset.Command);
        Assert.Equal(5, preset.TimeOffset);
        StampChange[] removed = [.. result.Changes.Where(c => c.Kind == DiffKind.Removed)];
        Assert.Equal(["/presetCommands/0", "/presetCommands/1"], removed.Select(c => c.Path));
        Assert.Contains("CM 50", removed[0].OldValue, StringComparison.Ordinal);
        Assert.Contains("SPD 210", removed[1].OldValue, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => (c.Kind == DiffKind.Added) && (c.Path == "/presetCommands/0"));
    }

    [Fact]
    public void Apply_ReplacePresets_ListsEachIdlessPresetAsRemoved()
    {
        ScenarioAircraft aircraft = Golden("B738");
        aircraft.PresetCommands.Add(Preset("", "CM 50", 10));
        aircraft.PresetCommands.Add(Preset("", "SPD 210", 20));

        StampResult result = RouteStamp.Apply(aircraft, Route("MOD SFO"), ReplacePresets(), Context(1));

        StampChange[] presets = [.. result.Changes.Where(c => c.Path.StartsWith("/presetCommands", StringComparison.Ordinal))];
        Assert.Equal(
            [("/presetCommands/0", DiffKind.Removed), ("/presetCommands/1", DiffKind.Removed), ("/presetCommands/0", DiffKind.Added)],
            presets.Select(c => (c.Path, c.Kind))
        );
        Assert.Contains("CM 50", presets[0].OldValue, StringComparison.Ordinal);
        Assert.Contains("SPD 210", presets[1].OldValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AddedPresets_LeaveIdlessPresetsUnchanged()
    {
        ScenarioAircraft aircraft = Golden("B738");
        aircraft.PresetCommands.Add(Preset("", "CM 50", 10));
        StampProfile profile = ReplacePresets() with { Presets = ReplacePresets().Presets with { Replace = false } };

        StampResult result = RouteStamp.Apply(aircraft, Route("MOD SFO"), profile, Context(1));

        StampChange change = Assert.Single(result.Changes, c => c.Path.StartsWith("/presetCommands", StringComparison.Ordinal));
        Assert.Equal(("/presetCommands/1", DiffKind.Added), (change.Path, change.Kind));
    }

    [Fact]
    public void Apply_AutoTrackOn_IsStamped()
    {
        StampProfile profile = new()
        {
            AutoTrack = new StampAutoTrack
            {
                Apply = true,
                PositionId = "2B",
                HandoffDelay = 30,
                ScratchPad = "SFO",
            },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(1));

        AutoTrackConditions conditions = Assert.IsType<AutoTrackConditions>(result.Aircraft.AutoTrackConditions);
        Assert.Equal("2B", conditions.PositionId);
        Assert.Equal(30, conditions.HandoffDelay);
        Assert.Equal("SFO", conditions.ScratchPad);
    }

    [Fact]
    public void Apply_AutoTrackWithNoPosition_IsSkippedWithAWarning()
    {
        StampProfile profile = new()
        {
            AutoTrack = new StampAutoTrack
            {
                Apply = true,
                PositionId = " ",
                HandoffDelay = 30,
            },
        };

        StampResult result = RouteStamp.Apply(Golden("B738"), Route("SFO MOD"), profile, Context(1));

        Assert.Null(result.Aircraft.AutoTrackConditions);
        Assert.Contains("Auto-track skipped: the profile names no position", result.Warnings);
    }

    [Fact]
    public void Apply_AutoTrackOff_IsUntouched()
    {
        ScenarioAircraft aircraft = Golden("B738");
        aircraft.AutoTrackConditions = new AutoTrackConditions { PositionId = "1A", HandoffDelay = 10 };

        StampResult result = RouteStamp.Apply(aircraft, Route("SFO MOD"), Cruise(35000), Context(1));

        Assert.Equal("1A", result.Aircraft.AutoTrackConditions!.PositionId);
        Assert.Equal(10, result.Aircraft.AutoTrackConditions.HandoffDelay);
        Assert.DoesNotContain(result.Changes, c => c.Path.StartsWith("/autoTrackConditions", StringComparison.Ordinal));
    }

    [Fact]
    public void FromFoundRoute_WithNoSources_IsNamedByItsRouteText()
    {
        var found = new FoundRoute("SFO MOD", 0, [], null, null, null);

        var route = StampRoute.FromFoundRoute(found);

        Assert.Equal("SFO MOD", route.Name);
    }

    [Fact]
    public void FromFoundRoute_IsNamedFromItsSources()
    {
        var found = new FoundRoute("SFO MOD", 3, ["FAA preferred", "SWIM"], null, null, null);

        var route = StampRoute.FromFoundRoute(found);

        Assert.Equal("FAA preferred, SWIM", route.Name);
        Assert.Equal("SFO MOD", route.RouteText);
    }

    [Fact]
    public void FromSavedRoute_Fixes_WritesTheNavPath()
    {
        var saved = new SavedRoute("r1", "West", null, "ZOA", [RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD")]);

        StampRouteConversion conversion = StampRoute.FromSavedRoute(saved, FixResolutionContext.None, NavigationDatabase.Instance);

        Assert.Null(conversion.Failure);
        Assert.Equal(new StampRoute("West", "SFO MOD", []).RouteText, conversion.Route!.RouteText);
        Assert.Equal("West", conversion.Route.Name);
        Assert.Empty(conversion.Route.Warnings);
    }

    [Fact]
    public void FromSavedRoute_UnknownFix_Fails()
    {
        var saved = new SavedRoute("r1", "West", null, "ZOA", [RoutePoint.AtFix("SFO"), RoutePoint.AtFix("QQZZX")]);

        StampRouteConversion conversion = StampRoute.FromSavedRoute(saved, FixResolutionContext.None, NavigationDatabase.Instance);

        Assert.Null(conversion.Route);
        Assert.Equal("Route West: Point 2: Unknown fix QQZZX.", conversion.Failure);
    }

    [Fact]
    public void FromSavedRoute_NoPoints_Fails()
    {
        var saved = new SavedRoute("r1", "Empty", null, "ZOA", []);

        StampRouteConversion conversion = StampRoute.FromSavedRoute(saved, FixResolutionContext.None, NavigationDatabase.Instance);

        Assert.Null(conversion.Route);
        Assert.Equal("Route Empty has no points", conversion.Failure);
    }

    [Fact]
    public void Apply_EveryCorpusAircraft_NeverThrowsAndRoundTrips()
    {
        string examples = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");
        (string from, string to) = TwoKsfoStars();
        StampProfile profile = Everything() with
        {
            StarAliases = [new StarAlias(from, to)],
            Pools = new StampPools
            {
                AircraftTypes = ["B738", "B77W"],
                CallsignPrefixes = ["UAL"],
                Runways = ["28L"],
                ParkingSpots = ["A1"],
            },
        };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = new StampContext(FixedNow, new Random(7), new Random(IdSeed), taken, ModelDate);
        int stamped = 0;
        foreach (string path in Directory.EnumerateFiles(examples, "*.json").Order(StringComparer.Ordinal))
        {
            Scenario scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), ScenarioLoader.JsonOptions)!;
            foreach (ScenarioAircraft aircraft in scenario.Aircraft)
            {
                JsonNode? before = JsonSerializer.SerializeToNode(aircraft, ScenarioLoader.JsonOptions);
                string routeText = aircraft.FlightPlan?.Route ?? "";
                StampResult result = RouteStamp.Apply(aircraft, Route($"{routeText} {from}"), profile, context);
                taken.Add(result.Aircraft.AircraftId);

                Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(aircraft, ScenarioLoader.JsonOptions)), path);
                string json = JsonSerializer.Serialize(result.Aircraft, ScenarioLoader.JsonOptions);
                ScenarioAircraft? read = JsonSerializer.Deserialize<ScenarioAircraft>(json, ScenarioLoader.JsonOptions);
                Assert.NotNull(read);
                Assert.Equal(json, JsonSerializer.Serialize(read, ScenarioLoader.JsonOptions));
                stamped++;
            }
        }

        Assert.True(stamped > 0);
    }

    private const int IdSeed = 99;

    private static StampContext Context(int poolSeed) => new(FixedNow, new Random(poolSeed), new Random(IdSeed), NoCallsigns, ModelDate);

    private static PresetCommand Preset(string id, string command, int timeOffset) =>
        new()
        {
            Id = id,
            Command = command,
            TimeOffset = timeOffset,
        };

    private static ScenarioAircraft Golden(string type) =>
        new()
        {
            Id = "a1",
            AircraftId = "UAL1",
            AircraftType = type,
            StartingConditions = new StartingConditions
            {
                Type = StartingConditionKinds.FixOrFrd,
                Fix = "SFO",
                Altitude = 5000,
                Speed = 250,
            },
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = "KSFO",
                Destination = "KLAX",
                CruiseAltitude = 33000,
                CruiseSpeed = 450,
                Route = "OLD",
                AircraftType = type,
            },
        };

    private static StampRoute Route(string text) => new("test", text, []);

    private static StampProfile RouteOnly() =>
        new()
        {
            FlightPlan = new StampFlightPlan { Apply = true, ApplyRoute = true },
        };

    private static StampProfile Cruise(int altitude) =>
        new()
        {
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                Rules = "IFR",
                CruiseAltitude = altitude,
                ApplyRoute = true,
            },
        };

    private static StampProfile Vfr(int altitude) => Cruise(altitude) with { FlightPlan = Cruise(altitude).FlightPlan with { Rules = "VFR" } };

    private static StampProfile HeavyOnly() => Cruise(36000) with { HeavyOnly = true };

    private static StampProfile ReplacePresets() =>
        Cruise(36000) with
        {
            Presets = new StampPresets
            {
                Apply = true,
                Replace = true,
                Commands = [new StampPresetCommand("SAY {CS} {RTE} {ALT}", 5)],
            },
        };

    private static StampProfile Everything() =>
        Cruise(36000) with
        {
            StartingConditions = new StampStartingConditions
            {
                Apply = true,
                Altitude = 9000,
                Speed = 280,
                ApplyNavigationPath = true,
            },
            Presets = new StampPresets { Apply = true, Commands = [new StampPresetCommand("CM {ALT}", 30)] },
            AutoTrack = new StampAutoTrack { Apply = true, PositionId = "2B" },
            PrimaryAirport = "KSFO",
        };

    // The current KSFO STAR ids, each once, in ordinal order.
    private static string[] KsfoStars()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        return
        [
            .. navDb
                .GetStars("KSFO")
                .Select(s => s.ProcedureId.ToUpperInvariant())
                .Where(id => navDb.ResolveStarId(id) == id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    // Every fix a STAR publishes: its body, its transition names and its transition fixes, upper case.
    private static HashSet<string> StarFixes(string star)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions = navDb.GetStarTransitions(star) ?? [];
        return
        [
            .. (navDb.GetStarBody(star) ?? [])
                .Concat(transitions.Select(t => t.Name))
                .Concat(transitions.SelectMany(t => t.Fixes))
                .Select(f => f.ToUpperInvariant()),
        ];
    }

    // Two different KSFO STARs, each its own current id.
    private static (string From, string To) TwoKsfoStars()
    {
        string[] stars = KsfoStars();
        Assert.True(stars.Length >= 2, "KSFO has fewer than two STARs in the pinned navdata");
        return (stars[0], stars[1]);
    }

    // A KSFO STAR, one of its transitions, and another KSFO STAR that has no transition of that name.
    private static (string From, string Transition, string To) StarPairWithoutTransition()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string[] stars = KsfoStars();
        foreach (string from in stars)
        {
            foreach ((string name, _) in navDb.GetStarTransitions(from) ?? [])
            {
                string? to = stars.FirstOrDefault(other =>
                    (other != from) && !(navDb.GetStarTransitions(other) ?? []).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                );
                if (to is not null)
                {
                    return (from, name.ToUpperInvariant(), to);
                }
            }
        }

        throw new InvalidOperationException("No KSFO STAR pair differs by a transition in the pinned navdata");
    }

    // Another KSFO STAR, a KSFO STAR, and a fix of its body past the first that no transition names or starts at.
    private static (string From, string To, string Mid) StarWithMidBodyFix()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string[] stars = KsfoStars();
        foreach (string to in stars)
        {
            IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions = navDb.GetStarTransitions(to) ?? [];
            HashSet<string> entries =
            [
                .. transitions.Select(t => t.Name.ToUpperInvariant()),
                .. transitions.Where(t => t.Fixes.Count > 0).Select(t => t.Fixes[0].ToUpperInvariant()),
            ];
            IReadOnlyList<string> body = navDb.GetStarBody(to) ?? [];
            string? mid = body.Skip(1)
                .Select(f => f.ToUpperInvariant())
                .FirstOrDefault(f => !entries.Contains(f) && !string.Equals(f, body[0], StringComparison.OrdinalIgnoreCase));
            if (mid is not null)
            {
                return (stars.First(s => s != to), to, mid);
            }
        }

        throw new InvalidOperationException("No KSFO STAR has a body fix past its entry in the pinned navdata");
    }

    // Another KSFO STAR, a KSFO STAR with two or more transitions, one transition, and the entry of another that is not on it.
    private static (string From, string To, string Transition, string Fix) StarTransitionAndFixOffIt()
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string[] stars = KsfoStars();
        foreach (string to in stars)
        {
            IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions = navDb.GetStarTransitions(to) ?? [];
            foreach ((string name, IReadOnlyList<string> fixes) in transitions)
            {
                string? fix = transitions
                    .Where(t => (t.Name != name) && (t.Fixes.Count > 0))
                    .Select(t => t.Fixes[0].ToUpperInvariant())
                    .FirstOrDefault(f => !fixes.Contains(f, StringComparer.OrdinalIgnoreCase) && !f.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (fix is not null)
                {
                    return (stars.First(s => s != to), to, name.ToUpperInvariant(), fix);
                }
            }
        }

        throw new InvalidOperationException("No KSFO STAR has two transitions with distinct entries in the pinned navdata");
    }

    // A procedure id of another airport that navdata knows exactly and the airport does not publish.
    private static string ProcedureNotAt(IEnumerable<string> ids, string airport, bool isSid)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string? id = ids.Select(i => i.ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(i =>
                isSid
                    ? (navDb.GetSidBody(i) is not null) && (navDb.GetSid(airport, i) is null)
                    : (navDb.GetStarBody(i) is not null) && (navDb.GetStar(airport, i) is null)
            );
        return id ?? throw new InvalidOperationException($"No procedure outside {airport} in the pinned navdata");
    }

    // A Random that fails the test on any draw.
    private sealed class ThrowingRandom : Random
    {
        public override int Next() => throw Drawn();

        public override int Next(int maxValue) => throw Drawn();

        public override int Next(int minValue, int maxValue) => throw Drawn();

        public override long NextInt64() => throw Drawn();

        public override long NextInt64(long maxValue) => throw Drawn();

        public override long NextInt64(long minValue, long maxValue) => throw Drawn();

        public override double NextDouble() => throw Drawn();

        public override float NextSingle() => throw Drawn();

        public override void NextBytes(byte[] buffer) => throw Drawn();

        public override void NextBytes(Span<byte> buffer) => throw Drawn();

        protected override double Sample() => throw Drawn();

        private static InvalidOperationException Drawn() => new("A random value was drawn with no pool");
    }
}
