using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Binds a radio button to one value of an enum: checked while the value equals the converter parameter; checking it
/// writes the parameter back, and unchecking it writes nothing (the radio checked in its place writes its own value).
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ? parameter : BindingOperations.DoNothing;
}
