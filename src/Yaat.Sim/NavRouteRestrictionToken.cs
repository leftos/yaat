namespace Yaat.Sim;

/// <summary>
/// One token of a crossing-restriction label line on the radar "Show nav route" overlay: an altitude (<c>≥8000</c>) or a
/// speed (<c>210</c>), as <see cref="Data.Vnas.CrossingRestrictionLabel"/> formats it. <see cref="Latent"/> is true for
/// a published restriction that is on the route but not in force under the current clearance; the client draws it
/// dimmed.
/// </summary>
/// <param name="Text">The token's label text.</param>
/// <param name="Latent">True when the restriction is published but not in force now.</param>
public record NavRouteRestrictionToken(string Text, bool Latent);
