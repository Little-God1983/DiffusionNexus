using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Computes perceptual hashes (pHash) for images using a naive 2D DCT approach.
/// The algorithm resizes the image to 32×32 grayscale, applies a 2D Discrete Cosine
/// Transform, takes the top-left 8×8 low-frequency coefficients, and median-thresholds
/// them into a 64-bit hash. Two images are considered near-duplicates when their
/// Hamming distance is below <see cref="NearDuplicateThreshold"/>.
/// </summary>
public static class PerceptualHasher
{
    /// <summary>Size to resize images to before DCT (32×32).</summary>
    internal const int ResizeSize = 32;

    /// <summary>Size of the low-frequency DCT block to keep (8×8 → 64 bits).</summary>
    internal const int HashBlockSize = 8;

    /// <summary>Hamming distance below which two images are near-duplicates.</summary>
    public const int NearDuplicateThreshold = 5;

    /// <summary>
    /// Computes a 64-bit perceptual hash for the image at <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to an image file.</param>
    /// <returns>A 64-bit perceptual hash.</returns>
    public static ulong ComputeHash(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        using var image = Image.Load<L8>(filePath);

        // Resize to 32×32 grayscale
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(ResizeSize, ResizeSize),
            Mode = ResizeMode.Stretch
        }));

        // Extract pixel values as doubles
        double[,] pixels = new double[ResizeSize, ResizeSize];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < ResizeSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < ResizeSize; x++)
                {
                    pixels[y, x] = row[x].PackedValue;
                }
            }
        });

        // Apply 2D DCT
        double[,] dct = ComputeDct2D(pixels);

        // Extract top-left 8×8 low-frequency coefficients (skip DC at [0,0])
        double[] coefficients = new double[HashBlockSize * HashBlockSize];
        int idx = 0;
        for (int y = 0; y < HashBlockSize; y++)
        {
            for (int x = 0; x < HashBlockSize; x++)
            {
                coefficients[idx++] = dct[y, x];
            }
        }

        // Compute median (excluding DC component at index 0)
        double median = ComputeMedian(coefficients, startIndex: 1);

        // Build 64-bit hash: bit = 1 if coefficient >= median
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (coefficients[i] >= median)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    /// <summary>
    /// Computes the Hamming distance between two 64-bit perceptual hashes.
    /// </summary>
    /// <param name="hashA">First hash.</param>
    /// <param name="hashB">Second hash.</param>
    /// <returns>Number of differing bits (0–64).</returns>
    public static int HammingDistance(ulong hashA, ulong hashB)
    {
        return System.Numerics.BitOperations.PopCount(hashA ^ hashB);
    }

    /// <summary>
    /// Returns a similarity percentage (0–100) from a Hamming distance.
    /// 0 distance → 100%, 64 distance → 0%.
    /// </summary>
    /// <param name="distance">Hamming distance (0–64).</param>
    /// <returns>Similarity as a percentage.</returns>
    public static double SimilarityPercent(int distance)
    {
        return Math.Round((1.0 - distance / 64.0) * 100, 1);
    }

    /// <summary>
    /// Naive 2D DCT-II implementation. Acceptable for 32×32 inputs.
    /// </summary>
    internal static double[,] ComputeDct2D(double[,] input)
    {
        int n = input.GetLength(0);
        double[,] temp = new double[n, n];
        double[,] result = new double[n, n];

        // DCT on rows
        for (int y = 0; y < n; y++)
        {
            for (int u = 0; u < n; u++)
            {
                double sum = 0;
                for (int x = 0; x < n; x++)
                {
                    sum += input[y, x] * Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * n));
                }
                temp[y, u] = sum;
            }
        }

        // DCT on columns
        for (int u = 0; u < n; u++)
        {
            for (int v = 0; v < n; v++)
            {
                double sum = 0;
                for (int y = 0; y < n; y++)
                {
                    sum += temp[y, u] * Math.Cos((2 * y + 1) * v * Math.PI / (2.0 * n));
                }
                result[v, u] = sum;
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the median of a subset of the array starting at <paramref name="startIndex"/>.
    /// </summary>
    internal static double ComputeMedian(double[] values, int startIndex = 0)
    {
        int count = values.Length - startIndex;
        if (count <= 0)
            return 0;

        double[] subset = new double[count];
        Array.Copy(values, startIndex, subset, 0, count);
        Array.Sort(subset);

        if (count % 2 == 0)
            return (subset[count / 2 - 1] + subset[count / 2]) / 2.0;

        return subset[count / 2];
    }
}
