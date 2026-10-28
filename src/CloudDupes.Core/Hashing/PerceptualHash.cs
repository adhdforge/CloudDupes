using System.Numerics;
using SkiaSharp;

namespace CloudDupes.Core.Hashing;

/// <summary>
/// Classic DCT-based pHash: shrink to 32x32 grayscale, take the 8x8 lowest frequencies and
/// set one bit per coefficient that is above the median. Robust to resizing and re-compression.
/// </summary>
public static class PerceptualHash
{
    const int Side = 32;
    const int Low = 8;

    static readonly double[,] Cos = BuildCosTable();

    static double[,] BuildCosTable()
    {
        var table = new double[Low, Side];
        for (var u = 0; u < Low; u++)
            for (var x = 0; x < Side; x++)
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        return table;
    }

    public static ulong Compute(Stream imageStream)
    {
        using var bitmap = SKBitmap.Decode(imageStream) ?? throw new InvalidDataException("Unsupported image format.");
        return Compute(bitmap);
    }

    public static ulong Compute(SKBitmap bitmap)
    {
        using var small = bitmap.Resize(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("Could not resize image.");

        var pixels = new double[Side, Side];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var c = small.GetPixel(x, y);
                pixels[y, x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            }
        }
        return Compute(pixels);
    }

    /// <summary>Hash of a 32x32 grayscale pixel grid indexed [y, x].</summary>
    public static ulong Compute(double[,] pixels)
    {
        var coefficients = new double[Low * Low];
        for (var v = 0; v < Low; v++)
        {
            for (var u = 0; u < Low; u++)
            {
                double sum = 0;
                for (var y = 0; y < Side; y++)
                    for (var x = 0; x < Side; x++)
                        sum += pixels[y, x] * Cos[u, x] * Cos[v, y];
                coefficients[v * Low + u] = sum;
            }
        }

        // The DC term (overall brightness) would dominate the median, so leave it out.
        var sorted = coefficients[1..].Order().ToArray();
        var median = (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

        ulong hash = 0;
        for (var i = 0; i < coefficients.Length; i++)
            if (coefficients[i] > median) hash |= 1UL << i;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
