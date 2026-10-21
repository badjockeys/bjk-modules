namespace BJK.Modules.Media.Contracts;

/// <summary>
/// CLIP image embeddings (512 numbers from the media worker's CLIP ViT-B/32 model, scaled to length 1). Each media item
/// keeps one averaged summary, about 2 KB, for type suggestions and to train our own type model later. Never pictures.
/// </summary>
public static class ClipEmbedding
{
    /// <summary>CLIP ViT-B/32's embedding size.</summary>
    public const int Size = 512;

    /// <summary>The vector scaled to length 1 (unchanged if it's all zeros).</summary>
    public static float[] Normalise(ReadOnlySpan<float> vector)
    {
        var result = vector.ToArray();
        var length = MathF.Sqrt(result.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] /= length;
            }
        }

        return result;
    }

    /// <summary>The weighted average of several summaries (each with how many frames went into it), normalised.</summary>
    public static float[]? Average(IEnumerable<(float[] Vector, int Weight)> parts)
    {
        float[]? sum = null;
        foreach (var (vector, weight) in parts)
        {
            if (weight <= 0 || vector.Length == 0)
            {
                continue;
            }

            sum ??= new float[vector.Length];
            if (vector.Length != sum.Length)
            {
                continue;
            }

            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vector[i] * weight;
            }
        }

        return sum is null ? null : Normalise(sum);
    }

    /// <summary>Cosine similarity of two vectors of length 1.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var total = 0f;
        var count = Math.Min(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>For storage: little-endian float32s.</summary>
    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>From storage.</summary>
    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}
