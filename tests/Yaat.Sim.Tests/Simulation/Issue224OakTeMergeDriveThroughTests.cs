using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #224: "Weird routing bug when one aircraft is stopped behind the other."
///
/// OAK ground. SWA1182 (B738) is taxiing down taxiway TE behind SWA863 (B738), which pushed onto
/// TE ahead of it and is stationary. Their routes merge onto the shared TE lane node 949→1:
/// SWA863's route (TAXI U W W1 30) STARTS at node 949; SWA1182's route (TAXI TE $E) ENDS by
/// traversing …948→949→1, so SWA863 sits dead ahead of it.
///
/// Bug: the instant SWA863 is given its taxi clearance (t≈580), the conflict detector flips the
/// yield — it pins SWA863 (SpeedLimit=0, "stuck") and releases SWA1182, which drives forward to
/// within ~12 ft ("through" SWA863) and parks on top of it. The instructor had to issue a manual
/// BREAK on SWA863 at t=600 to un-stick it.
///
/// Root cause: the merge node (SWA863's route-start) is invisible to convergence detection, so the
/// pair falls through to PairKind.Crossing, whose mutual-stop tie-break picked the holder by
/// callsign ordinal ("SWA863" &gt; "SWA1182" → SWA863 holds) and released the follower unguarded.
/// After the fix the follower (SWA1182) holds and the lead (SWA863) proceeds — the auto equivalent
/// of FOLLOW/BEHIND sequencing (7110.65 §3-7-2.a).
///
/// Assertions are scoped to the pre-BREAK window (t &lt; 600) so the user's manual BREAK cannot mask
/// the bug.
/// </summary>
public class Issue224OakTeMergeDriveThroughTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue224-oak-taxi-through-lead-recording.zip";
    private const string Lead = "SWA863";
    private const string Follower = "SWA1182";
    private const double FtPerNm = 6076.12;

    // The merge conflict forms just after SWA863 is cleared to taxi (t≈580); BREAK is at t=600.
    private const int WindowStart = 575;
    private const int PreBreakEnd = 599;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_MergeWindow()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var captured = new List<string>();
        GroundConflictDetector.DebugSink = line =>
        {
            if (line.Contains(Lead) && line.Contains(Follower))
            {
                captured.Add(line);
            }
        };

        try
        {
            engine.Replay(recording, WindowStart);
            for (int t = WindowStart; t <= 605; t++)
            {
                captured.Clear();
                engine.ReplayOneSecond();
                var lead = engine.FindAircraft(Lead);
                var follower = engine.FindAircraft(Follower);
                if (lead is null || follower is null)
                {
                    continue;
                }

                double gapFt = GeoMath.DistanceNm(lead.Position, follower.Position) * FtPerNm;
                output.WriteLine(
                    $"t={t, 4} {Lead} gs={lead.IndicatedAirspeed, 5:F1} lim={Fmt(lead.Ground.SpeedLimit)} ayt={lead.Ground.AutoYieldTarget ?? "-"} brk={lead.Ground.ConflictBreakRemainingSeconds:F0} | "
                        + $"{Follower} gs={follower.IndicatedAirspeed, 5:F1} lim={Fmt(follower.Ground.SpeedLimit)} ayt={follower.Ground.AutoYieldTarget ?? "-"} | gap={gapFt:F0}ft"
                );
                foreach (var line in captured)
                {
                    output.WriteLine("      " + line);
                }
            }
        }
        finally
        {
            GroundConflictDetector.DebugSink = null;
        }
    }

    [Fact]
    public void Follower_DoesNotDriveThroughLead_BeforeBreak()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, WindowStart);

        double minGapFt = double.MaxValue;
        int minGapTick = -1;
        bool conflictEngaged = false;
        double leadDistanceTravelledFt = 0;
        var prevLeadPos = engine.FindAircraft(Lead)?.Position;

        for (int t = WindowStart; t <= PreBreakEnd; t++)
        {
            engine.ReplayOneSecond();
            var lead = engine.FindAircraft(Lead);
            var follower = engine.FindAircraft(Follower);
            if (lead is null || follower is null)
            {
                continue;
            }

            if (prevLeadPos is { } pp)
            {
                leadDistanceTravelledFt += GeoMath.DistanceNm(pp, lead.Position) * FtPerNm;
            }
            prevLeadPos = lead.Position;

            if ((lead.Ground.SpeedLimit is not null) || (follower.Ground.SpeedLimit is not null))
            {
                conflictEngaged = true;
            }

            double gapFt = GeoMath.DistanceNm(lead.Position, follower.Position) * FtPerNm;
            if (gapFt < minGapFt)
            {
                minGapFt = gapFt;
                minGapTick = t;
            }
        }

        output.WriteLine(
            $"minGap={minGapFt:F0}ft at t={minGapTick}; leadTravelled={leadDistanceTravelledFt:F0}ft; conflictEngaged={conflictEngaged}"
        );

        Assert.True(conflictEngaged, "Expected the SWA863/SWA1182 merge conflict to engage — test is vacuous otherwise.");

        // Pre-fix: the follower drove through the lead, closing to ~12 ft. Post-fix the follower
        // holds behind, so the pair never overlaps.
        Assert.True(
            minGapFt >= 60,
            $"{Follower} closed to {minGapFt:F0}ft from {Lead} at t={minGapTick} (expected ≥60ft) — the follower drove through the stationary lead instead of holding behind it."
        );

        // The lead (correctly released) must make forward progress rather than sitting pinned until
        // the manual BREAK.
        Assert.True(
            leadDistanceTravelledFt >= 30,
            $"{Lead} (the merge lead) only moved {leadDistanceTravelledFt:F0}ft before BREAK — it was pinned instead of proceeding."
        );
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("F1") : "-";
}
