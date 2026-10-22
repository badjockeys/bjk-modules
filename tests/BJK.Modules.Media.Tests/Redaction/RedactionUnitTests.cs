using System.Text;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Xunit;

namespace BJK.Modules.Media.Tests.Redaction;

public sealed class RedactionTrackerTests
{
    private static DetectionBox Face(float x, float y = 100, float size = 50, float confidence = 0.9f) =>
        new(RedactionTarget.Face, x, y, size, size, confidence);

    private static DetectionBox Plate(float x, float y = 300, float confidence = 0.9f) =>
        new(RedactionTarget.NumberPlate, x, y, 50, 15, confidence);

    [Fact]
    public void Boxes_between_detection_passes_are_interpolated()
    {
        var tracker = new RedactionTracker(maxMissed: 2);
        tracker.Add(0, [Face(100)]);
        tracker.Add(10, [Face(120)]);

        var box = Assert.Single(tracker.BoxesAt(5, interval: 10));

        Assert.Equal(110, box.X, 3);
        Assert.Single(tracker.Tracks);
    }

    [Fact]
    public void A_face_missed_by_a_few_passes_stays_blurred_across_the_gap()
    {
        var tracker = new RedactionTracker(maxMissed: 3);
        tracker.Add(0, [Face(100)]);
        tracker.Add(5, []);
        tracker.Add(10, []);
        tracker.Add(15, [Face(130)]);

        Assert.Single(tracker.Tracks);
        foreach (var frame in Enumerable.Range(0, 16))
        {
            var box = Assert.Single(tracker.BoxesAt(frame, interval: 5));
            Assert.Equal(100 + (frame * 2), box.X, 3);
        }
    }

    [Fact]
    public void A_face_missed_by_too_many_passes_becomes_a_new_track()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(0, [Face(100)]);
        tracker.Add(5, []);
        tracker.Add(10, []);
        tracker.Add(15, [Face(100)]);

        Assert.Equal(2, tracker.Tracks.Count);
        Assert.Empty(tracker.BoxesAt(8, interval: 5));
    }

    [Fact]
    public void Tracks_are_extended_by_one_interval_at_each_end_following_their_motion()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(10, [Face(100)]);
        tracker.Add(15, [Face(110)]);

        Assert.Empty(tracker.BoxesAt(4, interval: 5));
        Assert.Equal(90, Assert.Single(tracker.BoxesAt(5, interval: 5)).X, 3);
        Assert.Equal(120, Assert.Single(tracker.BoxesAt(20, interval: 5)).X, 3);
        Assert.Empty(tracker.BoxesAt(21, interval: 5));
    }

    [Fact]
    public void A_fast_mover_with_little_overlap_is_still_the_same_track()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(0, [Face(100)]);
        tracker.Add(5, [Face(160)]);

        Assert.Single(tracker.Tracks);
    }

    [Fact]
    public void Distant_faces_and_plates_are_separate_tracks()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(0, [Face(100), Face(900), new DetectionBox(RedactionTarget.NumberPlate, 100, 100, 50, 50, 0.9f)]);
        tracker.Add(5, [Face(102), Face(902), new DetectionBox(RedactionTarget.NumberPlate, 102, 100, 50, 50, 0.9f)]);

        Assert.Equal(3, tracker.Tracks.Count);
        Assert.Equal(3, tracker.BoxesAt(3, interval: 5).Count);
    }

    [Fact]
    public void A_track_remembers_its_best_score()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(0, [Face(100, confidence: 0.6f)]);
        tracker.Add(5, [Face(100, confidence: 0.95f)]);
        tracker.Add(10, [Face(100, confidence: 0.7f)]);

        Assert.Equal(0.95f, Assert.Single(tracker.Tracks).MaxConfidence);
    }

    [Fact]
    public void A_moving_plate_missed_for_a_couple_of_passes_stays_one_track_and_stays_blurred()
    {
        // A car crossing the frame at 12 px a frame: the plate is missed at frames 10 and 15, and by frame 20 it is
        // 3.6 plate-widths from where it was last seen, but exactly where its speed says it should be.
        var tracker = new RedactionTracker(maxMissed: 3);
        tracker.Add(0, [Plate(100)]);
        tracker.Add(5, [Plate(160)]);
        tracker.Add(10, []);
        tracker.Add(15, []);
        tracker.Add(20, [Plate(340)]);

        Assert.Single(tracker.Tracks);
        for (var frame = 5; frame <= 20; frame++)
        {
            var box = Assert.Single(tracker.BoxesAt(frame, interval: 5));
            Assert.Equal(100 + (12 * frame), box.X, 1);
        }
    }

    [Fact]
    public void A_weak_detection_continues_a_plate_but_cannot_start_one()
    {
        var tracker = new RedactionTracker(maxMissed: 0, startAt: _ => 0.5f);
        tracker.Add(0, [Plate(100, confidence: 0.9f)]);
        tracker.Add(5, [Plate(104, confidence: 0.35f), Plate(700, confidence: 0.35f)]);
        tracker.Add(10, [Plate(108, confidence: 0.9f)]);

        var track = Assert.Single(tracker.Tracks);
        Assert.Equal(3, track.Count);
        Assert.Equal(0.9f, track.MaxConfidence);
        Assert.DoesNotContain(tracker.BoxesAt(5, interval: 5), b => b.X > 600);
    }

    [Fact]
    public void Frames_must_be_added_in_order()
    {
        var tracker = new RedactionTracker(maxMissed: 1);
        tracker.Add(5, []);

        Assert.Throws<ArgumentException>(() => tracker.Add(5, []));
    }
}

