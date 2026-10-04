using Yaat.Client.Services;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One hold-short node Taxi to runway offers for a runway end, with the text its submenu shows, e.g.
/// <c>At W (nearest, full length)</c>.
/// </summary>
/// <param name="Node">The hold-short node the taxi choices route to.</param>
/// <param name="Label">The submenu's text: the taxiway the hold short sits on and why it is offered.</param>
public sealed record RunwayHoldShortTarget(GroundNodeDto Node, string Label);
