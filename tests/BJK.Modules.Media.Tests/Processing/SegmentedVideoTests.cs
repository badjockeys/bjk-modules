using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BJK.Modules.Media.Tests.Processing.FfmpegTestSupport;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>
/// Splitting a video into segments, blurring each and joining them, with the real ffmpeg on a synthetic 12-second
/// clip: a checkerboard (so blur is measurable), a green square that moves 4 pixels every frame (so a dropped or
/// repeated frame at a join shows) and a red square, the stand-in "face", that disappears for a third of a second
/// just before the first join (frames 147 to 157). Skipped where ffmpeg isn't installed; CI installs it.
/// </summary>
public sealed class SegmentedVideoTests : IDisposable
{
    private const int Fps = 30;

    // The stand-in face box around the red square (40x40 at 280,200), and a checkerboard patch inside it to measure.
    private static readonly DetectionBox FaceBox = new(RedactionTarget.Face, 240, 170, 120, 100, 0.95f);
    private static readonly (int Left, int Top, int Right, int Bottom) Patch = (250, 248, 350, 266);

    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-segment-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string TestClip() => Make(
        Path.Combine(_folder, "clip.mp4"),
        "-f", "lavfi",
        "-i", $"color=c=gray:s=640x360:r={Fps}:d=12,format=yuv420p,{Checkerboard}[bg];" +
              $"color=c=0x00FF00:s=16x16:r={Fps}:d=12[g];color=c=0xFF0000:s=40x40:r={Fps}:d=12[r];" +
              "[bg][g]overlay=x='mod(n*4,560)':y=8[a];[a][r]overlay=x=280:y=200:enable='not(between(t,4.9,5.25))'",
        "-c:v", "libx264", "-preset", "ultrafast");

    /// <summary>Finds the red square, if it's showing, and reports the face box around it.</summary>
    private static FakeDetector RedSquareDetector() => new(RedactionTarget.Face, frame =>
    {
        var red = 0;
        for (var y = 205; y < 235; y++)
        {
            for (var x = 285; x < 315; x++)
            {
                var i = (y * frame.Stride) + (x * 3);
                if (frame.Pixels[i] > 150 && frame.Pixels[i + 1] < 100 && frame.Pixels[i + 2] < 100)
                {
                    red++;
                }
            }
        }

        return red > 400 ? [FaceBox] : [];
    });

    private static MediaRedactor Redactor(params IRedactionDetector[] detectors) => new(
        detectors,
        Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<MediaRedactor>.Instance);

    private static FfmpegTranscoder Transcoder() => new(
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<FfmpegTranscoder>.Instance);

    /// <summary>Blurs every segment of the clip in turn, as separate workers would, and joins them.</summary>
    private async Task<(string Joined, List<RedactionResult> Segments)> SplitBlurAndJoin(string clip, VideoSegmentPlan plan)
    {
        var redactor = Redactor(RedSquareDetector(), FakeDetector.Nothing(RedactionTarget.NumberPlate));
        var parts = new List<string>();
        var results = new List<RedactionResult>();
        for (var i = 0; i < plan.SegmentCount; i++)
        {
            var part = Path.Combine(_folder, $"{i:D3}.mp4");
            results.Add(await redactor.RedactVideoSegmentAsync(clip, plan, i, [], part, Ct));
            parts.Add(part);
        }

        var joined = Path.Combine(_folder, "720p.mp4");
        await Transcoder().ConcatenateVideoAsync(parts, null, joined, Ct);
        return (joined, results);
    }

    /// <summary>The green square's centre on its row, or -1 if it can't be seen.</summary>
    private static double GreenCentre(RgbFrame frame)
    {
        var xs = new List<int>();
        for (var x = 0; x < frame.Width; x++)
        {
            var i = (16 * frame.Stride) + (x * 3);
            if (frame.Pixels[i + 1] > 150 && frame.Pixels[i] < 110 && frame.Pixels[i + 2] < 110)
            {
                xs.Add(x);
            }
        }

        return xs.Count == 0 ? -1 : (xs.Min() + xs.Max()) / 2.0;
    }

