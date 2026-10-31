using Xunit;
using Yaat.Sim.Speech.Context;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// A corpus folder's <c>slots.json</c>: the gold slots of every clip by kind, value and spoken form, and
/// the errors a malformed file raises.
/// </summary>
public sealed class CorpusSlotsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-slots-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_NoFile_ReturnsNull() => Assert.Null(CorpusSlots.Read(_dir));

    [Fact]
    public void Read_ParsesKindsValuesSpokenAndNote()
    {
        WriteSlots(
            """
            {
              "koak-app_seg0004": [
                { "kind": "callsign", "value": "N4926F", "spoken": "four nine two six foxtrot" },
                { "kind": "fix", "value": "MITOE", "spoken": "mitoe" },
                { "kind": "runway", "value": "KHWD/28L", "spoken": "two eight left" },
                { "kind": "taxiway", "value": "KOAK/W4", "spoken": "whiskey four" },
                { "kind": "gate", "value": "KSFO/SPOT 6B", "spoken": "spot six bravo", "note": "stale spot" },
                { "kind": "landmark", "value": "KOAK/KAISER", "spoken": "kaiser" },
                { "kind": "procedure", "value": "KOAK/I30", "spoken": "ils three zero" }
              ],
              "ksfo-gnd_seg0099": []
            }
            """
        );

        IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>>? clips = CorpusSlots.Read(_dir);

        Assert.NotNull(clips);
        Assert.Equal(2, clips.Count);
        Assert.Empty(clips["ksfo-gnd_seg0099"]);
        IReadOnlyList<CorpusSlot> slots = clips["koak-app_seg0004"];
        Assert.Equal(Enum.GetValues<EntityKind>().Length, slots.Count);
        Assert.Equal(EntityKind.Callsign, slots[0].Kind);
        Assert.Equal("N4926F", slots[0].Value);
        Assert.Equal("four nine two six foxtrot", slots[0].Spoken);
        Assert.Null(slots[0].Note);
        Assert.Equal("spot six bravo", slots[4].Spoken);
        Assert.Equal("stale spot", slots[4].Note);
        Assert.Equal(
            ["callsign", "fix", "runway", "taxiway", "gate", "landmark", "procedure"],
            [.. slots.Select(slot => CorpusSlots.KindLabel(slot.Kind))]
        );
        Assert.Equal("KOAK/I30", slots[6].Value);
    }

    [Fact]
    public void Read_UnknownKind_ThrowsNamingClipAndKind()
    {
        WriteSlots("""{ "koak-app_seg0004": [ { "kind": "fixx", "value": "MITOE", "spoken": "mitoe" } ] }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));

        Assert.Contains("koak-app_seg0004", error.Message, StringComparison.Ordinal);
        Assert.Contains("fixx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_BlankValue_ThrowsNamingClip()
    {
        WriteSlots("""{ "koak-app_seg0004": [ { "kind": "fix", "value": "  ", "spoken": "mitoe" } ] }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));

        Assert.Contains("koak-app_seg0004", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MissingSpoken_ThrowsNamingClip()
    {
        WriteSlots("""{ "koak-app_seg0004": [ { "kind": "fix", "value": "MITOE" } ] }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));

        Assert.Contains("koak-app_seg0004", error.Message, StringComparison.Ordinal);
        Assert.Contains("no spoken form", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NullClip_Throws()
    {
        WriteSlots("""{ "koak-app_seg0004": null }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));

        Assert.Equal($"koak-app_seg0004: {Path.Combine(_dir, CorpusSlots.FileName)} gives no slot array.", error.Message);
    }

    [Fact]
    public void Read_NullSlot_Throws()
    {
        WriteSlots("""{ "koak-app_seg0004": [ null ] }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));

        Assert.Equal($"koak-app_seg0004: {Path.Combine(_dir, CorpusSlots.FileName)} holds a null slot.", error.Message);
    }

    [Fact]
    public void Read_NotAnObject_Throws()
    {
        WriteSlots("[]");

        Assert.Throws<InvalidDataException>(() => CorpusSlots.Read(_dir));
    }

    private void WriteSlots(string json) => File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), json);
}
