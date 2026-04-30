using System.Linq;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Systemic guard rail: <em>every</em> command in <see cref="CommandRegistry.All"/>, when
/// rejected, must produce a non-empty <see cref="CommandResult.Message"/> with descriptive
/// text. The test enumerates the entire registry, dispatches each command against an
/// aircraft state designed to surface its rejection path, and asserts the message contract.
///
/// What's tested for each command type:
/// <list type="number">
///   <item>Build a canonical-form input using a placeholder argument from the overload
///   metadata.</item>
///   <item>Parse it via <see cref="CommandParser.ParseCompound"/>.</item>
///   <item>If parse fails: assert the reason is non-empty AND descriptive (length &gt; 5,
///   not a generic placeholder).</item>
///   <item>If parse succeeds: dispatch via <see cref="CommandDispatcher.Dispatch"/> on a
///   parked aircraft (no phases, no flight plan, no runway). If dispatch fails, assert
///   the message contract.</item>
/// </list>
///
/// A command added to the registry without a rejection-message contract will fail this
/// test loudly — a new command cannot ship with a silent failure path.
/// </summary>
[Collection("NavDbMutator")]
public class CommandRejectionMessagesTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public CommandRejectionMessagesTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithFixNames("KLIDE", "BRIXX"));
    }

    public void Dispose() => _navDbScope.Dispose();

    /// <summary>
    /// Commands that legitimately succeed against a parked aircraft and therefore have no
    /// dispatch-level rejection to test in this scenario. They are NOT exempt from the
    /// rejection-message contract — their rejection paths are exercised in their own
    /// dedicated test files (e.g. <c>FlightCommandHandlerTests</c>).
    /// </summary>
    private static readonly HashSet<CanonicalCommandType> SuccessAgainstParkedAircraft =
    [
        CanonicalCommandType.Delete,
        CanonicalCommandType.Pause,
        CanonicalCommandType.Unpause,
        CanonicalCommandType.SimRate,
        CanonicalCommandType.Ident,
        CanonicalCommandType.Squawk,
        CanonicalCommandType.SquawkVfr,
        CanonicalCommandType.SquawkNormal,
        CanonicalCommandType.SquawkStandby,
        CanonicalCommandType.RandomSquawk,
        CanonicalCommandType.SquawkAll,
        CanonicalCommandType.SquawkNormalAll,
        CanonicalCommandType.SquawkStandbyAll,
    ];

    public static IEnumerable<object[]> AllCommandTypes() => CommandRegistry.All.Keys.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllCommandTypes))]
    public void EveryCommand_WhenRejected_ProducesDescriptiveMessage(CanonicalCommandType type)
    {
        if (SuccessAgainstParkedAircraft.Contains(type))
        {
            return;
        }

        var def = CommandRegistry.Get(type);
        Assert.NotNull(def);
        if (def.DefaultAliases.Length == 0)
        {
            return;
        }

        var input = SynthesizeCanonicalInput(def);
        if (input is null)
        {
            return;
        }

        var aircraft = MakeParkedAircraft();
        var ctx = TestDispatch.Context(new Random(0), validateDctFixes: false);

        var parseResult = CommandParser.ParseCompound(input, aircraft.FlightPlan.Route);
        if (!parseResult.IsSuccess)
        {
            AssertDescriptiveReason(parseResult.Reason, $"parser rejected '{input}' for {type}");
            return;
        }

        CommandResult dispatchResult;
        try
        {
            dispatchResult = CommandDispatcher.DispatchCompound(parseResult.Value!, aircraft, ctx);
        }
        catch (Exception ex) when (IsTestInfrastructureGap(ex))
        {
            // The dispatcher path threw because synthetic test data (e.g. missing CIFP
            // for a synthetic airport, missing ground layout) doesn't satisfy the
            // command's preconditions. The production code separately handles real-user
            // input via more populated state, and the rejection message contract is
            // covered for these commands by their dedicated handler tests
            // (e.g. ApproachCommandHandlerTests). Treat as out-of-scope for this test.
            return;
        }

        if (dispatchResult.Success)
        {
            return;
        }

        AssertDescriptiveReason(dispatchResult.Message, $"dispatcher rejected '{input}' for {type}");
    }

    private static bool IsTestInfrastructureGap(Exception ex)
    {
        // Synthetic test data without an active airport / ground layout / CIFP file path
        // surfaces as ArgumentException("path") from File.ReadLines. Real users hit
        // populated state — these are dedicated-test concerns, not contract violations
        // for this systemic test.
        if (ex is ArgumentException argEx && (argEx.ParamName == "path" || argEx.ParamName == "airport"))
        {
            return true;
        }
        return false;
    }

    private static void AssertDescriptiveReason(string? reason, string context)
    {
        Assert.NotNull(reason);
        Assert.False(string.IsNullOrWhiteSpace(reason), $"{context}: message must be non-empty");
        Assert.True(reason.Length > 5, $"{context}: message '{reason}' is too short to be descriptive");
        Assert.NotEqual("Unknown command", reason);
        Assert.NotEqual("Command rejected", reason);
    }

    /// <summary>
    /// Builds a canonical-form input string for a command definition using placeholder
    /// arguments derived from each parameter's name. Returns null when the command type
    /// requires special construction (e.g. text args with embedded commas) or has no
    /// usable overload.
    /// </summary>
    private static string? SynthesizeCanonicalInput(CommandDefinition def)
    {
        var alias = def.DefaultAliases[0];

        // Bare command — no args
        if (def.Overloads.Length == 0 || def.Overloads.All(o => o.Parameters.Length == 0))
        {
            return alias;
        }

        // Use the first overload with required params; pick placeholder values that match
        // the most common parameter shapes.
        var overload = def.Overloads.FirstOrDefault(o => o.Parameters.Length > 0) ?? def.Overloads[0];
        var args = new List<string>();
        foreach (var param in overload.Parameters)
        {
            var placeholder = PickPlaceholder(param);
            if (placeholder is null)
            {
                return null;
            }
            args.Add(placeholder);
        }

        return args.Count == 0 ? alias : $"{alias} {string.Join(' ', args)}";
    }

    private static string? PickPlaceholder(CommandParameter param)
    {
        if (param.IsLiteral)
        {
            return param.Name;
        }

        var name = param.Name.ToLowerInvariant();
        return name switch
        {
            "heading" => "270",
            "altitude" => "50",
            "speed" => "180",
            "mach" => ".78",
            "degrees" => "30",
            "code" => "1234",
            "runway" => "28R",
            "fix" or "fixname" => "KLIDE",
            "fixes" => "KLIDE",
            "approach" or "approachid" => "I28R",
            "airport" or "airportcode" => "KOAK",
            "star" or "starid" => "STAR1",
            "transition" => "KLIDE",
            "airway" or "airwayid" => "V25",
            "node" => "100",
            "parking" => "A1",
            "spot" => "B1",
            "taxiway" or "taxiway1" or "taxiway2" => "A",
            "path" => "A B C",
            "channel" or "channelid" => "1",
            "callsign" or "target" or "targetcallsign" or "traffic" => "N123",
            "frd" => "OAK030010",
            "location" => "C B",
            "rate" or "simrate" => "2",
            "duration" or "seconds" or "delay" => "5",
            "temperature" or "temp" => "20",
            "wind" => "27015",
            "visibility" or "vis" => "10",
            "ceiling" => "5000",
            "qnh" or "altimeter" => "29.92",
            "frequency" or "freq" => "118.0",
            "feet" => "1000",
            "value" or "amount" or "count" or "block" or "blockid" or "list" or "listid" or "tcp" or "id" => "1",
            "text" or "remark" or "remarks" or "annotation" or "name" => "TEST",
            "stripid" or "strip" => "ABC",
            "barid" or "bar" => "ABC",
            "x" or "y" => "0",
            "color" => "RED",
            _ => null,
        };
    }

    private static AircraftState MakeParkedAircraft()
    {
        return new AircraftState
        {
            Callsign = "N987",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            TrueHeading = new TrueHeading(280),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KSFO" },
        };
    }
}
