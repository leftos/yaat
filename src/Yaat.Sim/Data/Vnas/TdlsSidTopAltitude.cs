namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// A SID's top altitude for a bare climb via, from the departure airport's TDLS config: <see cref="AltitudeFt"/>, and
/// <see cref="IsExceptMaintain"/> when it is the climb-via text's "EXC MAINT" altitude (an ATC-assigned top, AIM
/// 5-2-9.h.9) rather than the transition's initial altitude.
/// </summary>
public readonly record struct TdlsSidTopAltitude(int AltitudeFt, bool IsExceptMaintain);
