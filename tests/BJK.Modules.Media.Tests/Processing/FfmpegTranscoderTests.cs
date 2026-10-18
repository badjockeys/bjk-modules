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
    public async Task A_1080p_video_with_gps_metadata_becomes_720p_without_it()
    {
        RequireFfmpeg();
        var input = Make(
            "input.mp4",
            "-f", "lavfi", "-i", "testsrc=size=1920x1080:rate=60:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-metadata", "location=+53.7457-001.7181/",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest");

        var probe = await _transcoder.ProbeAsync(input, Ct);
        Assert.True(probe.HasVideo);
        Assert.Equal((1920, 1080), (probe.Width, probe.Height));
        Assert.InRange(probe.DurationSeconds ?? 0, 2.9m, 3.2m);

        var playback = Path.Combine(_folder, "720p.mp4");
        var thumbnail = Path.Combine(_folder, "thumbnail.jpg");
        await _transcoder.TranscodeVideoAsync(input, playback, thumbnail, Ct);

        var output = await _transcoder.ProbeAsync(playback, Ct);
        Assert.Equal((1280, 720), (output.Width, output.Height));
        Assert.True(new FileInfo(thumbnail).Length > 0);
        Assert.DoesNotContain("53.7457", await RunFfprobe(playback));
    }

    [Fact]
    public async Task A_small_video_is_not_enlarged_and_a_short_one_still_gets_a_thumbnail()
    {
        RequireFfmpeg();
        var input = Make(
            "small.mp4",
            "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25:duration=0.5",
            "-c:v", "libx264", "-preset", "ultrafast");

        var playback = Path.Combine(_folder, "720p.mp4");
        var thumbnail = Path.Combine(_folder, "thumbnail.jpg");
        await _transcoder.TranscodeVideoAsync(input, playback, thumbnail, Ct);

        var output = await _transcoder.ProbeAsync(playback, Ct);
        Assert.Equal((640, 360), (output.Width, output.Height));
        Assert.True(new FileInfo(thumbnail).Length > 0);
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
