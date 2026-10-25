using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The worked-positions choices a solo student is offered: one per level from their own position down to delivery, each
/// labelled with every position it covers and noted with what working down to that level means.
/// </summary>
public class WorkedPositionsChoicesTests
{
    private const string GroundNote = "Departures call you ready to taxi; no clearance requests.";
    private const string DeliveryNote = "IFR departures also ask you for their clearance first.";

    [Fact]
    public void GroundStudent_GetsGroundOnly_AndGroundAndDelivery_WithTheirNotes()
    {
        List<WorkedPositionsChoice> choices = WorkedPositionsChoices.For("GND");

        Assert.Equal([new("GND", "Ground only", GroundNote), new WorkedPositionsChoice("DEL", "Ground and Delivery", DeliveryNote)], choices);
    }

    [Fact]
    public void ApproachStudent_GetsFourChoices_TheLastNamingEveryPosition()
    {
        List<WorkedPositionsChoice> choices = WorkedPositionsChoices.For("APP");

        Assert.Equal(
            ["Approach only", "Approach and Tower", "Approach, Tower and Ground", "Approach, Tower, Ground and Delivery"],
            choices.Select(choice => choice.Label)
        );
        Assert.Equal(
            [
                "No tower calls; the runways are worked for you.",
                "You clear departures and arrivals for the runway; no taxi calls.",
                GroundNote,
                DeliveryNote,
            ],
            choices.Select(choice => choice.Note)
        );
    }

    [Fact]
    public void CenterStudent_NotesTheCenterOnlyChoice()
    {
        WorkedPositionsChoice first = WorkedPositionsChoices.For("CTR")[0];

        Assert.Equal(new WorkedPositionsChoice("CTR", "Center only", "No approach or tower calls."), first);
    }

    [Fact]
    public void UnknownOwnLevel_HasNoChoices() => Assert.Empty(WorkedPositionsChoices.For("XYZ"));
}
