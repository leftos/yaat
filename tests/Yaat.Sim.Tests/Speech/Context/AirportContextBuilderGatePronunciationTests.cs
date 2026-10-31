using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Proto;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech.Context;

/// <summary>
/// Gate pronunciations and named ground places reach the speech-to-text context, from a real-navdata database over a temp
/// ARTCC folder holding only a <c>GatePronunciations/oak.json</c>.
/// </summary>
public class AirportContextBuilderGatePronunciationTests
{
    private static readonly Lazy<NavigationDatabase> Db = new(BuildDatabase);

    // The taxiway W4's bare spoken form, registered as a second SIG1 pronunciation to prove it gets no bare form.
    private static string W4Spoken => PhraseologyVerbalizer.SpellTaxiway("W4");

    [Fact]
    public void Sig1_GetsGateParkingAndBarePronunciationForms()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(Db.Value);

        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KOAK", Layout("OAK"), Db.Value);

        ContextEntity sig1 = Assert.Single(ground, entity => entity.Value == "KOAK/SIG1");
        Assert.Contains(sig1.Forms, form => Tokens(form) == TextTokens("gate signature one"));
        Assert.Contains(sig1.Forms, form => Tokens(form) == TextTokens("parking signature one"));
        Assert.Contains(sig1.Forms, form => Tokens(form) == TextTokens("signature one"));
    }

    [Fact]
    public void PronunciationEqualToTaxiwayBareForm_GetsNoBareForm()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(Db.Value);

        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KOAK", Layout("OAK"), Db.Value);

        ContextEntity sig1 = Assert.Single(ground, entity => entity.Value == "KOAK/SIG1");
        Assert.Contains(sig1.Forms, form => Tokens(form) == TextTokens($"gate {W4Spoken}"));
        Assert.DoesNotContain(sig1.Forms, form => Tokens(form) == TextTokens(W4Spoken));
    }

    [Fact]
    public void NamedPlace_BecomesLandmark_WithoutLayout()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(Db.Value);

        IReadOnlyList<ContextEntity> entities = AirportContextBuilder.Build("KOAK", null, 10, [], Db.Value, DecoderSettings.Default);

        AssertSignatureLandmark(entities);
    }

    [Fact]
    public void NamedPlace_BecomesLandmark_WithLayout()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(Db.Value);

        IReadOnlyList<ContextEntity> entities = AirportContextBuilder.Build("KOAK", Layout("OAK"), 10, [], Db.Value, DecoderSettings.Default);

        AssertSignatureLandmark(entities);
    }

    [Fact]
    public void NamedPlace_CallerLandmarkWithSameValueWins()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(Db.Value);
        ContextEntity callerSignature = ContextEntityFactory.Landmark("KOAK", "signature", ["signature aviation"]);

        IReadOnlyList<ContextEntity> entities = AirportContextBuilder.Build("KOAK", null, 10, [callerSignature], Db.Value, DecoderSettings.Default);

        ContextEntity landmark = Assert.Single(entities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KOAK/SIGNATURE"));
        Assert.Same(callerSignature, landmark);
    }

    private static void AssertSignatureLandmark(IReadOnlyList<ContextEntity> entities)
    {
        ContextEntity landmark = Assert.Single(entities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KOAK/SIGNATURE"));
        Assert.Contains(landmark.Forms, form => Tokens(form) == "signature");
    }

    private static NavigationDatabase BuildDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-gate-context-tests", Guid.NewGuid().ToString("N"));
        string category = Path.Combine(root, "ZZZ", "GatePronunciations");
        Directory.CreateDirectory(category);
        File.WriteAllText(
            Path.Combine(category, "oak.json"),
            $$"""
            {
              "airportId": "KOAK",
              "gates": [ { "name": "SIG1", "pronunciations": ["signature one", "{{W4Spoken}}"] } ],
              "places": [ { "name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["SIG*"] } ]
            }
            """
        );

        try
        {
            NavDataSet navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat")));
            string? cifpPath = TestVnasData.GetCifpPath();
            Assert.NotNull(cifpPath);
            return new NavigationDatabase(navData, cifpPath, artccsBaseDir: root, supplementaryCifpFilePaths: []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AirportGroundLayout Layout(string airportId)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);
        return layout;
    }

    private static string Tokens(SpokenForm form) => TextTokens(form.Text);

    private static string TextTokens(string text) => string.Join(" ", SpokenTokenizer.Tokenize(text).Select(token => token.Text));
}
