using BJK.Modules.Media.Infrastructure.Processing.Redaction;

namespace BJK.Modules.Media.Infrastructure.Processing.Safety;

/// <summary>
/// A 256-bit PDQ hash. Bit <c>k</c> is <c>(row * 16) + column</c> of the 16 x 16 DCT; <see cref="Hex"/> is the
/// standard 64-digit form (the 256-bit number with bit <c>k</c> worth 2^k, most significant digit first), which is how
/// hash lists (IWF, StopNCII, NCMEC, ThreatExchange) share PDQ hashes.
/// </summary>
public sealed class PdqHash : IEquatable<PdqHash>
{
    private readonly ulong[] _words;

    private PdqHash(ulong[] words) => _words = words;

    /// <summary>From the 256 bits, bit k at index k.</summary>
    public static PdqHash FromBits(bool[] bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        if (bits.Length != 256)
        {
            throw new ArgumentException("A PDQ hash has 256 bits.", nameof(bits));
        }

        var words = new ulong[4];
        for (var k = 0; k < 256; k++)
        {
            if (bits[k])
            {
                words[k >> 6] |= 1UL << (k & 63);
            }
        }

        return new PdqHash(words);
    }

    /// <summary>From the 64 hex digits; null if it isn't one.</summary>
    public static PdqHash? FromHex(string? hex)
    {
        var value = hex?.Trim();
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
        {
            return null;
        }

        var words = new ulong[4];
        for (var w = 0; w < 4; w++)
        {
            // The last 16 digits are bits 0-63, and so on.
            words[w] = Convert.ToUInt64(value.Substring(64 - ((w + 1) * 16), 16), 16);
        }

        return new PdqHash(words);
    }

    /// <summary>The four 64-bit words, bits 0-63 first.</summary>
    internal ReadOnlySpan<ulong> Words => _words;

    public bool this[int bit] => (_words[bit >> 6] & (1UL << (bit & 63))) != 0;

