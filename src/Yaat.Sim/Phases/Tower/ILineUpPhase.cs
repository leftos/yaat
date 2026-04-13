namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Marker interface for any phase that drives an aircraft from a taxiway
/// hold-short position onto the runway centerline and leaves it aligned,
/// stopped, and ready for <see cref="TakeoffPhase"/>.
///
/// All public behavior (<c>OnStart</c>, <c>OnTick</c>, <c>ToSnapshot</c>,
/// <c>CanAcceptCommand</c>, <c>Name</c>, etc.) is inherited from the
/// <see cref="Phase"/> base class — implementations are phase subclasses.
/// This interface exists solely as a type discriminator so callers can
/// check <c>currentPhase is ILineUpPhase</c> without tying the check to
/// a specific implementation version.
///
/// Implementations:
/// <list type="bullet">
///   <item><see cref="LineUpPhaseV1"/> — the analog 3-stage
///         perpendicular / cross / align implementation.</item>
/// </list>
///
/// V2 is under design; see <see cref="LineUpPhaseFactory"/> for the
/// runtime switch.
/// </summary>
public interface ILineUpPhase { }
