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

            return new ProbeResult(true, duration, width, height);
        }
        catch (JsonException ex)
        {
            throw new UnreadableMediaException("ffprobe returned something unexpected.", ex);
        }
    }

    public async Task TranscodeVideoAsync(string inputPath, string playbackPath, string thumbnailPath, CancellationToken cancellationToken)
    {
        // Scale down to VideoHeight (never up), keep even dimensions for H.264, 30 fps at most, fast start for streaming.
        var scale = $"scale=-2:'min({_options.VideoHeight},ih)':flags=lanczos";
        await RunOrThrowAsync(
            _options.FfmpegPath,
            [
                "-hide_banner", "-nostdin", "-y", "-i", inputPath,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-map_metadata", "-1", "-map_chapters", "-1",
                "-vf", scale, "-fpsmax", "30",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", _options.VideoCrf.ToString(CultureInfo.InvariantCulture),
                "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                "-movflags", "+faststart",
                playbackPath,
            ],
            "encode the video",
            cancellationToken);

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