public sealed class BoxMathTests
{
    [Fact]
    public void Padding_grows_the_box_by_a_fraction_on_every_side()
    {
        var box = new DetectionBox(RedactionTarget.Face, 100, 200, 40, 80, 0.9f);

        Assert.Equal((90, 180, 150, 300), BoxMath.Pad(box, 0.25, 1280, 720));
    }

    [Fact]
    public void Padding_is_clipped_to_the_frame()
    {
        var box = new DetectionBox(RedactionTarget.NumberPlate, 5, 700, 100, 30, 0.9f);

        Assert.Equal((0, 692, 130, 720), BoxMath.Pad(box, 0.25, 1280, 720));
    }

    [Fact]
    public void A_box_outside_the_frame_pads_to_nothing()
    {
        var box = new DetectionBox(RedactionTarget.Face, 2000, 2000, 10, 10, 0.9f);

        Assert.Equal((0, 0, 0, 0), BoxMath.Pad(box, 0.25, 1280, 720));
    }

    [Fact]
    public void IoU_of_identical_boxes_is_one_and_of_separate_boxes_zero()
    {
        var a = new DetectionBox(RedactionTarget.Face, 0, 0, 10, 10, 1);

        Assert.Equal(1, BoxMath.IoU(a, a), 3);
        Assert.Equal(0, BoxMath.IoU(a, a with { X = 20 }));
        Assert.Equal(1 / 3f, BoxMath.IoU(a, a with { X = 5 }), 3);
    }

    [Fact]
    public void Non_maximum_suppression_keeps_the_best_of_overlapping_boxes()
    {
        var best = new DetectionBox(RedactionTarget.Face, 0, 0, 10, 10, 0.9f);
        var kept = BoxMath.NonMaxSuppression([best with { X = 1, Confidence = 0.6f }, best, best with { X = 50 }], 0.3f);

        Assert.Equal(2, kept.Count);
        Assert.Equal(best, kept[0]);
    }
}

public sealed class FrameBlurTests
{
    private static RgbFrame Checkerboard(int width, int height)
    {
        var frame = new RgbFrame(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 0 : 255);
                var i = (y * frame.Stride) + (x * 3);
                frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = value;
            }
        }

        return frame;
    }

    [Fact]
    public void Inside_the_region_detail_is_destroyed_and_outside_it_is_untouched()
    {
        var frame = Checkerboard(200, 100);
        var before = (byte[])frame.Pixels.Clone();

        FrameBlur.Apply(frame, 40, 20, 160, 80);

        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                var i = (y * frame.Stride) + (x * 3);
                var inside = x is >= 40 and < 160 && y is >= 20 and < 80;
                if (inside)
                {
                    Assert.InRange(frame.Pixels[i], (byte)64, (byte)192);
                }
                else
                {
                    Assert.Equal(before[i], frame.Pixels[i]);
                }
            }
        }
    }

    [Theory]
    [InlineData(400, 100, 50)]
    [InlineData(100, 120, 34)]
    [InlineData(20, 10, 8)]
    public void Blocks_are_big_enough_that_only_a_few_fit_across(int width, int height, int expected)
    {
        Assert.Equal(expected, FrameBlur.BlockSize(width, height));
    }

    [Fact]
    public void Regions_partly_outside_the_frame_are_clipped()
    {
        var frame = Checkerboard(50, 50);

        FrameBlur.Apply(frame, -20, -20, 10, 10);
        FrameBlur.Apply(frame, 60, 60, 90, 90);
    }
}