    [Fact]
    public async Task A_12_second_clip_is_split_blurred_and_joined_without_losing_or_repeating_a_frame()
    {
        RequireFfmpeg();
        var clip = TestClip();
        var probe = await Transcoder().ProbeAsync(clip, Ct);
        var plan = VideoSegmentPlan.For((double)probe.DurationSeconds!.Value, MediaRedactor.OutputFrameRate(probe.FrameRate), probe.StartSeconds ?? 0, 5, 1.0);
        Assert.Equal(3, plan.SegmentCount);

        var (joined, segments) = await SplitBlurAndJoin(clip, plan);

        Assert.Equal(new[] { 150, 150, 60 }, segments.Select(s => s.Frames));
        Assert.All(segments, s => Assert.False(s.NeedsReview, string.Join(" ", s.ReviewReasons)));

        // Analysis includes the overlap: segment 1 decodes frames 120 to 329, so it ran detection on 42 of them.
        Assert.Equal(42, segments[1].FramesAnalysed);

        Assert.Equal(360, await CountFrames(joined));
        var info = await Ffprobe(joined);
        Assert.Contains("codec_name=h264", info);
        Assert.Contains("avg_frame_rate=30/1", info);
        Assert.Contains("width=640", info);
        Assert.DoesNotContain("codec_type=audio", info);
        Assert.Matches(@"duration=12\.0", info);

        // Every frame is where it should be either side of the joins (frames 150 and 300).
        int[] indices = [0, 148, 149, 150, 151, 298, 299, 300, 301, 359];
        var frames = await FramesAt(joined, indices);
        var origin = GreenCentre(frames[0]);
        for (var k = 0; k < indices.Length; k++)
        {
            var expected = origin + ((indices[k] * 4) % 560);
            Assert.InRange(GreenCentre(frames[k]), expected - 1.5, expected + 1.5);
        }
    }

    [Fact]
    public async Task Blurs_continue_across_a_join_thanks_to_the_overlap()
    {
        RequireFfmpeg();
        var clip = TestClip();
        var plan = VideoSegmentPlan.For(12, Fps, 0, 5, overlapSeconds: 1.0);

        var (joined, _) = await SplitBlurAndJoin(clip, plan);

        // The face is seen up to frame 145, missed on the passes at 150 and 155 (it's hidden), and seen again at 160.
        // The tracker carries the blur through the gap on both sides of the join, as it would for the whole video.
        int[] around = [140, 146, 149, 150, 152, 154, 157, 160, 170];
        var frames = await FramesAt(joined, around);
        for (var k = 0; k < around.Length; k++)
        {
            Assert.True(
                Detail(frames[k], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) < 30,
                $"Frame {around[k]} isn't blurred.");
        }

        // Far from the face, nothing is blurred.
        Assert.InRange(Detail(frames[0], 450, 250, 600, 340), 80, 255);
    }

    [Fact]
    public async Task Without_the_overlap_the_blur_would_drop_at_the_join()
    {
        RequireFfmpeg();
        var clip = TestClip();
        var plan = VideoSegmentPlan.For(12, Fps, 0, 5, overlapSeconds: 0);

        var (joined, _) = await SplitBlurAndJoin(clip, plan);

        // Segment 1 starts blind at frame 150 and first sees the face at 160 (blurring from one pass earlier, 155),
        // so frames 150 to 154 show it: the reason segments analyse an overlap.
        var frames = await FramesAt(joined, 149, 150, 152, 154, 156);
        Assert.True(Detail(frames[0], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) < 30);
        Assert.True(Detail(frames[1], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) > 80);
        Assert.True(Detail(frames[3], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) > 80);
        Assert.True(Detail(frames[4], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) < 30);
    }

    [Fact]
    public async Task The_segments_match_a_whole_video_pass_frame_for_frame_at_the_joins()
    {
        RequireFfmpeg();
        var clip = TestClip();
        var plan = VideoSegmentPlan.For(12, Fps, 0, 5, 1.0);
        var (joined, _) = await SplitBlurAndJoin(clip, plan);

        var whole = Path.Combine(_folder, "whole.mp4");
        await Redactor(RedSquareDetector(), FakeDetector.Nothing(RedactionTarget.NumberPlate))
            .RedactVideoAsync(clip, new ProbeResult(true, 12m, 640, 360, Fps, 0), [], whole, Ct);

        int[] indices = [149, 150, 155, 299, 300];
        var a = await FramesAt(joined, indices);
        var b = await FramesAt(whole, indices);
        for (var k = 0; k < indices.Length; k++)
        {
            // Same frame, same blur (allowing for two separate lossy encodes).
            Assert.InRange(GreenCentre(a[k]) - GreenCentre(b[k]), -1.5, 1.5);
            var blurredA = Detail(a[k], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) < 30;
            var blurredB = Detail(b[k], Patch.Left, Patch.Top, Patch.Right, Patch.Bottom) < 30;
            Assert.Equal(blurredB, blurredA);
        }
    }
}
