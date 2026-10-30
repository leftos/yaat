using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer</c> argument parser: every malformed command line fails with a message naming
/// the problem, and the corpus defaults (subset, seed, prompt arms) fill in for a well-formed one.
/// </summary>
public sealed class CorpusWerArgsTests : IDisposable
{
    private const string MissingDirToken = "<missing-dir>";
    private const string UwbNamedDirToken = "<uwb-named-dir>";

    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-wer-args-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("exclude each other", "uwb-atcc", "--subset", "5", "--all")]
    [InlineData("--subset needs a positive integer", "uwb-atcc", "--subset", "0")]
    [InlineData("--subset needs a positive integer", "uwb-atcc", "--subset", "x")]
    [InlineData("--prompt must be default, none or both", "uwb-atcc", "--prompt", "loud")]
    [InlineData("--seed needs a value", "uwb-atcc", "--seed")]
    [InlineData("unrecognised argument '--frobnicate'", "uwb-atcc", "--frobnicate")]
    [InlineData("the first argument must be the corpus")]
    [InlineData("the first argument must be the corpus", "--all")]
    [InlineData("nor an existing directory", MissingDirToken)]
    [InlineData("rename", UwbNamedDirToken)]
    public void A_Malformed_Command_Line_Fails_With_A_Message(string fragment, params string[] args)
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([.. args.Select(Substitute)]);

        Assert.Null(parse.Options);
        Assert.NotNull(parse.Error);
        Assert.Contains(fragment, parse.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Uwb_Atcc_Alone_Takes_The_Uwb_Defaults()
    {
        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs(["uwb-atcc"]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Equal("uwb-atcc", options.Corpus);
        Assert.Null(options.CorpusDir);
        Assert.Equal(400, options.Subset);
        Assert.Equal(1117, options.Seed);
        Assert.Equal(["default", "none"], options.Prompts);
    }

    [Fact]
    public void A_Folder_Takes_Every_Segment_And_Its_Lowercased_Name()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "MyCorpus")).FullName;

        CorpusWerRunner.ParseResult parse = CorpusWerRunner.ParseArgs([folder]);

        Assert.Null(parse.Error);
        CorpusWerRunner.Options options = Assert.IsType<CorpusWerRunner.Options>(parse.Options);
        Assert.Null(options.Subset);
        Assert.Equal("mycorpus", options.Corpus);
        Assert.Equal(folder, options.CorpusDir);
    }

    private string Substitute(string arg) =>
        arg switch
        {
            MissingDirToken => Path.Combine(_dir, "does-not-exist"),
            UwbNamedDirToken => Directory.CreateDirectory(Path.Combine(_dir, "UWB-ATCC")).FullName,
            _ => arg,
        };
}
