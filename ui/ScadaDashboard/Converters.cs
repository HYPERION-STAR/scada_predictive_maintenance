using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ScadaDashboard;

/// <summary>Uyari onem seviyesini renge cevirir (uyari paneli).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Critical = Frozen(0xE7, 0x4C, 0x3C);
    private static readonly SolidColorBrush Warning = Frozen(0xF1, 0xC4, 0x0F);
    private static readonly SolidColorBrush Info = Frozen(0x3A, 0x9B, 0xDC);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Kritik" => Critical,
            "Uyari" => Warning,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Sayiyi "%73" gibi bicimler (kultur bagimsiz).</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value is double v ? v : 0;
        return $"%{d:0}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
