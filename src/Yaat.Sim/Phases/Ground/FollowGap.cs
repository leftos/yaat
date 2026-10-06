namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The nose-to-tail gap a <c>FOLLOWG</c> follower keeps behind its lead, keyed by the lead: about 100 ft behind a small or
/// prop lead, 150 ft behind a large jet (757 included), 250 ft behind a heavy or super. A small or helicopter follower adds
/// 100 ft behind any jet, for its jet blast. The close-follow band — where the follower stops holding taxi speed and starts
/// closing up on the lead — reaches <see cref="CloseFollowBandExtraFt"/> beyond the stop gap.
/// </summary>
public static class FollowGap
{
    /// <summary>Stop gap (ft) behind a small or prop lead.</summary>
    public const double SmallOrPropLeadFt = 100.0;

    /// <summary>Stop gap (ft) behind a large jet, the 757 included.</summary>
    public const double LargeJetLeadFt = 150.0;

    /// <summary>Stop gap (ft) behind a heavy or super.</summary>
    public const double HeavyLeadFt = 250.0;

    /// <summary>What a small or helicopter follower adds (ft) behind a jet.</summary>
    public const double SmallFollowerBehindJetExtraFt = 100.0;

    /// <summary>How far (ft) beyond the stop gap the close-follow band reaches.</summary>
    public const double CloseFollowBandExtraFt = 150.0;

    /// <summary>
    /// The nose-to-tail gap (ft) the follower stops at behind the lead. A heavy or super lead takes the heavy gap whatever its
    /// engines; otherwise a small lead, or one that is not a jet, takes the small-or-prop gap, and any other jet the large-jet
    /// gap.
    /// </summary>
    public static double StopGapFt(string leadType, AircraftCategory leadCategory, string followerType, AircraftCategory followerCategory)
    {
        WakeTurbulenceData.WakeClass leadWake = WakeTurbulenceData.WakeClassForType(leadType, leadCategory);
        double gapFt = leadWake switch
        {
            WakeTurbulenceData.WakeClass.Heavy or WakeTurbulenceData.WakeClass.Super => HeavyLeadFt,
            WakeTurbulenceData.WakeClass.Small => SmallOrPropLeadFt,
            _ => leadCategory == AircraftCategory.Jet ? LargeJetLeadFt : SmallOrPropLeadFt,
        };

        bool smallFollower =
            (followerCategory == AircraftCategory.Helicopter)
            || (WakeTurbulenceData.WakeClassForType(followerType, followerCategory) == WakeTurbulenceData.WakeClass.Small);
        return (smallFollower && (leadCategory == AircraftCategory.Jet)) ? gapFt + SmallFollowerBehindJetExtraFt : gapFt;
    }

    /// <summary>The nose-to-tail distance (ft) inside which the follower closes up on the lead rather than holding taxi speed.</summary>
    public static double CloseFollowBandFt(string leadType, AircraftCategory leadCategory, string followerType, AircraftCategory followerCategory) =>
        StopGapFt(leadType, leadCategory, followerType, followerCategory) + CloseFollowBandExtraFt;
}
