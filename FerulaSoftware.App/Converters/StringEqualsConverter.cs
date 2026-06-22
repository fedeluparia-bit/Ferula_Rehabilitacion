using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FerulaSoftware.App.Converters;

/// <summary>
/// Devuelve true si el valor (string) coincide con el ConverterParameter.
/// Se usa para activar la clase visual del ítem de navegación seleccionado
/// en el sidebar (Classes.active), comparando PaginaActiva con la clave de cada botón.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
