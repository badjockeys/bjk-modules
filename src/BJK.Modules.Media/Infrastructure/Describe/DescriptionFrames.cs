using System.Diagnostics;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Describe;

/// <summary>
/// Chooses up to <see cref="DescribeOptions.MaxFrames"/> pictures for the description model, always from the
/// <b>blurred</b> processed files (never originals). A video is sampled once a second and CLIP picks clear, different
/// frames: the most unusual one first (the furthest from the clip's average, usually the incident), then each next one
/// the least like those already chosen. Photos are taken in order, skipping near-duplicates. Each picture is a JPEG no
/// longer than <see cref="DescribeOptions.FrameSize"/> on its longest side. Files are only kept in a temporary folder.
/// </summary>
internal sealed class DescriptionFrames(
    IMediaStorage storage,
    ISensitiveContentClassifier classifier,
    IOptions<DescribeOptions> options)
{
    /// <summary>Two pictures more alike than this (CLIP cosine) count as the same view.</summary>
    internal const float SameView = 0.95f;

    public async Task<IReadOnlyList<byte[]>> PickAsync(IReadOnlyList<MediaItem> media, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var folder = Path.Combine(Path.GetTempPath(), $"bjk-describe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var pictures = new List<(byte[] Jpeg, float[]? Embedding)>();
            foreach (var item in media)
            {
                var key = item.PlaybackKey ?? throw new InvalidOperationException($"Media {item.Id} has no processed file.");
                var (bucket, fileKey) = item.IsRestricted ? (MediaBucket.Sensitive, MediaKeys.Sensitive(key)) : (MediaBucket.Public, key);
                var file = Path.Combine(folder, $"{item.Id:N}{Path.GetExtension(key)}");
                await using (var stream = File.Create(file))
                {
                    await storage.DownloadAsync(bucket, fileKey, stream, cancellationToken);
                }

                if (item.MediaType == MediaType.Video)
                {
                    pictures.AddRange(await FromVideoAsync(file, item.DurationSeconds, settings, cancellationToken));
                }
                else
                {
                    var jpeg = await JpegAsync(settings.FfmpegPath, file, null, settings.FrameSize, cancellationToken);
                    pictures.Add((jpeg, await EmbedAsync(settings.FfmpegPath, file, null, cancellationToken)));
                }
            }

            return media.Any(m => m.MediaType == MediaType.Video)
                ? pictures.Select(p => p.Jpeg).ToList()
                : WithoutDuplicates(pictures, settings.MaxFrames);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Photos in order, leaving out any that look like one already chosen.</summary>
    internal static List<byte[]> WithoutDuplicates(IReadOnlyList<(byte[] Jpeg, float[]? Embedding)> pictures, int max)
    {
        var chosen = new List<(byte[] Jpeg, float[]? Embedding)>();
        foreach (var picture in pictures)
        {
            var duplicate = picture.Embedding is { } e
                && chosen.Any(c => c.Embedding is { } other && ClipEmbedding.Dot(e, other) > SameView);
            if (!duplicate)
            {
                chosen.Add(picture);
            }

            if (chosen.Count == max)
            {
                break;
            }
        }

        return chosen.Select(c => c.Jpeg).ToList();
    }

    /// <summary>
    /// Indexes of up to <paramref name="max"/> frames: the furthest from the average first, then each the least like
    /// those already chosen, returned in time order. Without embeddings: evenly spread.
    /// </summary>
    internal static IReadOnlyList<int> Choose(IReadOnlyList<float[]?> embeddings, int max)
    {
        var count = embeddings.Count;
        if (count <= max)
        {
            return Enumerable.Range(0, count).ToList();
        }

        if (embeddings.Any(e => e is null))
        {
            return Enumerable.Range(0, max).Select(i => (int)((long)i * (count - 1) / (max - 1))).Distinct().ToList();
        }

        var vectors = embeddings.Select(e => e!).ToList();
        var average = ClipEmbedding.Average(vectors.Select(v => (v, 1)))!;
        var chosen = new List<int> { Enumerable.Range(0, count).MinBy(i => ClipEmbedding.Dot(vectors[i], average)) };
        while (chosen.Count < max)
        {
            var next = Enumerable.Range(0, count)
                .Where(i => !chosen.Contains(i))
                .MinBy(i => chosen.Max(c => ClipEmbedding.Dot(vectors[i], vectors[c])));
            chosen.Add(next);
        }

        chosen.Sort();
        return chosen;
    }

    private async Task<List<(byte[] Jpeg, float[]? Embedding)>> FromVideoAsync(
        string file,
        decimal? durationSeconds,
        DescribeOptions settings,
        CancellationToken cancellationToken)
    {
        // One small frame a second for choosing (CLIP centre-crops to 224 anyway).
        var embeddings = new List<float[]?>();
        foreach (var frame in await FramesAsync(settings.FfmpegPath, file, "fps=1,scale=336:-2", cancellationToken))
        {
            embeddings.Add(Embed(frame));
        }

        if (embeddings.Count == 0)
        {
            throw new InvalidOperationException("No frames could be read from the video.");
        }

        var result = new List<(byte[] Jpeg, float[]? Embedding)>();
        var length = (double?)durationSeconds ?? embeddings.Count;
        foreach (var index in Choose(embeddings, settings.MaxFrames))
        {
            var seconds = Math.Min(index + 0.5, Math.Max(0, length - 0.1));
            result.Add((await JpegAsync(settings.FfmpegPath, file, seconds, settings.FrameSize, cancellationToken), embeddings[index]));
        }

        return result;
    }

    private float[]? Embed(RgbFrame frame)
    {
        if (!classifier.IsAvailable)
        {
            return null;
        }

        try
        {
            return classifier.Classify(frame).Embedding;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<float[]?> EmbedAsync(string ffmpeg, string file, double? seconds, CancellationToken cancellationToken)
    {
        var frames = await FramesAsync(ffmpeg, file, "scale=336:-2", cancellationToken, seconds, single: true);
        return frames.Count == 0 ? null : Embed(frames[0]);
    }

    private static async Task<List<RgbFrame>> FramesAsync(
        string ffmpeg,
        string file,
        string filter,
        CancellationToken cancellationToken,
        double? seconds = null,
        bool single = false)
    {
        var arguments = new List<string> { "-v", "error" };
        if (seconds is { } at)
        {
            arguments.AddRange(["-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-i", file, "-vf", filter]);
        if (single)
        {
            arguments.AddRange(["-frames:v", "1"]);
        }

        arguments.AddRange(["-f", "image2pipe", "-c:v", "ppm", "pipe:1"]);
        using var process = Start(ffmpeg, arguments);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var reader = new PpmReader(process.StandardOutput.BaseStream);
        var frames = new List<RgbFrame>();
        while (await reader.ReadAsync(cancellationToken) is { } frame)
        {
            frames.Add(frame);
        }

        await Task.WhenAll(errors, process.WaitForExitAsync(cancellationToken));
        return frames;
    }

    private static async Task<byte[]> JpegAsync(string ffmpeg, string file, double? seconds, int size, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-v", "error" };
        if (seconds is { } at)
        {
            arguments.AddRange(["-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange([
            "-i", file, "-frames:v", "1",
            "-vf", $"scale='if(gt(iw,ih),min({size},iw),-2)':'if(gt(iw,ih),-2,min({size},ih))'",
            "-q:v", "3", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1",
        ]);
        using var process = Start(ffmpeg, arguments);
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(copy, errors, process.WaitForExitAsync(cancellationToken));
        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException("ffmpeg couldn't make a picture for the description.");
        }

        return output.ToArray();
    }

    private static Process Start(string ffmpeg, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("ffmpeg couldn't be started.");
    }
}
