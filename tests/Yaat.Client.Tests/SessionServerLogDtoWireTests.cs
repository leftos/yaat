using System.Text.Json;
using Xunit;
using Yaat.Client.Services;

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
                },
            },
        };
        string json = JsonSerializer.Serialize(serverShape, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        SessionServerLogDto? dto = JsonSerializer.Deserialize(json, YaatHubJsonContext.Default.SessionServerLogDto);

        Assert.NotNull(dto);
        Assert.Equal("A0 joined the room", dto.Text);
        Assert.Equal(("1826127", "Jane Doe", "JD"), (dto.Participants[0].Cid, dto.Participants[0].RealName, dto.Participants[0].Initials));
    }
}
