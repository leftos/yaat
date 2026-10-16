namespace Yaat.Sim.Weather;

/// <summary>
/// A recording refused as a whole because its GFS wind grids pass a limit: more distinct grids than
/// <see cref="WindGridRegistry.MaxRoomGrids"/>, or a grid entry that decompresses past <see cref="WindGridRegistry.MaxGridBytes"/>.
/// Deliberately not an <see cref="IOException"/> or <see cref="InvalidOperationException"/>, so the per-entry damage handling
/// that skips a broken grid never downgrades the refusal to a skip.
/// </summary>
public sealed class WindGridLimitException(string message) : Exception(message);
