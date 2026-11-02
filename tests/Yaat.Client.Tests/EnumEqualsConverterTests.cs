using System.Globalization;
using Avalonia.Data;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>The radio converter: checked while the enum equals the radio's value, and only a check writes back.</summary>
public sealed class EnumEqualsConverterTests
{
    private static readonly EnumEqualsConverter Converter = new();

    [Fact]
    public void Convert_IsTrueOnlyForTheParameterValue()
    {
        Assert.Equal(true, Converter.Convert(SttMode.Accurate, typeof(bool?), SttMode.Accurate, CultureInfo.InvariantCulture));
        Assert.Equal(false, Converter.Convert(SttMode.Fast, typeof(bool?), SttMode.Accurate, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_WritesTheParameterWhenChecked_AndNothingWhenUnchecked()
    {
        Assert.Equal(SttDevice.Gpu, Converter.ConvertBack(true, typeof(SttDevice), SttDevice.Gpu, CultureInfo.InvariantCulture));
        Assert.Same(BindingOperations.DoNothing, Converter.ConvertBack(false, typeof(SttDevice), SttDevice.Gpu, CultureInfo.InvariantCulture));
    }
}
