using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Logging;

namespace Yaat.Client.Tests;

/// <summary>
/// The server sends <c>GetSessionServerLog</c>'s result in camelCase; the client reads it through the source-generated
/// hub context, participants and all, so the bundle's client logs get the server log's pseudonyms.
/// </summary>
public class SessionServerLogDtoWireTests
{
    [Fact]
    public void ServerShape_DeserializesThroughTheHubContext()
    {
        var serverShape = new
        {
            Text = "A0 joined the room",
            Participants = new[]
            {
                new
                {
                    Cid = "1826127",
                    RealName = "Jane Doe",
                    Initials = "JD",
                    OtherInitials = new[] { "JX" },
                    OtherRealNames = new[] { "Jane X. Doe" },
                },
            },
        };
        string json = JsonSerializer.Serialize(serverShape, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        SessionServerLogDto? dto = JsonSerializer.Deserialize(json, YaatHubJsonContext.Default.SessionServerLogDto);

        Assert.NotNull(dto);
        Assert.Equal("A0 joined the room", dto.Text);
        LogParticipant participant = dto.Participants[0];
        Assert.Equal(("1826127", "Jane Doe", "JD"), (participant.Cid, participant.RealName, participant.Initials));
        Assert.Equal(["JX"], participant.OtherInitials);
        Assert.Equal(["Jane X. Doe"], participant.OtherRealNames);
    }
}
