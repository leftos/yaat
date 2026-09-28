using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// How <see cref="ApproachCommandHandler.BuildProcedureTurnPhase"/> derives a procedure turn's courses from real CIFP legs.
/// </summary>
public class ProcedureTurnInboundCourseTests
{
    private static NavigationDatabase NavDb()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        NavigationDatabase.SetInstance(navDb);
        return navDb;
    }

    private static TrueHeading FinalCourse(NavigationDatabase navDb, CifpApproachProcedure procedure, string airport, string runway)
    {
        RunwayInfo runwayInfo = Assert.IsType<RunwayInfo>(navDb.GetRunway(airport, runway));
        return FinalApproachCourseExtractor.Extract(procedure, runwayInfo, navDb).Course;
    }

    /// <summary>
    /// KACK VOR RWY 24: the course into the PT fix sits in the PT's own transition (PI ACK, then CF ACK 239.6°M);
    /// the common IF ACK leg carries none. 239.6°M with the ACK VOR's station declination (W015) is 224.6°T.
    /// </summary>
    [Fact]
    public void KackS24_InboundCourse_IsTransitionCfLegAfterPi_ConvertedWithStationDeclination()
    {
        NavigationDatabase navDb = NavDb();
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("KACK", "S24"));
        CifpLeg piLeg = Assert.IsType<CifpLeg>(procedure.ProcedureTurnLeg);
        Assert.Equal(-15.0, navDb.GetStationDeclination("ACK"));

        ProcedureTurnPhase pt = Assert.IsType<ProcedureTurnPhase>(
            ApproachCommandHandler.BuildProcedureTurnPhase(piLeg, procedure, "KACK", FinalCourse(navDb, procedure, "KACK", "24"))
        );

        Assert.Equal("ACK", pt.FixName);
        Assert.InRange(pt.InboundCourseDeg, 224.5, 224.7);
    }

    /// <summary>
    /// A PI leg without a course flies 45° off the outbound course, on the side away from the 180° turn back. KCCR S19R
    /// with the PI course removed: inbound 207.6°T, outbound 027.6°T, left turn back, so 072.6°T, the published value.
    /// </summary>
    [Fact]
    public void KccrS19R_PiLegWithoutCourse_PtHeadingIs45DegreesOffOutboundAwayFromTurnBack()
    {
        NavigationDatabase navDb = NavDb();
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("KCCR", "S19R"));
        CifpLeg piLeg = Assert.IsType<CifpLeg>(procedure.ProcedureTurnLeg);
        Assert.Equal('L', piLeg.TurnDirection);

        ProcedureTurnPhase pt = Assert.IsType<ProcedureTurnPhase>(
            ApproachCommandHandler.BuildProcedureTurnPhase(
                piLeg with
                {
                    OutboundCourse = null,
                },
                procedure,
                "KCCR",
                FinalCourse(navDb, procedure, "KCCR", "19R")
            )
        );

        Assert.InRange(pt.InboundCourseDeg, 207.5, 207.7);
        Assert.InRange(pt.PtOutboundCourseDeg, 72.5, 72.7);
    }
}
