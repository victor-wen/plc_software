using System.Globalization;
using System.Windows.Data;
using PlcSoftware.Core.Configuration;

namespace PlcSoftware.App.Converters;

/// <summary>Converts a <see cref="Parity"/> value to its Chinese display label (无 / 奇校验 / 偶校验 / 标志 / 空格).</summary>
public sealed class ParityChineseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Parity parity ? ParityLabel(parity) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static string ParityLabel(Parity parity) => parity switch
    {
        Parity.None => "无",
        Parity.Odd => "奇校验",
        Parity.Even => "偶校验",
        Parity.Mark => "标志",
        Parity.Space => "空格",
        _ => parity.ToString(),
    };
}

/// <summary>Converts a <see cref="StopBits"/> value to its Chinese display label (1 / 1.5 / 2).</summary>
public sealed class StopBitsChineseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StopBits stopBits ? StopBitsLabel(stopBits) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static string StopBitsLabel(StopBits stopBits) => stopBits switch
    {
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => stopBits.ToString(),
    };
}