    public string Hex => string.Concat(_words.Reverse().Select(w => w.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>How many bits differ (0 to 256). Lists usually call 31 or less a match.</summary>
    public int Distance(PdqHash other) =>
        System.Numerics.BitOperations.PopCount(_words[0] ^ other._words[0])
        + System.Numerics.BitOperations.PopCount(_words[1] ^ other._words[1])
        + System.Numerics.BitOperations.PopCount(_words[2] ^ other._words[2])
        + System.Numerics.BitOperations.PopCount(_words[3] ^ other._words[3]);

    public bool Equals(PdqHash? other) => other is not null && _words.AsSpan().SequenceEqual(other._words);

    public override bool Equals(object? obj) => Equals(obj as PdqHash);

    public override int GetHashCode() => HashCode.Combine(_words[0], _words[1], _words[2], _words[3]);

    public override string ToString() => Hex;
}

/// <summary>
/// Meta's PDQ perceptual hash (open source, BSD), as in the reference implementation: luma, two passes of a Jarosz
/// (box) filter in each direction, decimation to 64 x 64, the 16 x 16 low-frequency DCT (skipping the DC terms), and a
/// bit per coefficient above the median. Near-identical pictures (resized, recompressed, slightly cropped) get hashes a
/// few bits apart, so known images can be recognised without anyone looking at them. <c>Quality</c> (0 to 100) is low
/// for flat pictures, whose hashes match too easily; lists ignore hashes below 50.
/// </summary>
public static class Pdq
{
    private const int Size = 64;
    private const int Dct = 16;
    private const int JaroszPasses = 2;

    private static readonly float[,] DctMatrix = BuildDct();

    /// <summary>The hash and quality of a picture.</summary>
    public static (PdqHash Hash, int Quality) Compute(RgbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (rows, cols) = (frame.Height, frame.Width);
        var luma = new float[rows * cols];
        var pixels = frame.Pixels;
        for (var i = 0; i < luma.Length; i++)
        {
            luma[i] = (0.299f * pixels[i * 3]) + (0.587f * pixels[(i * 3) + 1]) + (0.114f * pixels[(i * 3) + 2]);
        }

        var buffer = new float[rows * cols];
        JaroszFilter(luma, buffer, rows, cols, WindowSize(cols, Size), WindowSize(rows, Size));

        var small = new float[Size, Size];
        for (var i = 0; i < Size; i++)
        {
            var ini = (int)(((i + 0.5) * rows) / Size);
            for (var j = 0; j < Size; j++)
            {
                var inj = (int)(((j + 0.5) * cols) / Size);
                small[i, j] = luma[(ini * cols) + inj];
            }
        }

        var quality = Quality(small);
        var dct = Dct16(small);
        var values = new float[Dct * Dct];
        for (var i = 0; i < Dct; i++)
        {
            for (var j = 0; j < Dct; j++)
            {
                values[(i * Dct) + j] = dct[i, j];
            }
        }

        var median = LowerMedian(values);
        var bits = new bool[256];
        for (var k = 0; k < 256; k++)
        {
            bits[k] = values[k] > median;
        }

        return (PdqHash.FromBits(bits), quality);
    }

    /// <summary>The reference's window: (old + 2 x new - 1) / (2 x new), in integers.</summary>
    internal static int WindowSize(int oldDimension, int newDimension) => (oldDimension + (2 * newDimension) - 1) / (2 * newDimension);

    private static void JaroszFilter(float[] data, float[] temp, int rows, int cols, int windowAlongRows, int windowAlongCols)
    {
        for (var pass = 0; pass < JaroszPasses; pass++)
        {
            for (var i = 0; i < rows; i++)
            {
                Box1D(data, temp, i * cols, cols, 1, windowAlongRows);
            }

            for (var j = 0; j < cols; j++)
            {
                Box1D(temp, data, j, rows, cols, windowAlongCols);
            }
        }
    }

    /// <summary>The reference's one-dimensional box filter, in its four phases.</summary>
    private static void Box1D(float[] input, float[] output, int start, int length, int stride, int fullWindow)
    {
        var halfWindow = (fullWindow + 2) / 2;
        var phase1 = halfWindow - 1;
        var phase2 = fullWindow - halfWindow + 1;
        var phase3 = length - fullWindow;
        var phase4 = halfWindow - 1;
        int li = start, ri = start, oi = start;
        var sum = 0f;
        var window = 0;
        for (var i = 0; i < phase1; i++)
        {
            sum += input[ri];
            window++;
            ri += stride;
        }

        for (var i = 0; i < phase2; i++)
        {
            sum += input[ri];
            window++;
            output[oi] = sum / window;
            ri += stride;
            oi += stride;
        }

        for (var i = 0; i < phase3; i++)
        {
            sum += input[ri];
            sum -= input[li];
            output[oi] = sum / window;
            li += stride;
            ri += stride;
            oi += stride;
        }

        for (var i = 0; i < phase4; i++)
        {
            sum -= input[li];
            window--;
            output[oi] = sum / window;
            li += stride;
            oi += stride;
        }
    }

    private static int Quality(float[,] small)
    {
        var gradientSum = 0;
        for (var i = 0; i < Size - 1; i++)
        {
            for (var j = 0; j < Size; j++)
            {
                gradientSum += Math.Abs((int)(((small[i, j] - small[i + 1, j]) * 100) / 255));
            }
        }

        for (var i = 0; i < Size; i++)
        {
            for (var j = 0; j < Size - 1; j++)
            {
                gradientSum += Math.Abs((int)(((small[i, j] - small[i, j + 1]) * 100) / 255));
            }
        }

        return Math.Min(100, gradientSum / 90);
    }

    private static float[,] Dct16(float[,] a)
    {
        var t = new float[Dct, Size];
        for (var i = 0; i < Dct; i++)
        {
            for (var j = 0; j < Size; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Size; k++)
                {
                    sum += DctMatrix[i, k] * a[k, j];
                }

                t[i, j] = sum;
            }
        }

        var b = new float[Dct, Dct];
        for (var i = 0; i < Dct; i++)
        {
            for (var j = 0; j < Dct; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Size; k++)
                {
                    sum += t[i, k] * DctMatrix[j, k];
                }

                b[i, j] = sum;
            }
        }

        return b;
    }

    private static float[,] BuildDct()
    {
        var d = new float[Dct, Size];
        var scale = Math.Sqrt(2.0 / Size);
        for (var i = 0; i < Dct; i++)
        {
            for (var j = 0; j < Size; j++)
            {
                d[i, j] = (float)(scale * Math.Cos(Math.PI / 2 / Size * (i + 1) * ((2 * j) + 1)));
            }
        }

        return d;
    }

    /// <summary>The 128th smallest of the 256 values (Torben's median, as the reference uses).</summary>
    private static float LowerMedian(float[] values)
    {
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        return sorted[(sorted.Length - 1) / 2];
    }
}
