namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>
/// Which side of a CRC-style menu a pick is meant for: ERAM writes the scope data as CRC's menu does, PILOT sends the
/// matching pilot command.
/// </summary>
public enum CrcMenuMode
{
    /// <summary>The scope data entry CRC's own menu makes.</summary>
    Eram,

    /// <summary>The pilot command matching the pick.</summary>
    Pilot,
}
