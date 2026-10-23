using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// Runs ffprobe and ffmpeg as child processes. Arguments are passed as a list, never through a shell, so nothing in
/// a file can be interpreted as a command.
/// </summary>
internal sealed class FfmpegTranscoder(IOptions<MediaProcessingOptions> options, ILogger<FfmpegTranscoder> logger)
    : IMediaTranscoder
{
    private readonly MediaProcessingOptions _options = options.Value;

    public async Task<ProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            _options.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", inputPath],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new UnreadableMediaException($"ffprobe couldn't read the file: {Tail(result.Error)}");
        }

        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var video = json.RootElement.TryGetProperty("streams", out var streams)
                ? streams.EnumerateArray().FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video")
                : default;
            if (video.ValueKind != JsonValueKind.Object)
            {
                return new ProbeResult(false, null, null, null);
            }

            var width = video.TryGetProperty("width", out var w) ? w.GetInt32() : (int?)null;
            var height = video.TryGetProperty("height", out var h) ? h.GetInt32() : (int?)null;

            // Phones record portrait video as landscape plus a rotation; report the size as it's seen.
            if (IsRotatedSideways(video) && width is not null && height is not null)
            {
                (width, height) = (height, width);
            }

            decimal? duration = null;
            if (json.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var d)
                && decimal.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = Math.Round(seconds, 3);
            }

            double? start = null;
            if (video.TryGetProperty("start_time", out var st)
                && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var startSeconds)
                && double.IsFinite(startSeconds))
            {
                start = startSeconds;
            }

            return new ProbeResult(true, duration, width, height, FrameRate(video), start);
        }
        catch (JsonException ex)
        {
            throw new UnreadableMediaException("ffprobe returned something unexpected.", ex);
        }
    }

    public async Task CreateVideoThumbnailAsync(string playbackPath, string thumbnailPath, CancellationToken cancellationToken)
    {
        // A frame one second in (or the first frame of a shorter clip) as the thumbnail.
        var thumb = $"scale='min({_options.ThumbnailMaxSize},iw)':-2";
        var result = await RunAsync(
            _options.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-ss", "1", "-i", playbackPath, "-frames:v", "1", "-vf", thumb, "-q:v", "3", thumbnailPath],
            cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(thumbnailPath) || new FileInfo(thumbnailPath).Length == 0)
        {
            await RunOrThrowAsync(
                _options.FfmpegPath,
                ["-hide_banner", "-nostdin", "-y", "-i", playbackPath, "-frames:v", "1", "-vf", thumb, "-q:v", "3", thumbnailPath],
                "make the thumbnail",
                cancellationToken);
        }
    }

    public async Task ConvertImageAsync(string inputPath, string imagePath, string thumbnailPath, CancellationToken cancellationToken)
    {
        foreach (var (output, size) in new[] { (imagePath, _options.ImageMaxSize), (thumbnailPath, _options.ThumbnailMaxSize) })
        {
            // Fit inside size x size, never enlarging; ffmpeg applies the EXIF rotation, then -map_metadata drops EXIF.
            var fit = $"scale='min({size},iw)':'min({size},ih)':force_original_aspect_ratio=decrease";
            await RunOrThrowAsync(
                _options.FfmpegPath,
                ["-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map_metadata", "-1", "-frames:v", "1", "-vf", fit,
                 "-c:v", "libwebp", "-quality", "82", output],
                "convert the photo",
                cancellationToken);
        }
    }

    public async Task CreateFeedImageAsync(string inputPath, string feedPath, CancellationToken cancellationToken)
    {
        var size = _options.FeedImageMaxSize;
        var fit = $"scale='min({size},iw)':'min({size},ih)':force_original_aspect_ratio=decrease";
        await RunOrThrowAsync(
            _options.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map_metadata", "-1", "-frames:v", "1", "-vf", fit,
             "-c:v", "libwebp", "-quality", "82", feedPath],
            "make the feed-size photo",
            cancellationToken);
    }

    public async Task CreateSensitivePreviewAsync(string inputPath, string previewPath, CancellationToken cancellationToken)
    {
        // Longest side down to a few dozen pixels (area averaging), blur, back up to the preview size, blur hard again.
        var small = _options.SensitivePreviewSourceSize;
        var large = _options.SensitivePreviewSize;
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"scale='if(gte(iw,ih),{small},-2)':'if(gte(iw,ih),-2,{small})':flags=area,gblur=sigma=3,scale='if(gte(iw,ih),{large},-2)':'if(gte(iw,ih),-2,{large})':flags=bicubic,gblur=sigma=12");
        await RunOrThrowAsync(
            _options.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map_metadata", "-1", "-frames:v", "1", "-vf", filter,
             "-c:v", "libwebp", "-quality", "60", previewPath],
            "make the blurred preview",
            cancellationToken);
    }

    public async Task ConcatenateVideoAsync(
        IReadOnlyList<string> segmentPaths,
        string? audioSourcePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (segmentPaths.Count == 0)
        {
            throw new ArgumentException("There are no segments to join.", nameof(segmentPaths));
        }

        // The concat demuxer's list: one "file" line per segment. The names are ours, but quote them properly anyway.
        var list = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", "segments.txt");
        await File.WriteAllLinesAsync(
            list,
            segmentPaths.Select(p => "file '" + Path.GetFullPath(p).Replace("'", "'\\''", StringComparison.Ordinal) + "'"),
            cancellationToken);

        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", list };
        if (audioSourcePath is not null)
        {
            arguments.AddRange(["-i", audioSourcePath, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-shortest"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0", "-an"]);
        }

        arguments.AddRange(["-c:v", "copy", "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", outputPath]);
        await RunOrThrowAsync(_options.FfmpegPath, arguments, "join the video segments", cancellationToken);
    }

    public async Task CreateShareVideoAsync(
        string playbackPath,
        string watermarkPath,
        string endCardPath,
        string outputPath,
        CancellationToken cancellationToken) =>
        await RunOrThrowAsync(
            _options.FfmpegPath,
            ShareVideoArguments(playbackPath, watermarkPath, endCardPath, outputPath),
            "make the video for sharing",
            cancellationToken);

    /// <summary>The ffmpeg arguments for <see cref="CreateShareVideoAsync"/>.</summary>
    internal static IReadOnlyList<string> ShareVideoArguments(string playbackPath, string watermarkPath, string endCardPath, string outputPath)
    {
        var (w, h) = (ShareVideoAssets.Width, ShareVideoAssets.Height);
        // Background: the video filling the frame, blurred and darkened. Foreground: the whole video, fitted in the
        // middle. The watermark sits bottom right, above where the apps put their own captions and buttons. Then the
        // end card.
        string[] steps =
        [
            "[0:v]fps=30,split=2[a][b]",
            string.Create(CultureInfo.InvariantCulture, $"[a]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur=40:2,eq=brightness=-0.12[bg]"),
            string.Create(CultureInfo.InvariantCulture, $"[b]scale={w}:{h}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2[fg]"),
            "[bg][fg]overlay=(W-w)/2:(H-h)/2[v0]",
            "[v0][1:v]overlay=W-w-48:H-h-220,setsar=1,format=yuv420p[main]",
            string.Create(CultureInfo.InvariantCulture, $"[2:v]scale={w}:{h},setsar=1,format=yuv420p[end]"),
            "[main][end]concat=n=2:v=1:a=0[out]",
        ];
        var filter = string.Join(';', steps);
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", playbackPath,
            "-i", watermarkPath,
            "-loop", "1", "-framerate", "30", "-t", ShareVideoAssets.EndCardSeconds.ToString(CultureInfo.InvariantCulture), "-i", endCardPath,
            "-filter_complex", filter,
            "-map", "[out]", "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-r", "30", "-pix_fmt", "yuv420p",
            "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart",
            outputPath,
        ];
    }

    /// <summary>Average frame rate ("30000/1001"), falling back to the nominal rate; null if neither makes sense.</summary>
    internal static double? FrameRate(JsonElement video)
    {
        foreach (var name in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            if (video.TryGetProperty(name, out var value) && value.GetString() is { } text)
            {
                var parts = text.Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
                    && denominator > 0
                    && numerator / denominator is > 0 and < 1000 and var rate)
                {
                    return rate;
                }
            }
        }

        return null;
    }

    private static bool IsRotatedSideways(JsonElement video)
    {
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
            && int.TryParse(rotate.GetString(), out var degrees))
        {
            return Math.Abs(degrees) % 180 == 90;
        }

        if (video.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var r))
                {
                    return Math.Abs((int)r) % 180 == 90;
                }
            }
        }

        return false;
    }

    private async Task RunOrThrowAsync(string executable, IReadOnlyList<string> arguments, string what, CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new UnreadableMediaException($"ffmpeg couldn't {what}: {Tail(result.Error)}");
        }
    }

    private async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { error.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Couldn't start '{executable}'. Is ffmpeg installed? Set Media:Processing:FfmpegPath.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{Path.GetFileName(executable)} took longer than {_options.CommandTimeout}.");
        }

        // Make sure the asynchronous readers have finished.
        process.WaitForExit();
        logger.LogDebug("{Executable} exited with {ExitCode}.", Path.GetFileName(executable), process.ExitCode);
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no details" : string.Join(" | ", lines.TakeLast(3));
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
