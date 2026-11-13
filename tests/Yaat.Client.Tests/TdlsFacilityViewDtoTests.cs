using System.Text.Json;
using Xunit;
using Yaat.Client.Tdls.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The vTDLS facility page carries the student position's frequency for the Facility Menu's "Add current frequency to
/// Temp. Dep Freqs". The hub writes it camelCased, as SignalR's JSON does, and the client reads it through the same
/// source-generated context it reads every other vTDLS payload with.
/// </summary>
public class TdlsFacilityViewDtoTests
{
    private static readonly JsonSerializerOptions HubOptions = new()
    {
        TypeInfoResolver = YaatTdlsHubJsonContext.Default,
        PropertyNameCaseInsensitive = true,
    };

    private static TdlsFacilityViewDto? Read(string json) =>
        (TdlsFacilityViewDto?)JsonSerializer.Deserialize(json, typeof(TdlsFacilityViewDto), HubOptions);

    [Fact]
    public void StudentFrequency_DeserializesFromTheHubPayload()
    {
        TdlsFacilityViewDto? view = Read("""{"facilityId":"OAK","facilityName":"Oakland ATCT","memberConfigs":[],"studentFrequencyMhz":"121.100"}""");

        Assert.NotNull(view);
        Assert.Equal("121.100", view.StudentFrequencyMhz);
    }

    [Fact]
    public void StudentFrequency_IsNull_WhenThePayloadCarriesNone()
    {
        TdlsFacilityViewDto? view = Read("""{"facilityId":"OAK","facilityName":"Oakland ATCT","memberConfigs":[]}""");

        Assert.NotNull(view);
        Assert.Null(view.StudentFrequencyMhz);
    }
}
