using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Test convenience factory for <see cref="DispatchContext"/>. Production code
/// constructs the record explicitly (no optional params) so the compiler enforces
/// wiring when new context fields are added. Tests use this factory to stay
/// concise — they rarely care about the ground layout or auto-cross-runway flag.
/// </summary>
internal static class TestDispatch
{
    public static DispatchContext Context(
        Random rng,
        bool validateDctFixes = true,
        AirportGroundLayout? groundLayout = null,
        bool autoCrossRunway = false
    ) => new(groundLayout, rng, validateDctFixes, autoCrossRunway);
}
