using System.Windows.Media;
using SkiaSharp;

namespace Dcb.Client.Map;

/// <summary>The four package colours, apart from the identity palette (blue own, red hostile,
/// yellow unknown) so a package member stands out on every map.</summary>
public static class PackageColors
{
    private static readonly (byte R, byte G, byte B)[] Rgb =
    [
        (0xFF, 0x5C, 0xE1),  // 1 magenta
        (0x2E, 0xE6, 0xD6),  // 2 cyan
        (0xA8, 0xF0, 0x3C),  // 3 lime
        (0xFF, 0xA8, 0x2E),  // 4 amber
    ];

    private static readonly Brush[] Brushes = Rgb.Select(c =>
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public static SKColor Sk(int number) => new(Rgb[number - 1].R, Rgb[number - 1].G, Rgb[number - 1].B);
    public static Brush Brush(int number) => Brushes[number - 1];
}
