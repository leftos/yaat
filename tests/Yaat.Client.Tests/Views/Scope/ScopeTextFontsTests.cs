using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="ScopeTextFonts"/>: the bundled Terminus TTF Bold loads, draws its hand-drawn strikes aliased, and the
/// CRC STARS and ERAM size ladders map to the strike and row pitch the issue's tables give.
/// </summary>
public class ScopeTextFontsTests
{
    [Fact]
    public void Typeface_LoadsTheBundledTerminusBold()
    {
        SKTypeface typeface = ScopeTextFonts.Typeface;

        Assert.Contains("Terminus", typeface.FamilyName, StringComparison.Ordinal);
        Assert.True(typeface.GlyphCount > 0);
    }

    [Fact]
    public void LoadTypeface_AMissingResource_ThrowsNamingIt()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ScopeTextFonts.LoadTypeface("Yaat.Client.Fonts.NoSuchFont.ttf")
        );

        Assert.Contains("Yaat.Client.Fonts.NoSuchFont.ttf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MakeFont_DrawsTheStrikeAliased_WithTheStrikeCellPitch()
    {
        using SKFont font = ScopeTextFonts.MakeFont(14);
        Assert.Equal(14f, font.Size);
        Assert.Equal(SKFontEdging.Alias, font.Edging);

        using SKBitmap bitmap = DrawPositioned(font, "N5", ScopeTextFonts.GlyphPitch(14));

        var inkColumns = new SortedSet<int>();
        var inkRows = new SortedSet<int>();
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte red = bitmap.GetPixel(x, y).Red;
                Assert.True(red is 0 or 255, $"pixel ({x},{y}) is {red}: the text is not aliased");
                if (red == 255)
                {
                    inkColumns.Add(x);
                    inkRows.Add(y);
                }
            }
        }

        // The 14 px strike's capitals are 10 px tall.
        Assert.Equal(10, inkRows.Max - inkRows.Min + 1);
        // N and 5 are each one run of ink columns; at the 8 px cell pitch a blank column separates them.
        Assert.Equal(2, ColumnRuns(inkColumns));
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 14)]
    [InlineData(2, 18)]
    [InlineData(3, 20)]
    [InlineData(4, 24)]
    public void EramLadder_MapsEachSizeToItsStrike(int size, int strikePx) => Assert.Equal(strikePx, ScopeTextFonts.EramStrikePx[size]);

    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 12)]
    [InlineData(2, 14)]
    [InlineData(3, 18)]
    [InlineData(4, 18)]
    [InlineData(5, 22)]
    [InlineData(6, 28)]
    public void StarsLadder_MapsEachSizeToItsStrike(int size, int strikePx) => Assert.Equal(strikePx, ScopeTextFonts.StarsStrikePx[size]);

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 16)]
    [InlineData(2, 18)]
    [InlineData(3, 20)]
    [InlineData(4, 22)]
    public void EramRowPitch_IsCrcCapPlusSeven(int size, int pitch) => Assert.Equal(pitch, ScopeTextFonts.EramRowPitch[size]);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 11)]
    [InlineData(2, 13)]
    [InlineData(3, 15)]
    [InlineData(4, 17)]
    [InlineData(5, 19)]
    [InlineData(6, 22)]
    public void StarsRowPitch_IsCrcLineHeightPlusSpacing(int size, int pitch) => Assert.Equal(pitch, ScopeTextFonts.StarsRowPitch[size]);

    [Fact]
    public void Ladders_CoverEveryCrcSize()
    {
        Assert.Equal(5, ScopeTextFonts.EramStrikePx.Count);
        Assert.Equal(5, ScopeTextFonts.EramRowPitch.Count);
        Assert.Equal(7, ScopeTextFonts.StarsStrikePx.Count);
        Assert.Equal(7, ScopeTextFonts.StarsRowPitch.Count);
    }

    [Theory]
    [InlineData(12, 6f)]
    [InlineData(14, 8f)]
    [InlineData(18, 10f)]
    [InlineData(20, 10f)]
    [InlineData(24, 12f)]
    public void GlyphPitch_IsTheStrikeCell(int strikePx, float pitch) => Assert.Equal(pitch, ScopeTextFonts.GlyphPitch(strikePx));

    private static SKBitmap DrawPositioned(SKFont font, string text, float pitch)
    {
        ushort[] glyphs = font.GetGlyphs(text);
        var positions = new SKPoint[glyphs.Length];
        for (int i = 0; i < glyphs.Length; i++)
        {
            positions[i] = new SKPoint(i * pitch, 0f);
        }

        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(font, glyphs.Length);
        run.SetGlyphs(glyphs);
        run.SetPositions(positions);
        using SKTextBlob blob = builder.Build()!;

        var bitmap = new SKBitmap(new SKImageInfo(40, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawText(blob, 4f, 18f, paint);
        return bitmap;
    }

    private static int ColumnRuns(SortedSet<int> columns)
    {
        int runs = 0;
        int previous = int.MinValue;
        foreach (int column in columns)
        {
            if (column != previous + 1)
            {
                runs++;
            }

            previous = column;
        }

        return runs;
    }
}