public sealed class PpmReaderTests
{
    [Fact]
    public async Task Reads_consecutive_pictures_and_stops_at_the_end()
    {
        var stream = new MemoryStream();
        foreach (var (w, h, value) in new[] { (2, 1, (byte)7), (1, 2, (byte)9) })
        {
            stream.Write(Encoding.ASCII.GetBytes($"P6\n# comment\n{w} {h}\n255\n"));
            stream.Write(Enumerable.Repeat(value, w * h * 3).ToArray());
        }

        stream.Position = 0;
        var reader = new PpmReader(stream);

        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var end = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal((2, 1), (first!.Width, first.Height));
        Assert.All(first.Pixels, b => Assert.Equal(7, b));
        Assert.Equal((1, 2), (second!.Width, second.Height));
        Assert.All(second.Pixels, b => Assert.Equal(9, b));
        Assert.Null(end);
    }

    [Fact]
    public async Task Truncated_data_is_unreadable()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("P6\n4 4\n255\nabc"));

        await Assert.ThrowsAsync<UnreadableMediaException>(() => new PpmReader(stream).ReadAsync(TestContext.Current.CancellationToken));
    }
}

public sealed class PlateDetectorGeometryTests
{
    [Fact]
    public void Prior_boxes_match_the_models_4385_outputs_at_320x240()
    {
        Assert.Equal(4385 * 4, LpdYuNetPlateDetector.Priors(320, 240).Length);
    }

    [Fact]
    public void A_720p_frame_is_searched_in_a_2x2_grid_of_overlapping_tiles_reaching_the_far_edges()
    {
        Assert.Equal(704, LpdYuNetPlateDetector.TileSize(1280, 704));
        Assert.Equal(396, LpdYuNetPlateDetector.TileSize(720, 528));
        Assert.Equal(new[] { 0, 576 }, LpdYuNetPlateDetector.Offsets(1280, 704));
        Assert.Equal(new[] { 0, 324 }, LpdYuNetPlateDetector.Offsets(720, 396));
        Assert.Equal(new[] { 0 }, LpdYuNetPlateDetector.Offsets(400, 480));
        Assert.Equal(4, LpdYuNetPlateDetector.Offsets(2048, 704).Count);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(60.0, 30)]
    [InlineData(25.0, 25)]
    [InlineData(29.97, 29.97)]
    [InlineData(0.0, 30)]
    public void Output_frame_rate_is_the_source_rate_up_to_30(double? source, double expected)
    {
        Assert.Equal(expected, MediaRedactor.FrameRate(source), 3);
    }
}

public sealed class PersonDetectorTests
{
    [Fact]
    public void The_head_is_the_top_of_the_person_box()
    {
        var head = YoloxPersonDetector.HeadOf(new DetectionBox(RedactionTarget.Head, 100, 50, 40, 200, 0.8f));

        Assert.Equal((100f, 50f, 40f), (head.X, head.Y, head.Width));
        Assert.Equal(200 * YoloxPersonDetector.HeadFraction, head.Height, 3);
        Assert.Equal(0.8f, head.Confidence);
    }

    [Fact]
    public void The_plate_area_is_the_lower_middle_of_the_vehicle_box()
    {
        var area = YoloxPersonDetector.PlateAreaOf(new DetectionBox(RedactionTarget.PlateArea, 100, 200, 200, 100, 0.7f));

        Assert.Equal(RedactionTarget.PlateArea, area.Target);
        Assert.Equal(130f, area.X, 3);
        Assert.Equal(245f, area.Y, 3);
        Assert.Equal(140f, area.Width, 3);
        Assert.Equal(47f, area.Height, 3);
        Assert.Equal(0.7f, area.Confidence);
    }

    [Fact]
    public void The_letterbox_scales_the_frame_into_the_top_left_and_pads_with_grey()
    {
        var frame = new RgbFrame(1280, 720);
        Array.Fill(frame.Pixels, (byte)200);

        var (data, scale) = FrameOps.ToRgbLetterbox(frame, 640);

        Assert.Equal(0.5f, scale);
        Assert.Equal(3 * 640 * 640, data.Length);
        Assert.Equal(200f, data[0]);                      // inside the picture (red plane)
        Assert.Equal(200f, data[(359 * 640) + 639]);      // last row of the picture
        Assert.Equal(114f, data[(360 * 640) + 10]);       // below it: padding
        Assert.Equal(114f, data[(2 * 640 * 640) + (639 * 640) + 639]); // padding in the blue plane
    }
}

