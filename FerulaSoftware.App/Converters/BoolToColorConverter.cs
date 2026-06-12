using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FerulaSoftware.App.Converters;

/// <summary>
/// Convierte bool → SolidColorBrush: true = verde (Online), false = rojo (Offline).
/// Usado en la barra de conexión para el indicador de estado del WebSocket.
/// </summary>
public sealed class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    private static readonly SolidColorBrush Online  = new(Color.Parse("#06D6A0"));
    private static readonly SolidColorBrush Offline = new(Color.Parse("#E63946"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Online : Offline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
