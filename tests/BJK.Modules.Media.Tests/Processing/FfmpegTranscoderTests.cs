using System.Diagnostics;
using BJK.Modules.Media.Infrastructure.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>Runs the real ffmpeg. Skipped where it isn't installed; CI installs it.</summary>
public sealed class FfmpegTranscoderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-ffmpeg-tests-").FullName;
    private readonly FfmpegTranscoder _transcoder = new(
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<FfmpegTranscoder>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void RequireFfmpeg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.WaitForExit();
            if (process?.ExitCode == 0)
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        Assert.Skip("ffmpeg isn't installed.");
    }

    private string Make(string name, params string[] arguments)
    {
        var path = Path.Combine(_folder, name);
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-y" }.Concat(arguments).Append(path))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    [Fact]
    public async Task Probe_reads_size_duration_and_frame_rate()
    {
        RequireFfmpeg();
        var input = Make(
            "input.mp4",
            "-f", "lavfi", "-i", "testsrc=size=1920x1080:rate=60:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast");

        var probe = await _transcoder.ProbeAsync(input, Ct);

        Assert.True(probe.HasVideo);
        Assert.Equal((1920, 1080), (probe.Width, probe.Height));
        Assert.InRange(probe.DurationSeconds ?? 0, 2.9m, 3.2m);
        Assert.Equal(60, probe.FrameRate);
    }

    [Fact]
    public async Task A_short_video_still_gets_a_thumbnail()
    {
        RequireFfmpeg();
        var input = Make(
            "small.mp4",
            "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=25:duration=0.5",
            "-c:v", "libx264", "-preset", "ultrafast");

        var thumbnail = Path.Combine(_folder, "thumbnail.jpg");
        await _transcoder.CreateVideoThumbnailAsync(input, thumbnail, Ct);

        var thumb = await _transcoder.ProbeAsync(thumbnail, Ct);
        Assert.Equal((640, 360), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task A_large_photo_becomes_a_smaller_webp_and_thumbnail()
    {
        RequireFfmpeg();
        var input = Make("photo.png", "-f", "lavfi", "-i", "testsrc=size=4000x3000:duration=1", "-frames:v", "1");

        var image = Path.Combine(_folder, "image.webp");
        var thumbnail = Path.Combine(_folder, "thumbnail.webp");
        await _transcoder.ConvertImageAsync(input, image, thumbnail, Ct);

        var converted = await _transcoder.ProbeAsync(image, Ct);
        Assert.Equal((2048, 1536), (converted.Width, converted.Height));
        var thumb = await _transcoder.ProbeAsync(thumbnail, Ct);
        Assert.Equal((640, 480), (thumb.Width, thumb.Height));
    }

    /// <summary>The photo types added on 21 Oct 2026. An animated GIF is read from its first frame.</summary>
    [Theory]
    [InlineData("photo.gif", new[] { "-frames:v", "1" })]
    [InlineData("animated.gif", new[] { "-r", "5" })]
    [InlineData("photo.tiff", new[] { "-frames:v", "1" })]
    [InlineData("photo.bmp", new[] { "-frames:v", "1" })]
    [InlineData("photo.avif", new[] { "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "8" })]
    public async Task Newly_accepted_photo_types_become_webp(string name, string[] encode)
    {
        RequireFfmpeg();
        var input = Make(name, ["-f", "lavfi", "-i", "testsrc=size=2400x1800:rate=5:duration=1", .. encode]);

        var image = Path.Combine(_folder, "image.webp");
        var thumbnail = Path.Combine(_folder, "thumbnail.webp");
        await _transcoder.ConvertImageAsync(input, image, thumbnail, Ct);

        var converted = await _transcoder.ProbeAsync(image, Ct);
        Assert.Equal((2048, 1536), (converted.Width, converted.Height));
        Assert.Contains("codec_name=webp", await FfmpegTestSupport.Ffprobe(image));
        var thumb = await _transcoder.ProbeAsync(thumbnail, Ct);
        Assert.Equal((640, 480), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task Photos_get_a_1080_pixel_feed_copy_never_enlarged()
    {
        RequireFfmpeg();
        var large = Make("large.png", "-f", "lavfi", "-i", "testsrc=size=3000x4000:duration=1", "-frames:v", "1");
        var small = Make("small.png", "-f", "lavfi", "-i", "testsrc=size=800x600:duration=1", "-frames:v", "1");

        await _transcoder.CreateFeedImageAsync(large, Path.Combine(_folder, "feed-large.webp"), Ct);
        await _transcoder.CreateFeedImageAsync(small, Path.Combine(_folder, "feed-small.webp"), Ct);

        var feedLarge = await _transcoder.ProbeAsync(Path.Combine(_folder, "feed-large.webp"), Ct);
        var feedSmall = await _transcoder.ProbeAsync(Path.Combine(_folder, "feed-small.webp"), Ct);
        Assert.Equal((810, 1080), (feedLarge.Width, feedLarge.Height));
        Assert.Equal((800, 600), (feedSmall.Width, feedSmall.Height));
        Assert.Contains("codec_name=webp", await FfmpegTestSupport.Ffprobe(Path.Combine(_folder, "feed-large.webp")));
    }

    [Fact]
    public async Task The_sensitive_preview_is_a_small_heavily_blurred_webp()
    {
        RequireFfmpeg();
        // A 16-pixel checkerboard: full of detail, which the preview must destroy.
        var thumbnail = Make("thumb.jpg", "-f", "lavfi", "-i", $"color=c=gray:s=640x360,format=yuv420p,{FfmpegTestSupport.Checkerboard}", "-frames:v", "1");
        var preview = Path.Combine(_folder, "preview.webp");

        await _transcoder.CreateSensitivePreviewAsync(thumbnail, preview, Ct);

        var probe = await _transcoder.ProbeAsync(preview, Ct);
        Assert.Equal((480, 270), (probe.Width, probe.Height));
        Assert.True(new FileInfo(preview).Length < 10_000, "A heavily blurred picture compresses to almost nothing.");
        var frame = Assert.Single(await FfmpegTestSupport.Frames(preview));
        Assert.InRange(FfmpegTestSupport.Detail(frame, 0, 0, 480, 270), 0, 8);
    }

    [Fact]
    public async Task A_file_that_is_not_media_is_unreadable()
    {
        RequireFfmpeg();
        var path = Path.Combine(_folder, "notes.mp4");
        await File.WriteAllTextAsync(path, "not a video", Ct);

        await Assert.ThrowsAsync<UnreadableMediaException>(() => _transcoder.ProbeAsync(path, Ct));
    }

    private static async Task<string> RunFfprobe(string path)
    {
        var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-show_format", "-show_streams", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
